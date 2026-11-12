namespace Lagerfeuer.Services;

/// <summary>Ein Rechteck in Bildschirmpixeln.</summary>
public readonly record struct WindowRect(int X, int Y, int Width, int Height);

/// <summary>
/// Größe und Lage des Fensters beim Start.
///
/// Früher öffnete Lagerfeuer immer mit 1280 × 860 Pixeln an der Stelle, die Windows vorgab. Auf einem
/// Bildschirm mit 1280 × 800 oder 1366 × 768 ragte das Fenster über den Rand, die Player-Leiste lag außerhalb.
/// Und weil der Wert in Pixeln gemeint war, fiel dasselbe Fenster bei 150 % Skalierung deutlich kleiner aus.
///
/// Gerechnet wird deshalb in geräteunabhängigen Einheiten (ein Pixel bei 100 %): Wunschgröße mal Skalierung,
/// begrenzt auf den Arbeitsbereich des Bildschirms, mittig darin.
/// </summary>
public static class WindowPlacement
{
    public const int DefaultWidth = 1280, DefaultHeight = 860;

    /// <summary>Kleiner wird eine gemerkte Größe nicht übernommen (etwa aus einer beschädigten Einstellung).</summary>
    public const int MinWidth = 480, MinHeight = 360;

    private const int MaxSaved = 20000;

    /// <param name="workArea">Arbeitsbereich des Bildschirms in Pixeln (ohne Taskleiste).</param>
    /// <param name="scale">Skalierung des Bildschirms, 1.0 bei 100 %.</param>
    /// <param name="savedWidth">Gemerkte Breite in geräteunabhängigen Einheiten, sonst null.</param>
    /// <param name="savedHeight">Gemerkte Höhe in geräteunabhängigen Einheiten, sonst null.</param>
    public static WindowRect Initial(WindowRect workArea, double scale, int? savedWidth = null, int? savedHeight = null)
    {
        scale = Usable(scale);
        // Nur ein vollständiges, plausibles Paar zählt; sonst die Vorgabe.
        var saved = savedWidth is >= MinWidth and <= MaxSaved && savedHeight is >= MinHeight and <= MaxSaved;
        var width = (int)Math.Round((saved ? savedWidth!.Value : DefaultWidth) * scale);
        var height = (int)Math.Round((saved ? savedHeight!.Value : DefaultHeight) * scale);

        // Ohne brauchbaren Arbeitsbereich bleibt nur die Größe; die Lage überlässt der Aufrufer Windows.
        if (workArea.Width <= 0 || workArea.Height <= 0) return new WindowRect(workArea.X, workArea.Y, width, height);

        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        return new WindowRect(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width, height);
    }

    /// <summary>Rechnet eine Fenstergröße in Pixeln in geräteunabhängige Einheiten um, zum Merken.</summary>
    public static (int Width, int Height) ToSaved(int widthPixels, int heightPixels, double scale)
    {
        scale = Usable(scale);
        return ((int)Math.Round(widthPixels / scale), (int)Math.Round(heightPixels / scale));
    }

    private static double Usable(double scale) => scale is > 0.1 and < 10 ? scale : 1.0;
}
