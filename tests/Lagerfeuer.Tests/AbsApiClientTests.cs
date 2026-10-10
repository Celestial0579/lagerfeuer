using System.Net;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class AbsApiClientTests
{
    private static readonly Uri Server = new("https://abs.test/");

    private static string LoginJson(string access, string? refresh, string username = "anna")
        => JsonSerializer.Serialize(new
        {
            user = new { id = "u1", username, accessToken = access, refreshToken = refresh, token = "legacy", mediaProgress = Array.Empty<object>() },
            userDefaultLibraryId = "lib1",
        });

    [Theory]
    [InlineData("abs.example.com", "https://abs.example.com/")]
    [InlineData("  https://abs.example.com/  ", "https://abs.example.com/")]
    [InlineData("http://nas:13378", "http://nas:13378/")]
    [InlineData("https://example.com/audiobookshelf", "https://example.com/audiobookshelf/")]
    public void NormalizeServerUrl_ErgaenztSchemaUndSlash(string input, string expected)
        => Assert.Equal(expected, AbsApiClient.NormalizeServerUrl(input).ToString());

    [Theory]
    [InlineData("ftp://abs.example.com")]
    [InlineData("https://")]
    public void NormalizeServerUrl_LehntUngueltigeAdressenAb(string input)
        => Assert.Throws<AbsApiException>(() => AbsApiClient.NormalizeServerUrl(input));

    [Theory]
    [InlineData("https://abs.test/", "api/libraries", "https://abs.test/api/libraries")]
    [InlineData("https://abs.test/", "/api/items/1/file/2", "https://abs.test/api/items/1/file/2")]
    // ABS unter Unterpfad: contentUrls enthalten den Unterpfad bereits
    [InlineData("https://host/abs/", "/abs/api/items/1/file/2", "https://host/abs/api/items/1/file/2")]
    [InlineData("https://host/abs/", "api/me", "https://host/abs/api/me")]
    public void Resolve_BeruecksichtigtUnterpfad(string server, string path, string expected)
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK)));
        Assert.Equal(expected, api.Resolve(path, new Uri(server)).ToString());
    }

    [Fact]
    public async Task Login_FordertTokensAnUndSpeichertSie()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(LoginJson("access-1", "refresh-1")));
        using var api = new AbsApiClient(handler);
        var tokensChanged = 0;
        api.TokensChanged += (_, _) => tokensChanged++;

        var login = await api.LoginAsync(Server, "anna", "geheim", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://abs.test/login", request.RequestUri!.ToString());
        Assert.Equal("true", request.Headers.GetValues("x-return-tokens").Single());
        Assert.Equal("anna", login.User.Username);
        Assert.Equal("access-1", api.AccessToken);
        Assert.Equal("refresh-1", api.RefreshToken);
        Assert.Equal(1, tokensChanged);
    }

    [Fact]
    public async Task Login_FalschesPasswort_GibtVerstaendlicheMeldung()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.Unauthorized)));
        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.LoginAsync(Server, "anna", "falsch", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Contains("Passwort", ex.Message);
    }

    [Fact]
    public void ApplyLogin_AeltererServerOhneAccessToken_NutztLegacyToken()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK)));
        var login = JsonSerializer.Deserialize<Models.LoginResponse>(
            """{"user":{"id":"u1","username":"anna","token":"legacy-token"}}""", AbsApiClient.Json)!;

        api.ApplyLogin(Server, login);

        Assert.Equal("legacy-token", api.AccessToken);
        Assert.Null(api.RefreshToken);
    }

    [Fact]
    public async Task AbgelaufenesToken_WirdErneuertUndAnfrageWiederholt()
    {
        var handler = new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/auth/refresh")
            {
                Assert.Equal("refresh-1", req.Headers.GetValues("x-refresh-token").Single());
                return FakeHandler.Json(LoginJson("access-2", "refresh-2"));
            }
            return req.Headers.Authorization?.Parameter == "access-2"
                ? FakeHandler.Json("""{"libraries":[{"id":"lib1","name":"Hörbücher","mediaType":"book"}]}""")
                : FakeHandler.Status(HttpStatusCode.Unauthorized);
        });
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "access-1", "refresh-1");
        var tokensChanged = 0;
        api.TokensChanged += (_, _) => tokensChanged++;

        var libraries = await api.GetLibrariesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Hörbücher", Assert.Single(libraries).Name);
        Assert.Equal("access-2", api.AccessToken);
        Assert.Equal("refresh-2", api.RefreshToken); // rotiert
        Assert.Equal(1, tokensChanged);
    }

    [Fact]
    public async Task GleichzeitigeAnfragen_ErneuernDasTokenNurEinmal()
    {
        var handler = new FakeHandler(async req =>
        {
            if (req.RequestUri!.AbsolutePath == "/auth/refresh")
            {
                await Task.Delay(100); // parallele Anfragen sollen in den Refresh laufen
                return FakeHandler.Json(LoginJson("access-2", "refresh-2"));
            }
            return req.Headers.Authorization?.Parameter == "access-2"
                ? FakeHandler.Json("""{"libraries":[]}""")
                : FakeHandler.Status(HttpStatusCode.Unauthorized);
        });
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "access-1", "refresh-1");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.GetLibrariesAsync(TestContext.Current.CancellationToken)));

        Assert.Equal(1, handler.Count(r => r.RequestUri!.AbsolutePath == "/auth/refresh"));
    }

    [Fact]
    public async Task AbgelehnterRefresh_MeldetAbgelaufeneSitzung()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.Unauthorized));
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "access-1", "refresh-1");
        var expired = false;
        api.SessionExpired += (_, _) => expired = true;

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.GetLibrariesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.True(expired);
    }

    [Fact]
    public async Task OhneRefreshToken_KeinRefreshVersuch()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.Unauthorized));
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "legacy", null);

        await Assert.ThrowsAsync<AbsApiException>(() => api.GetLibrariesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Count(r => r.RequestUri!.AbsolutePath == "/auth/refresh"));
    }

    [Fact]
    public async Task StartPlayback_SendetGeraeteinfoUndDirectPlay()
    {
        string? body = null;
        var handler = new FakeHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return FakeHandler.Json("""{"id":"sess1","libraryItemId":"item1","duration":100,"currentTime":12.5,"audioTracks":[],"chapters":[]}""");
        });
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "access", null);

        var session = await api.StartPlaybackAsync("item1", "device-42", ct: TestContext.Current.CancellationToken);

        Assert.Equal("https://abs.test/api/items/item1/play", Assert.Single(handler.Requests).RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("device-42", json.RootElement.GetProperty("deviceInfo").GetProperty("deviceId").GetString());
        Assert.Equal(AbsApiClient.ClientName, json.RootElement.GetProperty("deviceInfo").GetProperty("clientName").GetString());
        Assert.True(json.RootElement.GetProperty("forceDirectPlay").GetBoolean());
        Assert.Equal(12.5, session.CurrentTime);
    }

    [Fact]
    public async Task SyncSession_404_WirdAlsNotFoundGemeldet()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));
        api.Configure(Server, "access", null);

        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.SyncSessionAsync("sess1", 10, 5, 100, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }
}
