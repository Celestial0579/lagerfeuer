using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>
/// Abspielen einer Wiedergabeliste in Reihenfolge (#36). Hält eine Momentaufnahme der Einträge, damit
/// eine spätere Änderung der Liste (auch über den Live-Abgleich) die laufende Wiedergabe nicht umwirft.
/// Nicht verfügbare Einträge (gelöschtes Buch, fremde IDs) werden übersprungen.
/// </summary>
public sealed class PlaylistQueue
{
    private readonly List<PlaylistItem> _items;

    public PlaylistQueue(Playlist playlist, int startIndex)
    {
        PlaylistId = playlist.Id;
        Name = PlaylistRules.DisplayName(playlist);
        _items = playlist.Items.ToList();
        Index = FirstAvailableFrom(Math.Max(0, startIndex));
    }

    public string PlaylistId { get; }
    public string Name { get; }
    public int Count => _items.Count;

    /// <summary>Position des laufenden Eintrags, -1 wenn nichts (mehr) abspielbar ist.</summary>
    public int Index { get; private set; }

    public PlaylistItem? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;

    /// <summary>Gibt es nach dem laufenden noch einen abspielbaren Eintrag?</summary>
    public bool HasNext => Current is not null && FirstAvailableFrom(Index + 1) >= 0;

    /// <summary>Weiter zum nächsten abspielbaren Eintrag; null am Ende der Liste.</summary>
    public PlaylistItem? MoveNext()
    {
        if (Current is null) return null;
        Index = FirstAvailableFrom(Index + 1);
        return Current;
    }

    /// <summary>Gehört dieser Titel zum laufenden Eintrag?</summary>
    public bool IsCurrent(string? itemId, string? episodeId)
        => Current is { } c && c.LibraryItemId == itemId && c.EpisodeId == episodeId;

    /// <summary>Anzeige wie „Liste · 2 von 5“.</summary>
    public string Describe() => Current is null ? Name : $"{Name} · {Index + 1} von {Count}";

    private int FirstAvailableFrom(int start)
    {
        for (var i = start; i < _items.Count; i++)
            if (PlaylistRules.IsAvailable(_items[i])) return i;
        return -1;
    }
}
