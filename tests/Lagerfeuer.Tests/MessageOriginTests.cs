using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// #40: Erst schlägt das Laden fehl, dann gelingt es. Danach steht keine Fehlermeldung mehr da, Meldungen
/// anderer Herkunft aber schon. Die Leiste wird hier als (offen, Text) nachgebildet.
/// </summary>
public class MessageOriginTests
{
    private sealed class Bar
    {
        public bool IsOpen;
        public string? Message;
        public readonly MessageOrigin Origin = new();

        public void Show(string message, string? origin = null)
        {
            Message = message;
            IsOpen = true;
            Origin.Shown(message, origin);
        }

        public void Loaded(string origin)
        {
            if (Origin.Resolve(origin, IsOpen, Message)) IsOpen = false;
        }
    }

    [Fact]
    public void FehlerDannErfolg_MeldungVerschwindet()
    {
        var bar = new Bar();
        bar.Show("„Weiterhören“ konnte nicht geladen werden: Server meldet 503", MessageOrigins.Home);

        bar.Loaded(MessageOrigins.Home);

        Assert.False(bar.IsOpen);
    }

    [Fact]
    public void ErfolgAndererHerkunft_MeldungBleibt()
    {
        var bar = new Bar();
        bar.Show("„Weiterhören“ konnte nicht geladen werden: Server meldet 503", MessageOrigins.Home);

        bar.Loaded(MessageOrigins.Book);

        Assert.True(bar.IsOpen);
    }

    [Fact]
    public void NeuereMeldungOhneHerkunft_Bleibt()
    {
        var bar = new Bar();
        bar.Show("„Weiterhören“ konnte nicht geladen werden: Server meldet 504", MessageOrigins.Home);
        bar.Show("Lesezeichen konnte nicht gesetzt werden: Server meldet 504");

        bar.Loaded(MessageOrigins.Home);

        Assert.True(bar.IsOpen);
        Assert.StartsWith("Lesezeichen", bar.Message);
    }

    [Fact]
    public void VomNutzerGeschlossen_BleibtZu()
    {
        var bar = new Bar();
        bar.Show("Serien konnten nicht geladen werden: Bad Gateway", MessageOrigins.Series);
        bar.IsOpen = false;

        Assert.False(bar.Origin.Resolve(MessageOrigins.Series, bar.IsOpen, bar.Message));
    }

    [Fact]
    public void ZweiterErfolg_SchliesstNichtsMehr()
    {
        var bar = new Bar();
        bar.Show("Buch konnte nicht geladen werden: 404 page not found", MessageOrigins.Book);
        bar.Loaded(MessageOrigins.Book);
        bar.IsOpen = true; // gleiche Leiste, inzwischen von etwas anderem wieder geöffnet

        Assert.False(bar.Origin.Resolve(MessageOrigins.Book, bar.IsOpen, bar.Message));
    }

    [Fact]
    public void GleicherTextAndereHerkunft_ZaehltDieLetzte()
    {
        var bar = new Bar();
        bar.Show("Server meldet 503", MessageOrigins.Home);
        bar.Show("Server meldet 503", MessageOrigins.Library);

        bar.Loaded(MessageOrigins.Home);
        Assert.True(bar.IsOpen);

        bar.Loaded(MessageOrigins.Library);
        Assert.False(bar.IsOpen);
    }
}
