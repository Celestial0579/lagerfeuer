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

    public static string DisplayName(Collection c)
    {
        var name = PlaylistRules.Clean(c.Name, PlaylistRules.MaxNameLength);
        return name.Length == 0 ? "Ohne Namen" : name;
    }

    public static string DisplayDescription(Collection c)
        => PlaylistRules.DisplayDescription(new Playlist { Description = c.Description });

    /// <summary>Nur Bücher mit schlichter ID, der Rest wird nicht angezeigt und nicht weitergegeben.</summary>
    public static List<LibraryItem> Books(Collection c) => c.Books.Where(b => PlaylistRules.IsPlainId(b.Id)).ToList();

    public static bool Contains(Collection c, string itemId) => c.Books.Any(b => b.Id == itemId);

    /// <summary>
    /// Enthält die Sammlung am Server genau die Bücher, die Lagerfeuer gerade zeigt? Nur dann darf die neue
    /// Reihenfolge geschickt werden: PATCH ersetzt die ganze Liste, ein inzwischen hinzugefügtes oder
    /// entferntes Buch ginge sonst verloren bzw. käme zurück.
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
