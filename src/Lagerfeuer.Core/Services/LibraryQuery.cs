using System.Text;

namespace Lagerfeuer.Services;

/// <summary>
/// Sortierung und Filter für GET /api/libraries/:id/items.
/// Filter: "&lt;gruppe&gt;.&lt;wert&gt;", der Wert Base64-kodiert und zusätzlich URL-kodiert
/// (der Server dekodiert decodeURIComponent → base64; ein rohes "+" würde sonst zum Leerzeichen).
/// </summary>
public sealed record LibraryQuery(string Sort = LibraryQuery.SortTitle, bool Descending = false, string? FilterGroup = null, string? FilterValue = null)
{
    public const string SortTitle = "media.metadata.title";
    public const string SortAuthor = "media.metadata.authorNameLF";
    public const string SortAdded = "addedAt";
    public const string SortPublished = "media.metadata.publishedYear";
    public const string SortDuration = "media.duration";
    public const string SortProgress = "progress";
    /// <summary>Podcasts kennen keinen authorNameLF.</summary>
    public const string SortPodcastAuthor = "media.metadata.author";

    public static readonly LibraryQuery Default = new();

    public string ToQueryString()
    {
        var query = $"sort={Uri.EscapeDataString(Sort)}";
        if (Descending) query += "&desc=1";
        if (!string.IsNullOrEmpty(FilterGroup) && !string.IsNullOrEmpty(FilterValue))
            query += "&filter=" + EncodeFilter(FilterGroup, FilterValue);
        return query;
    }

    public static string EncodeFilter(string group, string value)
        => $"{group}.{Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)))}";
}
