using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Eine gescheiterte Verbindung ergibt einen kurzen deutschen Satz. Die Ausnahmen sind so gebaut, wie
/// HttpClient sie unter .NET 10 wirft: HttpRequestException mit HttpRequestError und der Ursache darunter.
/// </summary>
public class ConnectionFailureTests
{
    private static readonly Uri Server = new("https://abs.example.com/");

    private static HttpRequestException Http(HttpRequestError error, string message, Exception? inner = null)
        => new(error, message, inner);

    private static HttpRequestException NoTls() => Http(HttpRequestError.SecureConnectionError,
        "The SSL connection could not be established, see inner exception.",
        new AuthenticationException("Cannot determine the frame size or a corrupted frame was received."));

    private static HttpRequestException BadCertificate() => Http(HttpRequestError.SecureConnectionError,
        "The SSL connection could not be established, see inner exception.",
        new AuthenticationException("The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch, RemoteCertificateChainErrors"));

    private static HttpRequestException Refused() => Http(HttpRequestError.ConnectionError,
        "Es konnte keine Verbindung hergestellt werden, da der Zielcomputer die Verbindung verweigerte. (127.0.0.1:13378)",
        new SocketException((int)SocketError.ConnectionRefused));

    private static HttpRequestException UnknownName() => Http(HttpRequestError.NameResolutionError,
        "No such host is known. (abs.example.com:443)", new SocketException((int)SocketError.HostNotFound));

    [Fact]
    public void KeinHttps_NenntDenAuswegUeberHttp()
        => Assert.Equal(ConnectionFailure.NoHttps, ConnectionFailure.Describe(NoTls(), Server));

    [Fact]
    public void KeinHttps_AuchOhneUrsacheDarunter()
        => Assert.Equal(ConnectionFailure.NoHttps, ConnectionFailure.Describe(
            Http(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.",
                new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")), Server));

    [Fact]
    public void ZertifikatNichtVertrauenswuerdig_RaetNichtZuHttp()
    {
        var text = ConnectionFailure.Describe(BadCertificate(), Server);

        Assert.Equal(ConnectionFailure.Certificate, text);
        Assert.DoesNotContain("http://", text);
    }

    [Fact]
    public void UnbekannterName_NenntDenNamen()
        => Assert.Equal("Der Servername „abs.example.com“ ist unbekannt. Prüfe die Adresse.", ConnectionFailure.Describe(UnknownName(), Server));

    [Fact]
    public void UnbekannterName_OhneAdresse()
        => Assert.Equal("Der Servername ist unbekannt. Prüfe die Adresse.", ConnectionFailure.Describe(UnknownName()));

    [Theory]
    [InlineData(SocketError.ConnectionRefused, ConnectionFailure.Refused)]
    [InlineData(SocketError.TimedOut, ConnectionFailure.Timeout)]
    [InlineData(SocketError.HostUnreachable, ConnectionFailure.NoRoute)]
    [InlineData(SocketError.NetworkUnreachable, ConnectionFailure.NoRoute)]
    [InlineData(SocketError.ConnectionReset, ConnectionFailure.Aborted)]
    [InlineData(SocketError.AddressNotAvailable, ConnectionFailure.Generic)]
    public void Verbindungsfehler_NachDerUrsacheImSocket(SocketError error, string expected)
        => Assert.Equal(expected, ConnectionFailure.Describe(
            Http(HttpRequestError.ConnectionError, "connect failed", new SocketException((int)error)), Server));

    [Fact]
    public void AbgelaufeneZeit_IstEineZeitueberschreitung()
    {
        Assert.Equal(ConnectionFailure.Timeout, ConnectionFailure.Describe(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")));
        Assert.Equal(ConnectionFailure.Timeout, ConnectionFailure.Describe(new TaskCanceledException("abgebrochen", new TimeoutException())));
    }

    [Theory]
    [InlineData(HttpRequestError.ResponseEnded, ConnectionFailure.Aborted)]
    [InlineData(HttpRequestError.InvalidResponse, ConnectionFailure.NotAudiobookshelf)]
    [InlineData(HttpRequestError.HttpProtocolError, ConnectionFailure.NotAudiobookshelf)]
    [InlineData(HttpRequestError.Unknown, ConnectionFailure.Generic)]
    [InlineData(HttpRequestError.ProxyTunnelError, ConnectionFailure.Generic)]
    public void OhneUrsacheImSocket_NachDerArtDesFehlers(HttpRequestError error, string expected)
        => Assert.Equal(expected, ConnectionFailure.Describe(Http(error, "An error occurred while sending the request."), Server));

    [Fact]
    public void KeinJson_IstKeinAudiobookshelfServer()
    {
        Assert.Equal(ConnectionFailure.NotAudiobookshelf, ConnectionFailure.Describe(new JsonException("'<' is an invalid start of a value.")));
        Assert.Equal(ConnectionFailure.NotAudiobookshelf, ConnectionFailure.Describe(new NotSupportedException("The provided ContentType is not supported")));
    }

    public static TheoryData<Exception> AlleFehler() =>
    [
        NoTls(), BadCertificate(), Refused(), UnknownName(),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."),
        new HttpRequestException("An error occurred while sending the request."),
        new JsonException("'<' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0."),
    ];

    [Theory]
    [MemberData(nameof(AlleFehler))]
    public void Meldung_IstEinDeutscherSatzOhneTextDerLaufzeit(Exception ex)
    {
        var text = ConnectionFailure.Describe(ex, Server);

        Assert.EndsWith(".", text);
        Assert.DoesNotContain(ex.Message, text);
        Assert.DoesNotContain("inner exception", text);
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain(":443", text);
    }
}

/// <summary>Die Sätze kommen dort an, wo die Oberfläche sie anzeigt: in den Ausnahmen des API-Clients.</summary>
public class ConnectionMessageTests
{
    private static readonly Uri Server = new("https://abs.test/");

    private static HttpRequestException NoTls() => new(HttpRequestError.SecureConnectionError,
        "The SSL connection could not be established, see inner exception.",
        new AuthenticationException("Cannot determine the frame size or a corrupted frame was received."));

    [Fact]
    public async Task Verbinden_OhneHttps_DeutscherSatzMitAusweg()
    {
        using var api = new AbsApiClient(new FakeHandler(HttpResponseMessage (_) => throw NoTls()));

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.GetStatusAsync(Server, TestContext.Current.CancellationToken));

        Assert.Equal(ConnectionFailure.NoHttps, ex.Message);
        Assert.Null(ex.Status);
        Assert.IsType<HttpRequestException>(ex.InnerException); // fürs Log
    }

    [Fact]
    public async Task Verbinden_AntwortIstEineWebseite_KeinAudiobookshelfServer()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Willkommen</body></html>", Encoding.UTF8, "text/html"),
        });
        using var api = new AbsApiClient(handler);

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.GetStatusAsync(Server, TestContext.Current.CancellationToken));

        Assert.Equal(ConnectionFailure.NotAudiobookshelf, ex.Message);
    }

    [Fact]
    public async Task Verbinden_UnlesbaresJson_KeinAudiobookshelfServer()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Json("<html>")));

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.GetStatusAsync(Server, TestContext.Current.CancellationToken));

        Assert.Equal(ConnectionFailure.NotAudiobookshelf, ex.Message);
    }

    [Fact]
    public async Task Anmelden_OhneVerbindung_AbsApiExceptionStattLaufzeitText()
    {
        var handler = new FakeHandler(HttpResponseMessage (_) => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "Es konnte keine Verbindung hergestellt werden, da der Zielcomputer die Verbindung verweigerte. (abs.test:443)",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        using var api = new AbsApiClient(handler);

        var ex = await Assert.ThrowsAsync<AbsApiException>(
            () => api.LoginAsync(Server, "anna", "geheim", TestContext.Current.CancellationToken));

        Assert.Equal(ConnectionFailure.Refused, ex.Message);
        Assert.Null(ex.Status);
        Assert.Null(api.AccessToken);
    }

    [Fact]
    public async Task Abfrage_OhneVerbindung_DeutscherSatz()
    {
        var handler = new FakeHandler(HttpResponseMessage (_) => throw new HttpRequestException(HttpRequestError.NameResolutionError,
            "No such host is known. (abs.test:443)", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "token", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.GetLibrariesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Der Servername „abs.test“ ist unbekannt. Prüfe die Adresse.", ex.Message);
        Assert.Null(ex.Status);
    }

    [Fact]
    public async Task Abfrage_UnlesbaresJson_SagtDassDieAntwortUnlesbarWar()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Json("{kaputt")));
        api.Configure(Server, "token", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.GetLibrariesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Die Antwort des Servers war nicht lesbar.", ex.Message);
    }

    [Fact]
    public async Task Meldung_OhneVerbindung_DeutscherSatz()
    {
        var handler = new FakeHandler(HttpResponseMessage (_) => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "connect failed", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "token", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(
            () => api.SyncSessionAsync("sitzung", 10, 5, 100, TestContext.Current.CancellationToken));

        Assert.Equal(ConnectionFailure.Refused, ex.Message);
    }
}
