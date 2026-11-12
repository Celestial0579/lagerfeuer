using System.Net;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Wann gilt der Server als nicht erreichbar? Davon hängt ab, ob ein heruntergeladenes Buch von der Platte
/// spielt (PlayerService.StartSessionAsync) oder die Antwort als Auskunft des Servers behandelt wird.
/// </summary>
public class ServerUnreachableTests
{
    private static readonly Uri Server = new("https://abs.test/");

    [Theory]
    // Keine Antwort: Verbindung abgelehnt, Zeitüberschreitung, Name unbekannt.
    [InlineData(null, null, true)]
    // Ein Proxy meldet, dass der Server dahinter fehlt.
    [InlineData(502, "Bad Gateway", true)]
    [InlineData(503, "Service Unavailable", true)]
    [InlineData(504, "Gateway Timeout", true)]
    [InlineData(502, "", true)]
    // 404 von einem Proxy: Traefik ohne Dienst zur Adresse, HTML-Seite von nginx.
    [InlineData(404, "404 page not found", true)]
    [InlineData(404, "404 Page Not Found\n", true)]
    [InlineData(404, "<html>\r\n<head><title>404 Not Found</title></head>", true)]
    [InlineData(404, "  <!DOCTYPE html><html>", true)]
    // 404 von Audiobookshelf selbst (Express: „Not Found“), leer oder JSON: Auskunft des Servers.
    [InlineData(404, "Not Found", false)]
    [InlineData(404, "", false)]
    [InlineData(404, null, false)]
    [InlineData(404, "{\"error\":\"not found\"}", false)]
    // Alles andere ist eine Antwort des Servers, auch wenn sie ein Fehler ist.
    [InlineData(401, "Unauthorized", false)]
    [InlineData(403, "<html>Forbidden</html>", false)]
    [InlineData(500, "Internal Server Error", false)]
    [InlineData(429, "Too Many Requests", false)]
    public void ServerUnreachable_NurOhneAntwortOderMitProxyFehler(int? status, string? detail, bool expected)
    {
        var ex = new AbsApiException("Meldung", (HttpStatusCode?)status) { Detail = detail };

        Assert.Equal(expected, ex.ServerUnreachable);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "Bad Gateway", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "no available server", true)]
    [InlineData(HttpStatusCode.GatewayTimeout, "Gateway Timeout", true)]
    [InlineData(HttpStatusCode.NotFound, "404 page not found\n", true)]
    [InlineData(HttpStatusCode.NotFound, "Not Found", false)]
    [InlineData(HttpStatusCode.InternalServerError, "kaputt", false)]
    public async Task Wiedergabestart_FehlerantwortTraegtStatusUndAntworttext(HttpStatusCode status, string body, bool unreachable)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "token", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(
            () => api.StartPlaybackAsync("buch", "geraet", ct: TestContext.Current.CancellationToken));

        Assert.Equal(status, ex.Status);
        Assert.Equal(body.Trim(), ex.Detail);
        Assert.Equal(unreachable, ex.ServerUnreachable);
    }

    [Fact]
    public async Task Wiedergabestart_OhneVerbindung_GiltAlsNichtErreichbar()
    {
        var handler = new FakeHandler(HttpResponseMessage (_) => throw new HttpRequestException("Verbindung abgelehnt"));
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "token", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(
            () => api.StartPlaybackAsync("buch", "geraet", ct: TestContext.Current.CancellationToken));

        Assert.Null(ex.Status);
        Assert.Null(ex.Detail);
        Assert.True(ex.ServerUnreachable);
    }

    [Fact]
    public async Task Fortschrittsmeldung_404VomServer_BleibtAuskunftDesServers()
    {
        // Audiobookshelf antwortet so, wenn die Wiedergabe-Sitzung nicht mehr offen ist. Der Player legt
        // dann eine neue an; als „nicht erreichbar“ darf das nicht gelten.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") });
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "token", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(
            () => api.SyncSessionAsync("sitzung", 10, 5, 100, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
        Assert.False(ex.ServerUnreachable);
    }

    [Fact]
    public async Task Antworttext_WirdAufZweihundertZeichenGekuerzt()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(new string('x', 500)) });
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "token", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(
            () => api.StartPlaybackAsync("buch", "geraet", ct: TestContext.Current.CancellationToken));

        Assert.Equal(200, ex.Detail!.Length);
    }
}
