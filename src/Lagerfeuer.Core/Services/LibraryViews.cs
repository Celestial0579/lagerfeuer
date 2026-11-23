namespace Lagerfeuer.Services;

/// <summary>
/// Welche Ansichten eine Bibliothek anbietet. Buch-Bibliotheken haben Bücher, Serien, Autoren und Sammlungen (#35).
/// Podcast-Bibliotheken haben nur ihre Podcasts. Ein einzelner Reiter wäre dort sinnlos und hieß früher
/// fälschlich „Bücher“ (#43), deshalb gibt es für sie keine Reiter.
/// </summary>
public static class LibraryViews
{
    public const string Books = "books";
    public const string Series = "series";
    public const string Authors = "authors";
    public const string Collections = "collections";

    public static IReadOnlyList<string> For(string? mediaType)
        => mediaType == "podcast" ? [] : [Books, Series, Authors, Collections];

    /// <summary>Die Reiterleiste erscheint nur, wenn es etwas zu wählen gibt.</summary>
    public static bool ShowSelector(string? mediaType) => For(mediaType).Count > 1;
}
