using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Größe und Lage des Fensters beim Start.</summary>
public class WindowPlacementTests
{
    private static readonly WindowRect FullHd = new(0, 0, 1920, 1040);

    [Fact]
    public void GrosserBildschirm_VorgabeMittig()
        => Assert.Equal(new WindowRect(320, 90, 1280, 860), WindowPlacement.Initial(FullHd, 1.0));

    [Theory]
    // Die Fälle aus dem Issue: Das Fenster ragte über den Rand.
    [InlineData(1280, 752, 0, 0, 1280, 752)]   // 1280 × 800 mit Taskleiste
    [InlineData(1366, 720, 43, 0, 1280, 720)]  // 1366 × 768 mit Taskleiste
    [InlineData(1024, 728, 0, 0, 1024, 728)]
    public void KleinerBildschirm_NieGroesserAlsDerArbeitsbereich(int breite, int hoehe, int x, int y, int w, int h)
        => Assert.Equal(new WindowRect(x, y, w, h), WindowPlacement.Initial(new WindowRect(0, 0, breite, hoehe), 1.0));

    [Fact]
    public void Skalierung_VergroessertDieWunschgroesse()
    {
        // 4K bei 150 %: 1280 × 860 Einheiten sind 1920 × 1290 Pixel.
        var rect = WindowPlacement.Initial(new WindowRect(0, 0, 3840, 2100), 1.5);

        Assert.Equal(new WindowRect(960, 405, 1920, 1290), rect);
    }

    [Fact]
    public void Skalierung_AufKleinemBildschirm_BleibtImArbeitsbereich()
    {
        // 1920 × 1080 bei 150 %: Die Wunschgröße passte in der Höhe nicht mehr.
        var rect = WindowPlacement.Initial(new WindowRect(0, 0, 1920, 1032), 1.5);

        Assert.Equal(new WindowRect(0, 0, 1920, 1032), rect);
    }

    [Fact]
    public void ZweiterBildschirm_LageBeziehtSichAufDessenArbeitsbereich()
        => Assert.Equal(new WindowRect(1920 + 320, -200 + 90, 1280, 860),
            WindowPlacement.Initial(new WindowRect(1920, -200, 1920, 1040), 1.0));

    [Fact]
    public void GemerkteGroesse_WirdUebernommenUndBegrenzt()
    {
        Assert.Equal(new WindowRect(460, 220, 1000, 600), WindowPlacement.Initial(FullHd, 1.0, 1000, 600));
        // Gemerkt auf einem großen Bildschirm, gestartet auf einem kleinen.
        Assert.Equal(new WindowRect(0, 0, 1280, 752), WindowPlacement.Initial(new WindowRect(0, 0, 1280, 752), 1.0, 2400, 1300));
    }

    [Theory]
    [InlineData(1000, null)]      // nur die halbe Angabe
    [InlineData(null, 600)]
    [InlineData(100, 600)]        // zu klein, etwa aus einer beschädigten Einstellung
    [InlineData(1000, 50)]
    [InlineData(-1, -1)]
    [InlineData(999999, 600)]
    public void UnbrauchbareGemerkteGroesse_ErgibtDieVorgabe(int? breite, int? hoehe)
        => Assert.Equal(new WindowRect(320, 90, 1280, 860), WindowPlacement.Initial(FullHd, 1.0, breite, hoehe));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(50.0)]
    public void UnbrauchbareSkalierung_GiltAlsHundertProzent(double scale)
        => Assert.Equal(new WindowRect(320, 90, 1280, 860), WindowPlacement.Initial(FullHd, scale));

    [Fact]
    public void OhneArbeitsbereich_BleibtNurDieGroesse()
    {
        var rect = WindowPlacement.Initial(new WindowRect(0, 0, 0, 0), 1.0);

        Assert.Equal((1280, 860), (rect.Width, rect.Height));
    }

    [Theory]
    [InlineData(1920, 1290, 1.5, 1280, 860)]
    [InlineData(1000, 600, 1.0, 1000, 600)]
    [InlineData(1501, 901, 1.25, 1201, 721)]
    [InlineData(1000, 600, 0.0, 1000, 600)]
    public void Merken_RechnetInGeraeteunabhaengigeEinheitenUm(int breitePx, int hoehePx, double scale, int breite, int hoehe)
        => Assert.Equal((breite, hoehe), WindowPlacement.ToSaved(breitePx, hoehePx, scale));

    [Fact]
    public void MerkenUndWiederherstellen_ErgibtDieselbeGroesse()
    {
        // Bei 125 % gemerkt, bei 125 % wieder geöffnet: dieselben Pixel (bis auf Rundung).
        var (breite, hoehe) = WindowPlacement.ToSaved(1500, 900, 1.25);
        var rect = WindowPlacement.Initial(new WindowRect(0, 0, 2560, 1400), 1.25, breite, hoehe);

        Assert.Equal((1500, 900), (rect.Width, rect.Height));
    }
}
