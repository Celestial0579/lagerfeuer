using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Adressen aus Antworten eines Servers gibt Lagerfeuer nur dann an Windows zum Öffnen weiter, wenn es
/// Web-Adressen sind. Über die Shell öffnet Windows sonst auch Programme („file:“) und andere Anwendungen.
/// </summary>
public class WebLinksTests
{
    // Was ein Server statt einer Anmeldeseite nennen könnte.
    public static TheoryData<string> KeineWebAdressen => new()
    {
        "file:///C:/Windows/System32/calc.exe",
        "file://angreifer.example/freigabe/programm.exe",
        "ms-msdt:/id%20PCWDiagnostic",
        "search-ms:query=rechnung",
        "javascript:alert(1)",
        "data:text/html,hallo",
        "mailto:jemand@example.org",
        "ftp://example.org/datei",
        "audiobookshelf://oauth?code=1&state=2",
        "shell:startup",
    };

    [Theory]
    [InlineData("https://abs.example/")]
    [InlineData("http://192.168.1.5:13378/audiobookshelf/login")]
    [InlineData("https://idp.example/application/o/authorize/?client_id=abs&state=x")]
    [InlineData("HTTPS://IDP.EXAMPLE/authorize")]
    public void WebAdresse_WirdErkannt(string url) => Assert.True(WebLinks.IsWebAddress(new Uri(url)));

    [Theory]
    [MemberData(nameof(KeineWebAdressen))]
    public void AnderesSchema_IstKeineWebAdresse(string url)
        => Assert.False(WebLinks.IsWebAddress(new Uri(url, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void OhneAdresseOderNurEinPfad_IstKeineWebAdresse()
    {
        Assert.False(WebLinks.IsWebAddress(null));
        Assert.False(WebLinks.IsWebAddress(new Uri("/auth/openid", UriKind.Relative)));
    }

    [Fact]
    public void ImBrowserOeffnen_ReichtDieWebAdresseAnDieShell()
    {
        var info = WebLinks.OpenInBrowser(new Uri("https://idp.example/authorize?client_id=abs"));

        Assert.Equal("https://idp.example/authorize?client_id=abs", info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Equal("", info.Arguments);
    }

    [Theory]
    [MemberData(nameof(KeineWebAdressen))]
    public void ImBrowserOeffnen_LehntAllesAndereAb(string url)
        => Assert.Throws<ArgumentException>(() => WebLinks.OpenInBrowser(new Uri(url, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void ImBrowserOeffnen_OhneLeerzeichenUndAnfuehrungszeichen()
    {
        // Beides könnte sich sonst in die Befehlszeile des Browsers mogeln.
        var info = WebLinks.OpenInBrowser(new Uri("https://idp.example/authorize?hinweis=\"a b\" --schalter"));

        Assert.DoesNotContain('"', info.FileName);
        Assert.DoesNotContain(' ', info.FileName);
        Assert.StartsWith("https://idp.example/authorize?hinweis=", info.FileName);
    }

    // ---------- SSO-Anmeldung: Adresse der Anmeldeseite aus der Antwort des Servers ----------

    private static readonly Uri Start = new("https://abs.example/audiobookshelf/auth/openid?state=x");

    /// <summary>Liest eine Kopfzeile Location so, wie es der HttpClient bei einer echten Antwort tut.</summary>
    private static Uri? ParseLocation(string value)
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.Found);
        resp.Headers.TryAddWithoutValidation("Location", value);
        return resp.Headers.Location;
    }

    [Fact]
    public void Anmeldeseite_VollstaendigeWebAdresseBleibt()
    {
        var url = OpenIdLogin.ResolveAuthorizeUrl(Start, ParseLocation("https://idp.example/application/o/authorize/?client_id=abs")!);

        Assert.Equal("https://idp.example/application/o/authorize/?client_id=abs", url.AbsoluteUri);
    }

    [Fact]
    public void Anmeldeseite_PfadWirdUmDenServerErgaenzt()
    {
        var url = OpenIdLogin.ResolveAuthorizeUrl(Start, ParseLocation("/audiobookshelf/auth/anmelden?x=1")!);

        Assert.Equal("https://abs.example/audiobookshelf/auth/anmelden?x=1", url.AbsoluteUri);
    }

    [Theory]
    [MemberData(nameof(KeineWebAdressen))]
    public void Anmeldeseite_AnderesSchemaBrichtDieAnmeldungAb(string location)
    {
        var parsed = ParseLocation(location);
        Assert.NotNull(parsed);

        var ex = Assert.Throws<AbsApiException>(() => OpenIdLogin.ResolveAuthorizeUrl(Start, parsed));

        Assert.Contains("keine Web-Adresse", ex.Message);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\angreifer.example\freigabe\programm.exe")]
    [InlineData("C:/Windows/System32/calc.exe")]
    [InlineData("//angreifer.example/freigabe/programm.exe")]
    [InlineData("../../../Windows/System32/calc.exe")]
    public void Anmeldeseite_PfadeErgebenNieEtwasAnderesAlsEineWebAdresse(string location)
    {
        // Wie Windows-Pfade und Netzfreigaben gelesen werden, hängt vom Betriebssystem ab. Entscheidend ist:
        // Heraus kommt eine Web-Adresse oder die Anmeldung bricht ab.
        var parsed = ParseLocation(location);
        if (parsed is null) return; // als Adresse gar nicht lesbar: Die Anmeldung scheitert schon vorher

        try
        {
            Assert.True(WebLinks.IsWebAddress(OpenIdLogin.ResolveAuthorizeUrl(Start, parsed)));
        }
        catch (AbsApiException)
        {
            // abgelehnt
        }
    }

    [Theory]
    [MemberData(nameof(KeineWebAdressen))]
    public async Task Anmeldung_ServerNenntKeineWebAdresse_NichtsWirdGeoeffnet(string location)
    {
        // Der ganze Weg: Der Server antwortet auf den Start der SSO-Anmeldung mit einer Umleitung auf etwas,
        // das keine Web-Seite ist.
        var handler = new FakeHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.TryAddWithoutValidation("Location", location);
            return resp;
        });
        var opened = new List<Uri>();
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => OpenIdLogin.LoginAsync(
            api, new Uri("https://abs.example/"), handler, opened.Add, TestContext.Current.CancellationToken));

        Assert.Contains("keine Web-Adresse", ex.Message);
        Assert.Empty(opened);
        Assert.Single(handler.Requests); // nach der Antwort folgt keine weitere Anfrage
        Assert.False(OpenIdLogin.IsWaiting);
    }

    // ---------- Update-Hinweis: Seite des Release ----------

    [Fact]
    public void ReleaseSeite_WebAdresseBleibt()
        => Assert.Equal("https://github.com/Celestial0579/lagerfeuer/releases/tag/v0.2.2",
            UpdateService.ReleasePage("https://github.com/Celestial0579/lagerfeuer/releases/tag/v0.2.2"));

    [Theory]
    [MemberData(nameof(KeineWebAdressen))]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("keine Adresse")]
    [InlineData("/releases/tag/v0.2.2")]
    public void ReleaseSeite_AllesAndereWirdZurUebersichtDerReleases(string? htmlUrl)
        => Assert.Equal("https://github.com/Celestial0579/lagerfeuer/releases", UpdateService.ReleasePage(htmlUrl));

    [Fact]
    public async Task UpdateHinweis_NenntNurEineWebAdresse()
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "v9.9.9",
            name = "Lagerfeuer v9.9.9",
            html_url = "file:///C:/Windows/System32/calc.exe",
            draft = false,
            prerelease = false,
            assets = Array.Empty<object>(),
        });
        using var dir = new TempDir();
        var updates = new UpdateService(new HttpClient(new FakeHandler(_ => FakeHandler.Json(json))), new Version(0, 1, 0), false, "win-x64", dir.Path);

        var update = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal("https://github.com/Celestial0579/lagerfeuer/releases", update.ReleaseUrl);
    }

    // ---------- Warum die Prüfung nötig ist ----------

    [Fact]
    public void UnterWindows_StartetDieShellEinProgrammAusEinerFileAdresse()
    {
        // Das ist das Verhalten von Windows, gegen das die Prüfung schützt: Eine Adresse mit „file:“, über die
        // Shell geöffnet, startet das Programm dahinter. hostname.exe ist harmlos, es gibt den Rechnernamen aus.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Die Shell von Windows gibt es nur unter Windows.");
        var programm = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        Assert.True(File.Exists(programm), programm);

        using var p = Process.Start(new ProcessStartInfo(new Uri(programm).AbsoluteUri) { UseShellExecute = true });

        Assert.NotNull(p);
        Assert.True(p.WaitForExit(15000), "Das gestartete Programm ist nicht zu Ende gekommen.");
        Assert.Equal(0, p.ExitCode);
    }
}
