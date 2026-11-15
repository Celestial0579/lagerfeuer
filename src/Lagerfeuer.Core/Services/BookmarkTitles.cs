using System.Text;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Regeln für Lesezeichen, die ohne Oberfläche prüfbar sind: Titel beim Umbenennen bereinigen, die
/// Liste des Kontos nach Anlegen oder Umbenennen fortschreiben und entscheiden, ob die Taste zum Setzen
/// gerade Sinn ergibt.
/// </summary>
public static class BookmarkTitles
{
    /// <summary>Längster Titel, den Lagerfeuer schickt. Audiobookshelf selbst begrenzt nicht.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Bereinigt einen eingegebenen Titel: Zeilenumbrüche und Steuerzeichen werden zu Leerzeichen,
    /// mehrere Leerzeichen zu einem, Ränder fallen weg, mehr als <see cref="MaxLength"/> Zeichen werden
    /// abgeschnitten. Ergibt sich nichts oder nur der bisherige Titel, kommt null zurück: Dann gibt es
    /// nichts zu speichern.
    /// </summary>
    public static string? Normalize(string? input, string current)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var sb = new StringBuilder(input.Length);
        var space = false;
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        var title = sb.ToString();
        if (title.Length > MaxLength)
        {
            var cut = MaxLength;
            if (char.IsHighSurrogate(title[cut - 1])) cut--; // kein halbes Zeichen stehen lassen
            title = title[..cut].TrimEnd();
        }
        return title.Length == 0 || title == current ? null : title;
    }

    /// <summary>
    /// Trägt ein Lesezeichen aus der Antwort des Servers in die Liste ein. Lesezeichen haben keine eigene
    /// Id; Audiobookshelf erkennt sie an Buch und Zeit. Ein vorhandenes an derselben Stelle wird ersetzt.
    /// </summary>
    public static void Upsert(List<Bookmark> bookmarks, Bookmark bookmark)
    {
        bookmarks.RemoveAll(b => b.LibraryItemId == bookmark.LibraryItemId && b.Time == bookmark.Time);
        bookmarks.Add(bookmark);
    }

    /// <summary>
    /// Lesezeichen gehören in Audiobookshelf zum Buch, nicht zu einer Podcast-Folge. Bei einer Folge
    /// gäbe es weder eine Liste noch einen Weg zurück an die Stelle. Die Taste gibt es deshalb nur bei Büchern.
    /// </summary>
    public static bool CanSet(string? itemId, string? episodeId) => itemId is not null && episodeId is null;
}
