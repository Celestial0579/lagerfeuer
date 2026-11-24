using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Regeln für Sammlungen (#35), die ohne Oberfläche prüfbar sind. Sammlungen teilen sich alle Nutzer einer
/// Bibliothek; ändern darf nur, wer am Server das Recht dazu hat. Daten vom Server gelten als nicht
/// vertrauenswürdig: Namen nur bereinigt und gekürzt, IDs nur schlicht.
/// </summary>
public static class CollectionRules
{
    /// <summary>Was der angemeldete Nutzer an Sammlungen tun darf.</summary>
    public readonly record struct Rights(bool CanEdit, bool CanDelete);

    /// <summary>Verwalter dürfen alles; sonst gelten die Rechte „Aktualisieren“ und „Löschen“.</summary>
    public static Rights For(AbsUser? user)
    {
        if (user is null) return new(false, false);
        if (user.Type is "root" or "admin") return new(true, true);
        return new(user.Permissions?.Update == true, user.Permissions?.Delete == true);
    }

    /// <summary>
    /// Übernimmt Typ und Rechte aus einer Live-Meldung <c>user_updated</c> (#81), aber nur aus einem vollständigen
    /// Nutzerobjekt (Typ und Rechte gesetzt); eine Meldung ohne diese Angaben ändert nichts. Liefert, ob sich an
    /// den Rechten für Sammlungen etwas geändert hat.
    /// </summary>
    public static bool TakeRights(AbsUser target, AbsUser update)
    {
        if (update.Type is null || update.Permissions is null) return false;
        var before = For(target);
        target.Type = update.Type;
        target.Permissions = update.Permissions;
        return For(target) != before;
    }

    public static string DisplayName(Collection c)
    {
        var name = PlaylistRules.Clean(c.Name, PlaylistRules.MaxNameLength);
        return name.Length == 0 ? "Ohne Namen" : name;
    }

    public static string DisplayDescription(Collection c)
        => PlaylistRules.DisplayDescription(new Playlist { Description = c.Description });

    /// <summary>
    /// Bereinigt eine eingegebene Beschreibung: Zeilenumbrüche werden zu „\n“ (ein Textfeld unter Windows liefert
    /// „\r“), sonstige Steuerzeichen entfallen, höchstens <see cref="PlaylistRules.MaxDescriptionLength"/> Zeichen.
    /// Null heißt: nichts Neues zu speichern. Leer heißt: die Beschreibung entfernen.
    /// </summary>
    public static string? NormalizeDescription(string? input, string current)
    {
        var text = (input ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var chars = text.Where(c => c == '\n' || !char.IsControl(c)).ToArray();
        var cleaned = PlaylistRules.Cut(new string(chars).Trim(), PlaylistRules.MaxDescriptionLength);
        return cleaned == current ? null : cleaned;
    }

    /// <summary>Nur Bücher mit schlichter ID, der Rest wird nicht angezeigt und nicht weitergegeben.</summary>
    public static List<LibraryItem> Books(Collection c) => c.Books.Where(b => PlaylistRules.IsPlainId(b.Id)).ToList();

    public static bool Contains(Collection c, string itemId) => c.Books.Any(b => b.Id == itemId);

    /// <summary>
    /// Enthält die Sammlung am Server genau die Bücher, die Lagerfeuer gerade zeigt? Nur dann darf die neue
    /// Reihenfolge geschickt werden: Was der Server mit einer abweichenden Liste macht, hängt von seiner Version
    /// ab (2.37 schiebt fehlende Bücher nach vorn). Ein inzwischen hinzugefügtes Buch landete sonst an falscher
    /// Stelle.
    /// </summary>
    public static bool SameBooks(IEnumerable<string> shown, Collection fresh)
        => new HashSet<string>(shown).SetEquals(fresh.Books.Select(b => b.Id)) && fresh.Books.Count == shown.Count();

    /// <summary>Wie eine Sammlung als Warteschlange abgespielt wird: Bücher in ihrer Reihenfolge.</summary>
    public static Playlist AsPlaylist(Collection c) => new()
    {
        Id = c.Id,
        LibraryId = c.LibraryId,
        Name = c.Name,
        Items = c.Books.Select(b => new PlaylistItem { LibraryItemId = b.Id, LibraryItem = b }).ToList(),
    };

    /// <summary>Gilt eine Live-Meldung für eine der Bibliotheken des Nutzers?</summary>
    public static bool Applies(CollectionChange change, IEnumerable<string> libraryIds) => libraryIds.Contains(change.LibraryId);
}

public enum CollectionChangeKind { Added, Updated, Removed }

/// <summary>Live-Meldung zu einer Sammlung (nur Kennungen, kein Inhalt).</summary>
public sealed record CollectionChange(CollectionChangeKind Kind, string CollectionId, string LibraryId);

/// <summary>Ergebnis von <see cref="AbsApiClient.ReorderCollectionAsync"/>.</summary>
public sealed record CollectionReorderResult(bool Saved, Collection Current);
