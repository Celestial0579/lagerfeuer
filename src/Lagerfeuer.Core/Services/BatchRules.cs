namespace Lagerfeuer.Services;

/// <summary>
/// Mehrere Bücher auf einmal in eine Sammlung oder Wiedergabeliste (#65), ohne Oberfläche prüfbar. Die Auswahl ist
/// begrenzt, IDs gehen nur schlicht hinaus. Was der Server nicht übernommen hat, wird gezählt und gemeldet, nicht
/// still verworfen.
/// </summary>
public static class BatchRules
{
    /// <summary>Größte Auswahl, die Lagerfeuer in einem Aufruf schickt.</summary>
    public const int MaxItems = 500;

    /// <summary>
    /// Die IDs einer Auswahl: nur schlichte, ohne Doppelte, in der Reihenfolge der Auswahl. Leer, wenn nichts
    /// Brauchbares ausgewählt ist; null, wenn es mehr als <see cref="MaxItems"/> sind.
    /// </summary>
    public static List<string>? Ids(IEnumerable<string?> selected)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in selected)
        {
            if (!PlaylistRules.IsPlainId(id) || !seen.Add(id!)) continue;
            if (ids.Count == MaxItems) return null;
            ids.Add(id!);
        }
        return ids;
    }

    /// <summary>
    /// Zählt, was aus der Auswahl geworden ist: neu hinzugefügt, war schon drin, nicht übernommen (etwa weil es
    /// am Server nicht mehr zu dieser Bibliothek gehört). <paramref name="before"/> ist der Stand vor dem Aufruf,
    /// <paramref name="after"/> die Antwort des Servers.
    /// </summary>
    public static BatchResult Summarize(IReadOnlyCollection<string> requested, IEnumerable<string> before, IEnumerable<string> after)
    {
        var was = new HashSet<string>(before, StringComparer.Ordinal);
        var now = new HashSet<string>(after, StringComparer.Ordinal);
        int added = 0, already = 0, missing = 0;
        foreach (var id in requested.Distinct(StringComparer.Ordinal))
        {
            if (was.Contains(id)) already++;
            else if (now.Contains(id)) added++;
            else missing++;
        }
        return new BatchResult(added, already, missing);
    }

    /// <summary>Meldung für die Leiste, z. B. „3 Bücher zu „Regal“ hinzugefügt. 1 war schon drin.“</summary>
    public static string Message(BatchResult r, string target)
    {
        if (r.Added == 0 && r.NotAdded == 0) return $"Alles Ausgewählte ist schon in „{target}“.";
        var parts = new List<string>
        {
            r.Added > 0 ? $"{Books(r.Added)} zu „{target}“ hinzugefügt." : $"Nichts zu „{target}“ hinzugefügt.",
        };
        if (r.AlreadyThere > 0) parts.Add(r.AlreadyThere == 1 ? "1 war schon drin." : $"{r.AlreadyThere} waren schon drin.");
        if (r.NotAdded > 0)
            parts.Add(r.NotAdded == 1 ? "1 Buch hat der Server nicht übernommen." : $"{r.NotAdded} Bücher hat der Server nicht übernommen.");
        return string.Join(" ", parts);
    }

    private static string Books(int n) => n == 1 ? "1 Buch" : $"{n} Bücher";
}

/// <summary>Ergebnis eines Sammel-Aufrufs (#65).</summary>
public readonly record struct BatchResult(int Added, int AlreadyThere, int NotAdded);
