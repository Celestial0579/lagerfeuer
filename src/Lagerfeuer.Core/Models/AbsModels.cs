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
    public List<System.Text.Json.JsonElement>? AudioFiles { get; set; }
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
