using System.Text.Json.Serialization;

namespace Lagerfeuer.Models;

// DTOs für die Audiobookshelf-REST-API. Unbekannte Felder werden ignoriert.

public sealed class ServerStatus
{
    public bool IsInit { get; set; }
    public string? ServerVersion { get; set; }
    public List<string> AuthMethods { get; set; } = [];
    public AuthFormData? AuthFormData { get; set; }

    [JsonIgnore] public bool SupportsLocal => AuthMethods.Count == 0 || AuthMethods.Contains("local");
    [JsonIgnore] public bool SupportsOpenId => AuthMethods.Contains("openid");
}

public sealed class AuthFormData
{
    public string? AuthOpenIDButtonText { get; set; }
    public bool AuthOpenIDAutoLaunch { get; set; }
}

public sealed class LoginResponse
{
    public AbsUser User { get; set; } = new();
    public string? UserDefaultLibraryId { get; set; }
}

public sealed class AbsUser
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Legacy-Token (ABS &lt; 2.26). Neuere Server liefern AccessToken/RefreshToken.</summary>
    public string? Token { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public List<MediaProgress> MediaProgress { get; set; } = [];
}

public sealed class MediaProgress
{
    public string Id { get; set; } = "";
    public string LibraryItemId { get; set; } = "";
    public string? EpisodeId { get; set; }
    public double Duration { get; set; }
    public double Progress { get; set; }
    public double CurrentTime { get; set; }
    public bool IsFinished { get; set; }
    public long LastUpdate { get; set; }
}

public sealed class LibrariesResponse
{
    public List<Library> Libraries { get; set; } = [];
}

public sealed class Library
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MediaType { get; set; } = "book";
}

public sealed class LibraryItemsResponse
{
    public List<LibraryItem> Results { get; set; } = [];
    public int Total { get; set; }
}

public sealed class ItemsInProgressResponse
{
    public List<LibraryItem> LibraryItems { get; set; } = [];
}

public sealed class SearchResponse
{
    public List<SearchBookResult> Book { get; set; } = [];
}

public sealed class SearchBookResult
{
    public LibraryItem LibraryItem { get; set; } = new();
}

public sealed class LibraryItem
{
    public string Id { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string MediaType { get; set; } = "book";
    public long UpdatedAt { get; set; }
    public BookMedia Media { get; set; } = new();
    public MediaProgress? UserMediaProgress { get; set; }
}

public sealed class BookMedia
{
    public BookMetadata Metadata { get; set; } = new();
    public string? CoverPath { get; set; }
    public double Duration { get; set; }
    public List<Chapter> Chapters { get; set; } = [];
    /// <summary>Nur im erweiterten Item (expanded=1) befüllt; leer z.B. bei reinen E-Books.</summary>
    public List<AudioFile>? AudioFiles { get; set; }
}

public sealed class BookMetadata
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? AuthorName { get; set; }
    public string? NarratorName { get; set; }
    public string? SeriesName { get; set; }
    public string? Description { get; set; }
    public string? PublishedYear { get; set; }
    public List<NamedRef> Authors { get; set; } = [];
    public List<string> Narrators { get; set; } = [];

    [JsonIgnore]
    public string DisplayAuthor => !string.IsNullOrWhiteSpace(AuthorName)
        ? AuthorName!
        : string.Join(", ", Authors.Select(a => a.Name));

    [JsonIgnore]
    public string DisplayNarrator => !string.IsNullOrWhiteSpace(NarratorName)
        ? NarratorName!
        : string.Join(", ", Narrators);
}

public sealed class NamedRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Chapter
{
    public int Id { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public string Title { get; set; } = "";
}

public sealed class PlaybackSession
{
    public string Id { get; set; } = "";
    public string LibraryItemId { get; set; } = "";
    public string? EpisodeId { get; set; }
    public string? DisplayTitle { get; set; }
    public string? DisplayAuthor { get; set; }
    public double Duration { get; set; }
    public double CurrentTime { get; set; }
    public List<AudioTrack> AudioTracks { get; set; } = [];
    public List<Chapter> Chapters { get; set; } = [];
}

public sealed class AudioTrack
{
    public int Index { get; set; }
    public double StartOffset { get; set; }
    public double Duration { get; set; }
    public string? Title { get; set; }
    public string ContentUrl { get; set; } = "";
    public string? MimeType { get; set; }
    public AudioTrackMetadata? Metadata { get; set; }
}

public sealed class AudioTrackMetadata
{
    public string? Filename { get; set; }
    public string? Ext { get; set; }
    public long Size { get; set; }
}

public sealed class MediaProgressResponse
{
    public List<MediaProgress> MediaProgress { get; set; } = [];
}

/// <summary>Audiodatei eines Buchs (im erweiterten Item unter media.audioFiles).</summary>
public sealed class AudioFile
{
    public int Index { get; set; }
    public string Ino { get; set; } = "";
    public double Duration { get; set; }
    public string? MimeType { get; set; }
    /// <summary>Vom Admin ausgeschlossen – wird nicht abgespielt.</summary>
    public bool Exclude { get; set; }
    public AudioTrackMetadata? Metadata { get; set; }
}

/// <summary>
/// Lokal gespeicherte Daten eines Buchs ("item.json" im Cache-Ordner): alles, was zum Abspielen
/// ohne Server nötig ist. Wird beim Streamen und beim Herunterladen angelegt.
/// </summary>
public sealed class LocalItem
{
    public string LibraryItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? Narrator { get; set; }
    public string? Description { get; set; }
    public double Duration { get; set; }
    public List<AudioTrack> Tracks { get; set; } = [];
    public List<Chapter> Chapters { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    public static LocalItem FromSession(PlaybackSession session) => new()
    {
        LibraryItemId = session.LibraryItemId,
        Title = session.DisplayTitle ?? "",
        Author = session.DisplayAuthor,
        Duration = session.Duration > 0 ? session.Duration : session.AudioTracks.Sum(t => t.Duration),
        Tracks = session.AudioTracks.OrderBy(t => t.StartOffset).ToList(),
        Chapters = session.Chapters,
    };

    /// <summary>
    /// Baut die Trackliste aus einem erweiterten Item – wie der Server beim Direct Play
    /// (Book.getTracklist: nicht ausgeschlossene Dateien in Reihenfolge, Startzeiten aufsummiert).
    /// </summary>
    public static LocalItem FromLibraryItem(LibraryItem item)
    {
        var offset = 0.0;
        var tracks = new List<AudioTrack>();
        foreach (var file in (item.Media.AudioFiles ?? []).Where(f => !f.Exclude).OrderBy(f => f.Index))
        {
            tracks.Add(new AudioTrack
            {
                Index = file.Index,
                StartOffset = offset,
                Duration = file.Duration,
                Title = file.Metadata?.Filename,
                ContentUrl = $"/api/items/{item.Id}/file/{file.Ino}",
                MimeType = file.MimeType,
                Metadata = file.Metadata,
            });
            offset += file.Duration;
        }

        var meta = item.Media.Metadata;
        return new LocalItem
        {
            LibraryItemId = item.Id,
            Title = meta.Title ?? "",
            Author = meta.DisplayAuthor,
            Narrator = meta.DisplayNarrator,
            Description = meta.Description,
            Duration = item.Media.Duration > 0 ? item.Media.Duration : offset,
            Tracks = tracks,
            Chapters = item.Media.Chapters,
        };
    }

    /// <summary>Session für die Wiedergabe ohne Server.</summary>
    public PlaybackSession ToOfflineSession(string sessionId, double currentTime) => new()
    {
        Id = sessionId,
        LibraryItemId = LibraryItemId,
        DisplayTitle = Title,
        DisplayAuthor = Author,
        Duration = Duration,
        CurrentTime = currentTime,
        AudioTracks = Tracks.Select(t => t).ToList(),
        Chapters = Chapters,
    };
}
/// <summary>
/// Offline entstandene Wiedergabe-Session im Format von POST /api/session/local(-all).
/// Zeiten in Sekunden, Zeitstempel in Millisekunden seit 1970.
/// </summary>
public sealed class LocalSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string LibraryItemId { get; set; } = "";
    public string? EpisodeId { get; set; }
    public string MediaType { get; set; } = "book";
    public string? DisplayTitle { get; set; }
    public string? DisplayAuthor { get; set; }
    public double Duration { get; set; }
    /// <summary>3 = lokal (PlayMethod.LOCAL).</summary>
    public int PlayMethod { get; set; } = 3;
    public string MediaPlayer { get; set; } = "WinUI MediaPlayer";
    public double TimeListening { get; set; }
    public double StartTime { get; set; }
    public double CurrentTime { get; set; }
    public long StartedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public DeviceInfo? DeviceInfo { get; set; }
}

public sealed class DeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public string Manufacturer { get; set; } = "Microsoft";
    public string Model { get; set; } = "Windows";
    public string? DeviceName { get; set; }
}

public sealed class LocalSessionSyncResponse
{
    public List<LocalSessionResult> Results { get; set; } = [];
}

public sealed class LocalSessionResult
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
    public bool ProgressSynced { get; set; }
    public string? Error { get; set; }
}
