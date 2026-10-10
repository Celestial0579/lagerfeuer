using System.Net;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class SessionManagementTests
{
    private static readonly Uri Server = new("https://abs.test/");

    [Fact]
    public async Task Geraeteliste_SchicktRefreshTokenMit_UndErkenntDiesesGeraet()
    {
        var handler = new FakeHandler(req =>
        {
            Assert.Equal("refresh-1", req.Headers.GetValues("x-refresh-token").Single());
            return FakeHandler.Json("""
                {"total":2,"numPages":1,"page":0,"itemsPerPage":100,"sessions":[
                  {"id":"11111111-1111-1111-1111-111111111111","ipAddress":"10.0.0.2","userAgent":"Lagerfeuer-Windows/0.1","deviceInfo":null,"createdAt":1,"updatedAt":2,"current":true},
                  {"id":"22222222-2222-2222-2222-222222222222","ipAddress":"10.0.0.9","userAgent":"Mozilla/5.0",
                   "deviceInfo":{"browserName":"Firefox","osName":"Linux","osVersion":""},"createdAt":1,"updatedAt":3,"current":false}]}
                """);
        });
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "access", "refresh-1");

        var sessions = await api.GetAuthSessionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/api/me/sessions", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.True(sessions[0].Current);
        Assert.Equal("Lagerfeuer-Windows/0.1", sessions[0].DisplayName); // ohne deviceInfo → User-Agent
        Assert.Equal("Firefox · Linux", sessions[1].DisplayName);
    }

    [Fact]
    public async Task GeraetAbmelden_LoeschtDieSitzung()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "access", "refresh");

        await api.DeleteAuthSessionAsync("22222222-2222-2222-2222-222222222222", TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/me/sessions/22222222-2222-2222-2222-222222222222", request.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(false, "/logout", "")]
    [InlineData(true, "/logout", "?allDevices=1")]
    public async Task Abmelden_WiderruftRefreshToken(bool allDevices, string path, string query)
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"redirect_url":null}"""));
        using var api = new AbsApiClient(handler);
        api.Configure(Server, "access", "refresh-1");

        await api.LogoutAsync(allDevices);

        var request = handler.Requests.Single();
        Assert.Equal(path, request.RequestUri!.AbsolutePath);
        Assert.Equal(query, request.RequestUri.Query);
        Assert.Equal("refresh-1", request.Headers.GetValues("x-refresh-token").Single());
        Assert.Null(api.AccessToken);
        Assert.Null(api.RefreshToken);
    }

    [Fact]
    public async Task UeberallAbmelden_Fehlschlag_WirdGemeldet_LokalTrotzdemAbgemeldet()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.InternalServerError)));
        api.Configure(Server, "access", "refresh-1");

        await Assert.ThrowsAsync<AbsApiException>(() => api.LogoutAsync(allDevices: true));
        Assert.Null(api.RefreshToken);
    }
}
