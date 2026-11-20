using System.Net;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// #63: IDs vom Server kommen nur als schlichte, maskierte Pfadabschnitte in eine Anfrage. Andere lehnt der Client ab,
/// ohne etwas zu senden. contentUrls der Tracks dürfen nur den Dateiabruf eines Titels am eigenen Server treffen.
/// </summary>
public sealed class ApiPathTests : IDisposable
{
    private readonly FakeHandler _handler = new(_ => FakeHandler.Json("{}"));
    private readonly AbsApiClient _api;

    public ApiPathTests()
    {
        _api = new AbsApiClient(_handler);
        _api.Configure(new Uri("https://abs.test/"), "token", null);
    }

    public void Dispose() => _api.Dispose();

    public static TheoryData<string> BoeseIds() => new()
    {
        "..", ".", "", "a/b", "a?x=1", "a#b", "%2e%2e", "x/../../api/me", @"a\b", "a b", "ä", new string('a', 65),
    };

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Jeder Aufruf mit einer Server-ID im Pfad, die ID an der Stelle von <c>id</c>.</summary>
    private static readonly (string Name, Func<AbsApiClient, string, Task> Call)[] Calls =
    [
        ("DeleteAuthSession", (a, id) => a.DeleteAuthSessionAsync(id, Ct)),
        ("LibraryItems", (a, id) => a.GetLibraryItemsAsync(id, 0, 10, null, Ct)),
        ("Series", (a, id) => a.GetSeriesAsync(id, 0, 10, Ct)),
        ("SeriesBooks", (a, id) => a.GetSeriesBooksAsync(id, "ser_1", Ct)),
        ("Authors", (a, id) => a.GetAuthorsAsync(id, Ct)),
        ("Author", (a, id) => a.GetAuthorAsync(id, Ct)),
        ("AuthorImage", (a, id) => a.GetAuthorImageAsync(id, Ct)),
        ("Bookmarks", (a, id) => a.GetBookmarksAsync(id, Ct)),
        ("CreateBookmark", (a, id) => a.CreateBookmarkAsync(id, 1, "x", Ct)),
        ("RenameBookmark", (a, id) => a.RenameBookmarkAsync(id, 1, "x", Ct)),
        ("DeleteBookmark", (a, id) => a.DeleteBookmarkAsync(id, 1, Ct)),
        ("Search", (a, id) => a.SearchAsync(id, "x", Ct)),
        ("Item", (a, id) => a.GetItemAsync(id, Ct)),
        ("Cover", (a, id) => a.GetCoverAsync(id, Ct)),
        ("Play", (a, id) => a.StartPlaybackAsync(id, "geraet", null, Ct)),
        ("PlayEpisodeItem", (a, id) => a.StartPlaybackAsync(id, "geraet", "ep_1", Ct)),
        ("PlayEpisode", (a, id) => a.StartPlaybackAsync("li_1", "geraet", id, Ct)),
        ("Sync", (a, id) => a.SyncSessionAsync(id, 1, 1, 10, Ct)),
        ("Close", (a, id) => a.CloseSessionAsync(id, 1, 1, 10, Ct)),
        ("Progress", (a, id) => a.UpdateProgressAsync(id, 1, 10, false, null, Ct)),
        ("ProgressEpisodeItem", (a, id) => a.UpdateProgressAsync(id, 1, 10, false, "ep_1", Ct)),
        ("ProgressEpisode", (a, id) => a.UpdateProgressAsync("li_1", 1, 10, false, id, Ct)),
    ];

    public static TheoryData<string, string> AufrufeMitBoesenIds()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, _) in Calls)
            foreach (var id in BoeseIds()) data.Add(name, id);
        return data;
    }

    public static TheoryData<string> Aufrufe()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Calls) data.Add(name);
        return data;
    }

    private static Func<AbsApiClient, string, Task> Call(string name) => Calls.Single(c => c.Name == name).Call;

    [Theory]
    [MemberData(nameof(AufrufeMitBoesenIds))]
    public async Task BoeseId_WirdAbgelehnt_OhneAnfrage(string call, string id)
    {
        await Assert.ThrowsAsync<AbsApiException>(() => Call(call)(_api, id));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Aufrufe))]
    public async Task SchlichteId_LandetAlsEigenerPfadabschnitt(string call)
    {
        try { await Call(call)(_api, "li_Ab-9"); }
        catch (Exception) { } // Antwort "{}" passt nicht zu jedem Aufruf; entscheidend ist die gesendete Anfrage
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("abs.test", request.RequestUri!.Host);
        Assert.Contains("/li_Ab-9", request.RequestUri.AbsolutePath);
        Assert.StartsWith("/api/", request.RequestUri.AbsolutePath);
    }

    [Theory]
    [InlineData("https://abs.test/", "/api/items/li_1/file/123", "https://abs.test/api/items/li_1/file/123")]
    [InlineData("https://abs.test/", "api/items/li_1/file/123", "https://abs.test/api/items/li_1/file/123")]
    [InlineData("https://host/abs/", "/abs/api/items/li_1/file/123", "https://host/abs/api/items/li_1/file/123")]
    [InlineData("https://host/abs/", "/api/items/li_1/file/123", "https://host/abs/api/items/li_1/file/123")]
    public void ContentUrl_DateiabrufEinesTitels(string server, string contentUrl, string expected)
    {
        _api.Configure(new Uri(server), "token", null);
        Assert.Equal(expected, _api.ResolveContent(contentUrl).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/api/items/li_1/file/123?x=1")]
    [InlineData("/api/items/li_1/file/123#x")]
    [InlineData("/api/items/li_1/file/../../../me")]
    [InlineData("/api/items/../file/1")]
    [InlineData("/api/items/li_1/file/%2e%2e")]
    [InlineData("/api/items/li_1/cover")]
    [InlineData("/api/me")]
    [InlineData("/login")]
    [InlineData("//evil.example/api/items/li_1/file/1")]
    [InlineData("https://evil.example/api/items/li_1/file/1")]
    [InlineData("https://abs.test/api/items/li_1/file/1")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData(@"\api\items\li_1\file\1")]
    public void ContentUrl_AnderesWirdAbgelehnt(string? contentUrl)
        => Assert.Throws<AbsApiException>(() => _api.ResolveContent(contentUrl));

    [Fact]
    public async Task Streamen_MitFremderContentUrl_SendetNichts()
    {
        using var dir = new TempDir();
        await Assert.ThrowsAsync<AbsApiException>(() => CachingHttpStream.OpenAsync(_api,
            "https://evil.example/api/items/li_1/file/1", Path.Combine(dir.Path, "x.mp3"), Ct));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/api/me")]
    [InlineData("http://abs.test/api/me")]
    [InlineData("https://abs.test:8443/api/me")]
    public void Resolve_VolleAdresseZuAnderemServer_WirdAbgelehnt(string path)
        => Assert.Throws<AbsApiException>(() => _api.Resolve(path));

    [Fact]
    public void Resolve_VolleAdresseZumEigenenServer_BleibtErlaubt()
        => Assert.Equal("https://abs.test/api/me", _api.Resolve("https://ABS.test/api/me").ToString());
}
