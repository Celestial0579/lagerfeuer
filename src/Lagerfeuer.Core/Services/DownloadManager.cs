using System.Collections.Concurrent;
using System.Threading.Channels;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

public enum DownloadStatus { Queued, Downloading, Completed, Failed, Canceled }

public sealed class DownloadState
{
    public required string ItemId { get; init; }
    public string Title { get; internal set; } = "";
    public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;
    public double Fraction { get; internal set; }
    public string? Error { get; internal set; }
    internal CancellationTokenSource Cancellation { get; } = new();

    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading;
}

/// <summary>
/// Lädt Bücher vollständig herunter und heftet sie an (Offline-Wiedergabe). Nutzt denselben
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

    public DownloadState? Get(string itemId) => _states.GetValueOrDefault(itemId);

    public IReadOnlyList<DownloadState> All => _states.Values.ToList();

    public DownloadState Enqueue(string itemId, string? title = null)
    {
        if (_states.TryGetValue(itemId, out var existing) && existing.IsActive) return existing;
        var state = new DownloadState { ItemId = itemId, Title = title ?? "" };
        _states[itemId] = state;
        _queue.Writer.TryWrite(state);
        Changed?.Invoke(this, state);
        return state;
    }

    public void Cancel(string itemId)
    {
        if (_states.TryGetValue(itemId, out var state) && state.IsActive) state.Cancellation.Cancel();
    }

    /// <summary>Entfernt einen Download: Anheftung lösen und lokale Dateien löschen.</summary>
    public void Remove(string itemId)
    {
        Cancel(itemId);
        _cache.SetPinned(itemId, false);
        try { _cache.DeleteItem(itemId); }
        catch (IOException ex) { Log.Error($"Download {itemId} konnte nicht vollständig gelöscht werden (in Benutzung?)", ex); }
        _states.TryRemove(itemId, out _);
    }

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
                _cache.SetPinned(state.ItemId, false); // Teile bleiben als normaler Cache erhalten
                Set(state, DownloadStatus.Canceled);
            }
            catch (Exception ex)
            {
                Log.Error($"Download von {state.ItemId} fehlgeschlagen", ex);
                state.Error = ex.Message;
                Set(state, DownloadStatus.Failed);
            }
        }
    }

    private async Task DownloadAsync(DownloadState state, CancellationToken ct)
    {
        Set(state, DownloadStatus.Downloading, 0);

        var item = await _api.GetItemAsync(state.ItemId, ct);
        var local = LocalItem.FromLibraryItem(item);
        if (local.Tracks.Count == 0)
            throw new InvalidOperationException("Dieses Buch enthält keine Audiodateien.");

        state.Title = local.Title;
        _cache.SaveLocalItem(local);
        _cache.SetPinned(local.LibraryItemId, true);
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
