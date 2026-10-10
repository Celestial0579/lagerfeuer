using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Verwaltet den Audio-Cache: ein Ordner pro Buch (Item-ID), darin eine Datei pro Audiotrack
/// plus "session.json" mit Tracks/Kapiteln. Diese Struktur ist bewusst so gewählt, dass
/// v2 (Offline-Wiedergabe) direkt darauf aufbauen kann – vollständig gecachte Bücher sind
/// dann ohne Server abspielbar, angeheftete Downloads werden von der Bereinigung ausgenommen.
/// </summary>
public sealed class CacheManager(AbsApiClient api, string root)
{
    private const string SessionFileName = "session.json";

    public string Root { get; } = root;

    public string GetItemDirectory(string itemId) => Path.Combine(Root, Sanitize(itemId));

    public string GetTrackPath(string itemId, AudioTrack track)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StripQuery(track.ContentUrl))))[..16];
        var ext = track.Metadata?.Ext;
        if (string.IsNullOrWhiteSpace(ext)) ext = ExtensionFromMime(track.MimeType);
        if (!ext.StartsWith('.')) ext = "." + ext;
        return Path.Combine(GetItemDirectory(itemId), $"{track.Index:D3}_{key}{ext.ToLowerInvariant()}");
    }

    public bool IsTrackComplete(string itemId, AudioTrack track)
        => CachingHttpStream.IsFileComplete(GetTrackPath(itemId, track));

    public Task<CachingHttpStream> OpenTrackAsync(string itemId, AudioTrack track, CancellationToken ct = default)
        => CachingHttpStream.OpenAsync(api, track.ContentUrl, GetTrackPath(itemId, track), ct);

    /// <summary>Speichert die Session-Metadaten (Tracks, Kapitel) – Grundlage für Offline in v2.</summary>
    public void SaveSessionMetadata(PlaybackSession session)
    {
        try
        {
            var dir = GetItemDirectory(session.LibraryItemId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, SessionFileName), JsonSerializer.Serialize(session, AbsApiClient.Json));
        }
        catch (Exception ex)
        {
            Log.Error("Session-Metadaten konnten nicht gespeichert werden", ex);
        }
    }

    public long GetTotalSize()
    {
        if (!Directory.Exists(Root)) return 0;
        return new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    /// <summary>Löscht die am längsten nicht genutzten Bücher, bis das Limit eingehalten wird.</summary>
    public void EnforceLimit(long maxBytes, IReadOnlyCollection<string> protectedItemIds)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var protectedDirs = protectedItemIds.Select(GetItemDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dirs = new DirectoryInfo(Root).EnumerateDirectories()
                .Select(d =>
                {
                    var files = d.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                    return new
                    {
                        Dir = d,
                        Size = files.Sum(f => f.Length),
                        LastUse = files.Count == 0 ? d.LastWriteTimeUtc : files.Max(f => f.LastWriteTimeUtc),
                    };
                })
                .OrderBy(d => d.LastUse)
                .ToList();

            var total = dirs.Sum(d => d.Size);
            foreach (var d in dirs)
            {
                if (total <= maxBytes) break;
                if (protectedDirs.Contains(d.Dir.FullName)) continue;
                try
                {
                    d.Dir.Delete(recursive: true);
                    total -= d.Size;
                    Log.Info($"Cache bereinigt: {d.Dir.Name} ({d.Size / 1024 / 1024} MB)");
                }
                catch (IOException)
                {
                    // Datei in Benutzung – überspringen
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Cache-Bereinigung fehlgeschlagen", ex);
        }
    }

    public void Clear(IReadOnlyCollection<string> protectedItemIds) => EnforceLimit(0, protectedItemIds);

    private static string StripQuery(string url)
    {
        var i = url.IndexOf('?');
        return i < 0 ? url : url[..i];
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string ExtensionFromMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/x-m4a" or "audio/aac" => ".m4a",
        "audio/x-m4b" => ".m4b",
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/ogg" or "audio/opus" => ".ogg",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/webm" => ".webm",
        _ => ".bin",
    };
}

/// <summary>Lädt Cover einmalig herunter und hält sie auf der Platte vor.</summary>
public sealed class CoverCache(AbsApiClient api, string root)
{
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    public Task<string?> GetCoverPathAsync(string itemId)
    {
        var path = Path.Combine(root, itemId + ".jpg");
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        return _inflight.GetOrAdd(itemId, _ => DownloadAsync(itemId, path));
    }

    /// <summary>Verwirft ein Cover (z.B. wenn es am Server geändert wurde).</summary>
    public void Invalidate(string itemId)
    {
        try { File.Delete(Path.Combine(root, itemId + ".jpg")); } catch { /* egal */ }
    }

    private async Task<string?> DownloadAsync(string itemId, string path)
    {
        try
        {
            var bytes = await api.GetCoverAsync(itemId);
            if (bytes is null || bytes.Length == 0) return null;
            Directory.CreateDirectory(root);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error($"Cover {itemId} konnte nicht geladen werden", ex);
            return null;
        }
        finally
        {
            _inflight.TryRemove(itemId, out _);
        }
    }
}
