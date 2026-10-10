using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Verwaltet den Audio-Cache: ein Ordner pro Buch (Item-ID), darin eine Datei pro Audiotrack (+ .map),
/// "item.json" mit allem, was zum Abspielen ohne Server nötig ist, und – bei Downloads – die Markierung
/// ".pinned". Angeheftete Bücher nimmt die automatische Bereinigung aus.
/// </summary>
public sealed class CacheManager(AbsApiClient api, string root)
{
    private const string ItemFileName = "item.json";
    private const string PinnedFileName = ".pinned";

    public string Root { get; } = root;

    public string GetItemDirectory(string itemId) => Path.Combine(Root, Sanitize(itemId));

    public string GetTrackPath(string itemId, AudioTrack track)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CacheKey(track.ContentUrl))))[..16];
        var ext = track.Metadata?.Ext;
        if (string.IsNullOrWhiteSpace(ext)) ext = ExtensionFromMime(track.MimeType);
        if (!ext.StartsWith('.')) ext = "." + ext;
        return Path.Combine(GetItemDirectory(itemId), $"{track.Index:D3}_{key}{ext.ToLowerInvariant()}");
    }

    public bool IsTrackComplete(string itemId, AudioTrack track)
        => CachingHttpStream.IsFileComplete(GetTrackPath(itemId, track));

    public Task<CachingHttpStream> OpenTrackAsync(string itemId, AudioTrack track, CancellationToken ct = default)
        => CachingHttpStream.OpenAsync(api, track.ContentUrl, GetTrackPath(itemId, track), ct);

    // ---------- Lokale Items (Offline) ----------

    public void SaveLocalItem(LocalItem item)
    {
        try
        {
            var dir = GetItemDirectory(item.LibraryItemId);
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, ItemFileName + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(item, AbsApiClient.Json));
            File.Move(tmp, Path.Combine(dir, ItemFileName), overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Lokale Buchdaten konnten nicht gespeichert werden", ex);
        }
    }

    public LocalItem? GetLocalItem(string itemId)
    {
        try
        {
            var path = Path.Combine(GetItemDirectory(itemId), ItemFileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<LocalItem>(File.ReadAllText(path), AbsApiClient.Json) : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Lokale Buchdaten von {itemId} unlesbar", ex);
            return null;
        }
    }

    /// <summary>Alle Bücher mit lokalen Daten (heruntergeladen oder beim Streamen zwischengespeichert).</summary>
    public IReadOnlyList<LocalItem> ListLocalItems()
    {
        if (!Directory.Exists(Root)) return [];
        return new DirectoryInfo(Root).EnumerateDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, ItemFileName)))
            .Select(d => GetLocalItem(d.Name))
            .OfType<LocalItem>()
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool IsPinned(string itemId) => File.Exists(Path.Combine(GetItemDirectory(itemId), PinnedFileName));

    public void SetPinned(string itemId, bool pinned)
    {
        var dir = GetItemDirectory(itemId);
        var marker = Path.Combine(dir, PinnedFileName);
        if (pinned)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        else if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    /// <summary>Anteil der lokal vorhandenen Audiodaten (nach Dauer gewichtet, 0 bis 1).</summary>
    public double GetLocalFraction(LocalItem item)
    {
        var total = item.Tracks.Sum(t => Math.Max(t.Duration, 1));
        if (total <= 0) return 0;
        return item.Tracks.Sum(t => Math.Max(t.Duration, 1) * CachingHttpStream.GetCachedFraction(GetTrackPath(item.LibraryItemId, t))) / total;
    }

    /// <summary>Ist das Buch vollständig lokal vorhanden (also ohne Server abspielbar)?</summary>
    public bool IsAvailableOffline(LocalItem item)
        => item.Tracks.Count > 0 && item.Tracks.All(t => IsTrackComplete(item.LibraryItemId, t));

    public long GetItemSize(string itemId)
    {
        var dir = new DirectoryInfo(GetItemDirectory(itemId));
        return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    public void DeleteItem(string itemId)
    {
        var dir = GetItemDirectory(itemId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // ---------- Größe & Bereinigung ----------

    public long GetTotalSize()
    {
        if (!Directory.Exists(Root)) return 0;
        return new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    /// <summary>
    /// Löscht die am längsten nicht genutzten Bücher, bis das Limit eingehalten wird.
    /// Angeheftete (heruntergeladene) und geschützte Bücher bleiben immer erhalten.
    /// </summary>
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
                        Pinned = files.Any(f => f.Name == PinnedFileName),
                    };
                })
                .OrderBy(d => d.LastUse)
                .ToList();

            var total = dirs.Sum(d => d.Size);
            foreach (var d in dirs)
            {
                if (total <= maxBytes) break;
                if (d.Pinned || protectedDirs.Contains(d.Dir.FullName)) continue;
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

    /// <summary>Leert den Streaming-Cache; Downloads bleiben erhalten.</summary>
    public void Clear(IReadOnlyCollection<string> protectedItemIds) => EnforceLimit(0, protectedItemIds);

    /// <summary>
    /// Schlüssel einer Audiodatei unabhängig von einem Server-Unterpfad
    /// ("/abs/api/items/x/file/1" und "/api/items/x/file/1" sind dieselbe Datei).
    /// </summary>
    private static string CacheKey(string contentUrl)
    {
        var path = StripQuery(contentUrl);
        var i = path.IndexOf("/api/items/", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? path[i..] : path;
    }
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
