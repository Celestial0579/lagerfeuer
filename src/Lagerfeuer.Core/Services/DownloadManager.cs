using System.Collections.Concurrent;
using System.Threading.Channels;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

public enum DownloadStatus { Queued, Downloading, Completed, Failed, Canceled }

public sealed class DownloadState
{
    public required string ItemId { get; init; }
    /// <summary>Bei einer Podcast-Folge (#76) die Folge, sonst null.</summary>
    public string? EpisodeId { get; init; }
    public string Key => DownloadManager.Key(ItemId, EpisodeId);
    public string Title { get; internal set; } = "";
    public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;
    public double Fraction { get; internal set; }
    public string? Error { get; internal set; }
    internal CancellationTokenSource Cancellation { get; } = new();

    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading;
}

/// <summary>
/// Lädt Bücher und Podcast-Folgen (#76) vollständig herunter und heftet sie an (Offline-Wiedergabe). Nutzt denselben
/// blockweisen Cache wie das Streaming – bereits gehörte Teile werden nicht erneut übertragen.
/// Downloads laufen nacheinander.
/// </summary>
public sealed class DownloadManager
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly AbsApiClient _api;
    private readonly CacheManager _cache;
    private readonly CoverCache? _covers;
    private readonly Channel<DownloadState> _queue = Channel.CreateUnbounded<DownloadState>();
    private readonly ConcurrentDictionary<string, DownloadState> _states = new();
    private readonly TimeSpan _pollInterval;

    public DownloadManager(AbsApiClient api, CacheManager cache, CoverCache? covers, TimeSpan? pollInterval = null)
    {
        _api = api;
        _cache = cache;
        _covers = covers;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _ = Task.Run(WorkerAsync);
    }

    /// <summary>Zustandsänderung eines Downloads (Hintergrund-Thread).</summary>
    public event EventHandler<DownloadState>? Changed;

    /// <summary>Schlüssel eines Downloads: Item-ID, bei einer Folge „Item-ID/Folgen-ID“.</summary>
    public static string Key(string itemId, string? episodeId = null) => ProgressStore.Key(itemId, episodeId);

    /// <summary>Zerlegt einen <see cref="Key"/> wieder in Item- und Folgen-ID.</summary>
    public static (string ItemId, string? EpisodeId) ParseKey(string key)
    {
        var i = key.IndexOf('/');
        return i < 0 ? (key, null) : (key[..i], key[(i + 1)..]);
    }

    public DownloadState? Get(string itemId, string? episodeId = null) => _states.GetValueOrDefault(Key(itemId, episodeId));

    public IReadOnlyList<DownloadState> All => _states.Values.ToList();

    /// <summary>
    /// Stellt ein Buch oder eine Folge in die Warteschlange. Kennungen vom Server nur schlicht: Sie bilden den
    /// Schlüssel und über <see cref="CachePaths.SafeId"/> Dateinamen. Eine andere Kennung ergibt gleich einen
    /// fehlgeschlagenen Download statt einer Ausnahme im Klick der Oberfläche.
    /// </summary>
    public DownloadState Enqueue(string itemId, string? title = null, string? episodeId = null)
    {
        if (!PlaylistRules.IsPlainId(itemId) || (episodeId is not null && !PlaylistRules.IsPlainId(episodeId)))
            return new DownloadState { ItemId = itemId, EpisodeId = episodeId, Title = title ?? "", Status = DownloadStatus.Failed, Error = "Ungültige Kennung vom Server." };
        var key = Key(itemId, episodeId);
        if (_states.TryGetValue(key, out var existing) && existing.IsActive) return existing;
        var state = new DownloadState { ItemId = itemId, EpisodeId = episodeId, Title = title ?? "" };
        _states[key] = state;
        _queue.Writer.TryWrite(state);
        Changed?.Invoke(this, state);
        return state;
    }

    public void Cancel(string itemId, string? episodeId = null)
    {
        if (_states.TryGetValue(Key(itemId, episodeId), out var state) && state.IsActive) state.Cancellation.Cancel();
    }

    /// <summary>
    /// Entfernt einen Download: lokale Dateien löschen, die Anheftung erst nach den Audiodaten. Bei einer Folge nur
    /// deren Dateien, der Podcast-Ordner mit anderen Folgen bleibt. Lässt sich eine Datei nicht löschen (in
    /// Benutzung, Zugriff verweigert), bleibt der Download angeheftet und mit seinen Daten bestehen (#85); was
    /// schon gelöscht ist, lädt ein neuer Download wieder.
    /// </summary>
    /// <returns>Null, wenn der Download entfernt ist, sonst ein kurzer Grund ohne Pfade für die Meldung.</returns>
    public string? Remove(string itemId, string? episodeId = null)
    {
        Cancel(itemId, episodeId);
        try
        {
            if (episodeId is null) _cache.DeleteItem(itemId);
            else _cache.DeleteEpisode(itemId, episodeId);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Download {Key(itemId, episodeId)} konnte nicht vollständig gelöscht werden", ex);
            return UpdateService.FailureReason(ex);
        }
        finally
        {
            _states.TryRemove(Key(itemId, episodeId), out _);
        }
    }

    /// <summary>Meldung, wenn <see cref="Remove"/> einen Download nicht vollständig löschen konnte.</summary>
    public static string RemoveFailedMessage(string? title, string reason)
        => $"{(string.IsNullOrWhiteSpace(title) ? "Der Download" : $"„{title}“")} konnte nicht vollständig gelöscht werden: {reason}. " +
           "Versuch es später noch einmal.";

    private async Task WorkerAsync()
    {
        await foreach (var state in _queue.Reader.ReadAllAsync())
        {
            if (state.Cancellation.IsCancellationRequested)
            {
                Set(state, DownloadStatus.Canceled);
                continue;
            }
            try
            {
                await DownloadAsync(state, state.Cancellation.Token);
                Set(state, DownloadStatus.Completed, 1);
            }
            catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
            {
                // Teile bleiben als normaler Cache erhalten
                if (state.EpisodeId is { } episodeId) _cache.SetEpisodePinned(state.ItemId, episodeId, false);
                else _cache.SetPinned(state.ItemId, false);
                Set(state, DownloadStatus.Canceled);
            }
            catch (Exception ex)
            {
                Log.Error($"Download von {state.Key} fehlgeschlagen", ex);
                state.Error = ex.Message;
                Set(state, DownloadStatus.Failed);
            }
        }
    }

    private async Task DownloadAsync(DownloadState state, CancellationToken ct)
    {
        Set(state, DownloadStatus.Downloading, 0);

        var item = await _api.GetItemAsync(state.ItemId, ct);
        LocalItem local;
        if (state.EpisodeId is { } episodeId)
        {
            var episode = item.Media.Episodes?.FirstOrDefault(e => e.Id == episodeId)
                ?? throw new InvalidOperationException("Diese Folge gibt es auf dem Server nicht mehr.");
            local = LocalItem.FromEpisode(item, episode);
            if (local.Tracks.Count == 0)
                throw new InvalidOperationException("Diese Folge enthält keine Audiodatei.");
            state.Title = local.Title;
            _cache.SaveLocalEpisode(local);
            _cache.SetEpisodePinned(local.LibraryItemId, episodeId, true);
        }
        else
        {
            local = LocalItem.FromLibraryItem(item);
            if (local.Tracks.Count == 0)
                throw new InvalidOperationException("Dieses Buch enthält keine Audiodateien.");
            state.Title = local.Title;
            _cache.SaveLocalItem(local);
            _cache.SetPinned(local.LibraryItemId, true);
        }
        if (_covers is not null) _ = _covers.GetCoverPathAsync(local.LibraryItemId);

        var total = local.Tracks.Sum(t => Math.Max(t.Duration, 1));
        var done = 0.0;
        foreach (var track in local.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            var weight = Math.Max(track.Duration, 1);
            if (!_cache.IsTrackComplete(local.LibraryItemId, track))
            {
                // Öffnen startet den Hintergrund-Download der ganzen Datei.
                using var stream = await _cache.OpenTrackAsync(local.LibraryItemId, track, ct);
                var lastFraction = -1.0;
                var lastProgress = DateTime.UtcNow;
                while (!stream.IsPersisted)
                {
                    await Task.Delay(_pollInterval, ct);
                    var fraction = stream.CachedFraction;
                    if (fraction > lastFraction)
                    {
                        lastFraction = fraction;
                        lastProgress = DateTime.UtcNow;
                        Set(state, DownloadStatus.Downloading, (done + weight * fraction) / total);
                    }
                    else if (DateTime.UtcNow - lastProgress > StallTimeout)
                    {
                        throw new IOException("Der Download kommt nicht voran – ist der Server erreichbar?");
                    }
                }
            }
            done += weight;
            Set(state, DownloadStatus.Downloading, done / total);
        }
    }

    private void Set(DownloadState state, DownloadStatus status, double? fraction = null)
    {
        state.Status = status;
        if (fraction is { } f) state.Fraction = Math.Clamp(f, 0, 1);
        Changed?.Invoke(this, state);
    }
}
