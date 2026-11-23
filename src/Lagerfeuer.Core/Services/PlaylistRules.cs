using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Regeln für Wiedergabelisten (#36), die ohne Oberfläche prüfbar sind. Die Daten kommen von einem
/// Server, dem Lagerfeuer nicht blind vertraut (vgl. GHSA-wg65, GHSA-9cq7): Namen werden nur als
/// bereinigter, gekürzter Text gezeigt, Einträge nur mit schlichten IDs übernommen.
/// </summary>
public static class PlaylistRules
{
    /// <summary>Längster Name, den Lagerfeuer schickt oder anzeigt.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Längste Beschreibung, die Lagerfeuer anzeigt.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>Bereinigt einen eingegebenen Namen. Null heißt: nichts Neues zu speichern.</summary>
    public static string? NormalizeName(string? input, string current = "")
    {
        var name = Clean(input, MaxNameLength);
        return name.Length == 0 || name == current ? null : name;
    }

    /// <summary>Name zur Anzeige: bereinigt, gekürzt, nie leer.</summary>
    public static string DisplayName(Playlist p)
    {
        var name = Clean(p.Name, MaxNameLength);
        return name.Length == 0 ? "Ohne Namen" : name;
    }

    /// <summary>Beschreibung zur Anzeige: Zeilenumbrüche bleiben, sonstige Steuerzeichen nicht.</summary>
    public static string DisplayDescription(Playlist p)
    {
        if (string.IsNullOrWhiteSpace(p.Description)) return "";
        var chars = p.Description.Where(c => c == '\n' || !char.IsControl(c)).ToArray();
        return Cut(new string(chars).Trim(), MaxDescriptionLength);
    }

    /// <summary>Titel eines Eintrags: Folge bzw. Buch, sonst ein Hinweis.</summary>
    public static string DisplayTitle(PlaylistItem item)
    {
        var raw = item.EpisodeId is not null ? item.Episode?.Title : item.LibraryItem?.Media.Metadata.Title;
        var title = Clean(raw, 300);
        return title.Length > 0 ? title : IsAvailable(item) ? "Ohne Titel" : "Nicht mehr verfügbar";
    }

    /// <summary>Untertitel eines Eintrags: Podcast bzw. Autor.</summary>
    public static string DisplaySubtitle(PlaylistItem item)
        => Clean(item.EpisodeId is not null ? item.LibraryItem?.Media.Metadata.Title : item.LibraryItem?.Media.Metadata.DisplayAuthor, 300);

    /// <summary>Ob der Eintrag abspielbar ist: Buch bzw. Folge gibt es noch, die IDs sind schlicht.</summary>
    public static bool IsAvailable(PlaylistItem item)
        => item.LibraryItem is not null && IsPlainId(item.LibraryItemId) &&
           (item.EpisodeId is null ? !item.LibraryItem.IsPodcast : item.Episode is not null && IsPlainId(item.EpisodeId));

    /// <summary>IDs von Audiobookshelf sind UUIDs bzw. kurze Kennungen. Alles andere wird nicht verwendet.</summary>
    public static bool IsPlainId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Enthält die Liste dieses Buch bzw. diese Folge?</summary>
    public static bool Contains(Playlist p, string itemId, string? episodeId)
        => p.Items.Any(i => i.LibraryItemId == itemId && i.EpisodeId == episodeId);

    /// <summary>
    /// Verschiebt einen Eintrag um <paramref name="delta"/> Plätze und liefert die neue Reihenfolge,
    /// die per PATCH an den Server geht. Null, wenn sich nichts ändert.
    /// </summary>
    public static List<PlaylistItem>? Move(IReadOnlyList<PlaylistItem> items, int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= items.Count || target < 0 || target >= items.Count || delta == 0) return null;
        var list = items.ToList();
        var moved = list[index];
        list.RemoveAt(index);
        list.Insert(target, moved);
        return list;
    }

    /// <summary>Was per PATCH als neue Reihenfolge geschickt wird: nur die beiden IDs je Eintrag.</summary>
    public static object[] OrderBody(IEnumerable<PlaylistItem> items)
        => items.Select(i => i.EpisodeId is null
            ? (object)new { libraryItemId = i.LibraryItemId }
            : new { libraryItemId = i.LibraryItemId, episodeId = i.EpisodeId }).ToArray();

    /// <summary>Höchstens so viele Bücher und Folgen legt „Liste herunterladen“ auf einmal in die Downloads.</summary>
    public const int MaxDownloadEntries = 500;

    /// <summary>
    /// Was „Liste herunterladen“ in die Downloads legt (#62): jedes verfügbare Buch und jede verfügbare Podcast-Folge
    /// (#76) der Liste einmal, in ihrer Reihenfolge, nur mit schlichten IDs. Abgelegt wird wie bei jedem Download
    /// unter Item- bzw. Folgen-ID (<see cref="CachePaths.SafeId"/>); Name der Liste und Titel gehen in keinen Pfad
    /// ein, der Titel nur bereinigt in die Anzeige.
    /// </summary>
    public static PlaylistDownload DownloadPlan(Playlist p)
    {
        var entries = new List<PlaylistDownloadEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int unavailable = 0, overLimit = 0;
        foreach (var item in p.Items)
        {
            if (!IsAvailable(item)) unavailable++;
            else if (!seen.Add(ProgressStore.Key(item.LibraryItemId, item.EpisodeId))) continue;
            else if (entries.Count >= MaxDownloadEntries) overLimit++;
            else entries.Add(new PlaylistDownloadEntry(item.LibraryItemId, DisplayTitle(item), item.EpisodeId));
        }
        return new PlaylistDownload(entries, unavailable, overLimit);
    }

    /// <summary>
    /// Gilt eine Live-Meldung für die Ansicht? Nur Listen des angemeldeten Nutzers in der gezeigten Bibliothek.
    /// </summary>
    public static bool Applies(PlaylistChange change, string? userId, string? libraryId)
        => userId is not null && change.UserId == userId && change.LibraryId == libraryId;

    private static string Clean(string? input, int max)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(input.Length, max + 1));
        var space = false;
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length > max) break;
        }
        return Cut(sb.ToString(), max);
    }

    private static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = max;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut].TrimEnd();
    }
}

public enum PlaylistChangeKind { Added, Updated, Removed }

/// <summary>Ein Buch oder (mit EpisodeId) eine Podcast-Folge für „Liste herunterladen“.</summary>
public sealed record PlaylistDownloadEntry(string ItemId, string Title, string? EpisodeId = null);

/// <summary>Ergebnis von <see cref="PlaylistRules.DownloadPlan"/>: Bücher und Folgen und was übersprungen wird.</summary>
public sealed record PlaylistDownload(IReadOnlyList<PlaylistDownloadEntry> Entries, int Unavailable, int OverLimit)
{
    /// <summary>Kurzer Satz für die Meldung nach dem Klick.</summary>
    public string Describe()
    {
        var books = Entries.Count(e => e.EpisodeId is null);
        var episodes = Entries.Count - books;
        var what = (books, episodes) switch
        {
            (0, 0) => null,
            (_, 0) => books == 1 ? "1 Buch" : $"{books} Bücher",
            (0, _) => episodes == 1 ? "1 Folge" : $"{episodes} Folgen",
            _ => $"{(books == 1 ? "1 Buch" : $"{books} Bücher")} und {(episodes == 1 ? "1 Folge" : $"{episodes} Folgen")}",
        };
        var parts = new List<string>
        {
            what is null ? "In dieser Liste ist nichts zum Herunterladen." : $"{what} {(Entries.Count == 1 ? "kommt" : "kommen")} in die Downloads.",
        };
        if (Unavailable > 0) parts.Add(Unavailable == 1 ? "1 Eintrag ist nicht mehr verfügbar." : $"{Unavailable} Einträge sind nicht mehr verfügbar.");
        if (OverLimit > 0) parts.Add($"Mehr als {PlaylistRules.MaxDownloadEntries} Einträge auf einmal gehen nicht, {OverLimit} bleiben aus.");
        return string.Join(" ", parts);
    }
}

/// <summary>Live-Meldung zu einer Wiedergabeliste (nur Kennungen, kein Inhalt).</summary>
public sealed record PlaylistChange(PlaylistChangeKind Kind, string PlaylistId, string LibraryId, string UserId);
