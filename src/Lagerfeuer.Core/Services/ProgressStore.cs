using System.Text.Json;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Hörfortschritt des Benutzers (pro Buch bzw. Podcast-Folge) für Fortschrittsanzeigen und den
/// Startpunkt der Wiedergabe. Wird auf der Platte gespiegelt, damit er auch offline stimmt.
/// </summary>
public sealed class ProgressStore
{
    private readonly Dictionary<string, MediaProgress> _byKey = new();
    private readonly string? _path;
    private readonly object _sync = new();

    public ProgressStore(string? persistPath = null)
    {
        _path = persistPath;
        Load();
    }

    /// <summary>Schlüssel des geänderten Eintrags (Item-ID bzw. "Item-ID/Folgen-ID").</summary>
    public event EventHandler<string>? Changed;

    public static string Key(string itemId, string? episodeId = null)
        => string.IsNullOrEmpty(episodeId) ? itemId : $"{itemId}/{episodeId}";

    public void Reset(IEnumerable<MediaProgress> progress)
    {
        lock (_sync)
        {
            _byKey.Clear();
            foreach (var p in progress) _byKey[Key(p.LibraryItemId, p.EpisodeId)] = p;
        }
        Save();
    }

    public MediaProgress? Get(string itemId, string? episodeId = null)
    {
        lock (_sync) return _byKey.GetValueOrDefault(Key(itemId, episodeId));
    }

    public IReadOnlyList<MediaProgress> All()
    {
        lock (_sync) return _byKey.Values.ToList();
    }

    public void Set(MediaProgress progress)
    {
        var key = Key(progress.LibraryItemId, progress.EpisodeId);
        lock (_sync) _byKey[key] = progress;
        Save();
        Changed?.Invoke(this, key);
    }

    public void Update(string itemId, double currentTime, double duration, string? episodeId = null)
    {
        var key = Key(itemId, episodeId);
        lock (_sync)
        {
            var p = _byKey.GetValueOrDefault(key) ?? new MediaProgress { LibraryItemId = itemId, EpisodeId = episodeId };
            p.CurrentTime = currentTime;
            p.Duration = duration;
            p.Progress = duration > 0 ? Math.Clamp(currentTime / duration, 0, 1) : 0;
            if (duration > 0 && currentTime >= duration - 1) p.IsFinished = true;
            p.LastUpdate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _byKey[key] = p;
        }
        Save();
        Changed?.Invoke(this, key);
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<MediaProgress>>(File.ReadAllText(_path), AbsApiClient.Json) ?? [];
            foreach (var p in list) _byKey[Key(p.LibraryItemId, p.EpisodeId)] = p;
        }
        catch (Exception ex)
        {
            Log.Error("Gespeicherter Fortschritt unlesbar", ex);
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_byKey.Values, AbsApiClient.Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Fortschritt konnte nicht gespeichert werden", ex);
        }
    }
}
