using System.Net;
using System.Text.Json;
using System.Web;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// SSO-Anmeldung mit eigener Adresse lagerfeuer://oauth und Rückfall auf audiobookshelf://oauth (#34).
/// Die Anmeldung merkt sich den laufenden Vorgang statisch, deshalb laufen diese Tests nicht parallel.
/// </summary>
[Collection(Sammlung)]
public class OpenIdLoginTests
{
    public const string Sammlung = "SSO-Anmeldung";

    private static readonly Uri Server = new("https://abs.example/");
    private static readonly Uri Anmeldeseite = new("https://idp.example/authorize?x=1");

    private static string LoginJson()
        => JsonSerializer.Serialize(new
        {
            user = new { id = "u1", username = "anna", accessToken = "access-1", refreshToken = "refresh-1", token = "legacy", mediaProgress = Array.Empty<object>() },
            userDefaultLibraryId = "lib1",
        });

    private static HttpResponseMessage UngueltigeRedirectUri()
        => new(HttpStatusCode.BadRequest) { Content = new StringContent("Invalid redirect_uri") };

    /// <summary>Server, der nur die genannten Redirect-URIs erlaubt, und den Code-Tausch beantwortet.</summary>
    private static FakeHandler ServerMit(params string[] erlaubt) => new(req =>
    {
        var query = HttpUtility.ParseQueryString(req.RequestUri!.Query);
        if (req.RequestUri.AbsolutePath == "/auth/openid")
        {
            if (!erlaubt.Contains(query["redirect_uri"])) return UngueltigeRedirectUri();
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = Anmeldeseite;
            return resp;
        }
        if (req.RequestUri.AbsolutePath == "/auth/openid/callback") return FakeHandler.Json(LoginJson());
        return FakeHandler.Status(HttpStatusCode.NotFound);
    });

    private static string StateDerAnfrage(FakeHandler handler)
        => HttpUtility.ParseQueryString(handler.Requests.First().RequestUri!.Query)["state"]!;

    private static string RedirectUris(FakeHandler handler)
        => string.Join(" ", handler.Requests
            .Where(r => r.RequestUri!.AbsolutePath == "/auth/openid")
            .Select(r => HttpUtility.ParseQueryString(r.RequestUri!.Query)["redirect_uri"]));

    [Theory]
    [InlineData("lagerfeuer://oauth", "lagerfeuer://oauth", "lagerfeuer")]
    [InlineData("audiobookshelf://oauth", "lagerfeuer://oauth audiobookshelf://oauth", "audiobookshelf")]
    public async Task Anmeldung_NimmtDieErsteErlaubteAdresse(string erlaubt, string angefragt, string protokoll)
    {
        var handler = ServerMit(erlaubt);
        var registriert = new List<string>();
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));

        var login = await OpenIdLogin.LoginAsync(api, Server, handler,
            url =>
            {
                Assert.Equal(Anmeldeseite, url);
                // Der Browser ruft Lagerfeuer über das Protokoll auf, das der Server erlaubt hat.
                Assert.True(OpenIdLogin.OnCallback($"{protokoll}://oauth?code=c1&state={StateDerAnfrage(handler)}"));
            },
            TestContext.Current.CancellationToken,
            scheme => { registriert.Add(scheme); return true; });

        Assert.NotNull(login);
        Assert.Equal(angefragt, RedirectUris(handler));
        Assert.Equal([protokoll], registriert);
        Assert.Equal("access-1", api.AccessToken);
        Assert.False(OpenIdLogin.IsWaiting);
    }

    [Fact]
    public async Task Anmeldung_KeineAdresseErlaubt_MeldungNenntBeide()
    {
        var handler = ServerMit();
        var opened = new List<Uri>();
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => OpenIdLogin.LoginAsync(api, Server, handler,
            opened.Add, TestContext.Current.CancellationToken, _ => true));

        Assert.Contains("lagerfeuer://oauth", ex.Message);
        Assert.Contains("audiobookshelf://oauth", ex.Message);
        Assert.Empty(opened);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Anmeldung_RueckfallGehoertAnderemProgramm_BrowserBleibtZu()
    {
        var handler = ServerMit("audiobookshelf://oauth");
        var opened = new List<Uri>();
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => OpenIdLogin.LoginAsync(api, Server, handler,
            opened.Add, TestContext.Current.CancellationToken, _ => false));

        Assert.Contains("lagerfeuer://oauth", ex.Message);
        Assert.Empty(opened);
        Assert.False(OpenIdLogin.IsWaiting);
    }

    [Fact]
    public async Task Anmeldung_AndererFehlerBeimStart_KeinRueckfall()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.InternalServerError));
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<AbsApiException>(() => OpenIdLogin.LoginAsync(api, Server, handler,
            _ => { }, TestContext.Current.CancellationToken, _ => true));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("lagerfeuer://oauth?code=c1&state=fremd")]
    [InlineData("lagerfeuer://oauth?code=c1")]
    [InlineData("lagerfeuer://oauth?error=access_denied&error_description=Bitte%20rufe%20090012345%20an")]
    [InlineData("lagerfeuer://oauth?error=access_denied&state=fremd")]
    public async Task Rueckruf_OhnePassendenState_WirdUebergangen_AnmeldungLaeuftWeiter(string fremd)
    {
        // GHSA-9cq7-jcr5-vp2c: Eine Webseite ruft lagerfeuer://oauth auf, während die Anmeldung wartet.
        var handler = ServerMit("lagerfeuer://oauth");
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));

        var login = await OpenIdLogin.LoginAsync(api, Server, handler,
            _ =>
            {
                Assert.False(OpenIdLogin.OnCallback(fremd));
                Assert.True(OpenIdLogin.IsWaiting);
                Assert.True(OpenIdLogin.OnCallback($"lagerfeuer://oauth?code=c1&state={StateDerAnfrage(handler)}"));
            },
            TestContext.Current.CancellationToken, _ => true);

        Assert.NotNull(login);
        Assert.Equal("access-1", api.AccessToken);
    }

    [Fact]
    public async Task Rueckruf_FehlerMitPassendemState_ZeigtKeinenFremdenText()
    {
        var handler = ServerMit("lagerfeuer://oauth");
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => OpenIdLogin.LoginAsync(api, Server, handler,
            _ => Assert.True(OpenIdLogin.OnCallback(
                $"lagerfeuer://oauth?error=access_denied&error_description=Bitte%20rufe%20090012345%20an&state={StateDerAnfrage(handler)}")),
            TestContext.Current.CancellationToken, _ => true));

        Assert.Contains("abgelehnt oder abgebrochen", ex.Message);
        Assert.DoesNotContain("0900", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == "/auth/openid/callback");
    }

    [Theory]
    [InlineData("access_denied", "abgelehnt")]
    [InlineData("invalid_request", "Fehlercode invalid_request")]
    [InlineData("Bitte rufe 0900 an", "Anmeldedienst hat einen Fehler")]
    [InlineData("<script>", "Anmeldedienst hat einen Fehler")]
    public void Fehlercode_WirdNieWoertlichUebernommen(string error, string erwartet)
        => Assert.Contains(erwartet, OpenIdLogin.DescribeError(error));

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData(null, "abc", false)]
    [InlineData("", "", false)]
    [InlineData("abc", null, false)]
    public void State_Vergleich(string? erhalten, string? erwartet, bool passt)
        => Assert.Equal(passt, OpenIdLogin.StateMatches(erhalten, erwartet));

    [Theory]
    [InlineData("lagerfeuer://oauth?code=1&state=2", true)]
    [InlineData("LAGERFEUER://oauth", true)]
    [InlineData("audiobookshelf://oauth?code=1&state=2", true)]
    [InlineData("https://abs.example/", false)]
    [InlineData("--nach-update", false)]
    public void Rueckruf_BeideProtokolleWerdenErkannt(string arg, bool erwartet)
        => Assert.Equal(erwartet, ProtocolRegistration.IsCallback(arg));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("\"C:\\Users\\anna\\AppData\\Local\\Programs\\Lagerfeuer\\Lagerfeuer.exe\" \"%1\"", true)]
    [InlineData("\"C:\\Users\\anna\\Downloads\\Lagerfeuer-0.2.1-win-x64.exe\" \"%1\"", true)]
    [InlineData("C:\\Tools\\lagerfeuer.exe %1", true)]
    [InlineData("\"C:\\Program Files\\Audiobookshelf\\Audiobookshelf.exe\" \"%1\"", false)]
    [InlineData("\"C:\\Lagerfeuer\\andere-app.exe\" \"%1\"", false)]
    [InlineData("\"C:\\Tools\\Lagerfeuer.exe.bat\" \"%1\"", false)]
    [InlineData("\"unvollstaendig", false)]
    public void Rueckfall_NurFreiOderLagerfeuer(string? command, bool frei)
        => Assert.Equal(frei, ProtocolRegistration.IsFree(command));
}
