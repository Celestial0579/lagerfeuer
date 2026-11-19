namespace Lagerfeuer.Services;

/// <summary>
/// Merkt sich, woher die Meldung in der Leiste unten stammt (z. B. „Weiterhören“). Gelingt das Laden
/// dieser Herkunft später, schließt die Leiste die Meldung wieder (#40). Steht dort inzwischen eine
/// andere Meldung oder eine ohne Herkunft, bleibt sie stehen.
/// </summary>
public sealed class MessageOrigin
{
    private string? _origin;
    private string? _message;

    /// <summary>Eine neue Meldung steht in der Leiste. Ohne Herkunft schließt sie nur der Nutzer.</summary>
    public void Shown(string message, string? origin)
    {
        _message = message;
        _origin = origin;
    }

    /// <summary>
    /// Das Laden der Herkunft <paramref name="origin"/> ist gelungen. Ergibt true, wenn die Leiste gerade
    /// genau deren Meldung zeigt und sie schließen soll.
    /// </summary>
    public bool Resolve(string origin, bool isOpen, string? currentMessage)
    {
        if (!isOpen || _origin != origin || _message != currentMessage) return false;
        _origin = null;
        _message = null;
        return true;
    }
}

/// <summary>Herkünfte der Meldungen beim Laden der Seiten.</summary>
public static class MessageOrigins
{
    public const string Home = "home";
    public const string Library = "library";
    public const string Series = "series";
    public const string Authors = "authors";
    public const string Search = "search";
    public const string SeriesPage = "series-page";
    public const string AuthorPage = "author-page";
    public const string Book = "book";
    public const string Podcast = "podcast";
    public const string Playlists = "playlists";
    public const string Playlist = "playlist";
    public const string Collections = "collections";
    public const string Collection = "collection";
}
