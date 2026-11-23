using System.Net;
using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Mehrere Bücher auf einmal (#65): Auswahl, Aufrufe und Meldungen. Alle Serverantworten sind erfunden.</summary>
public class BatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AbsApiClient Api, FakeHandler Handler, List<string> Bodies) Client(string json = "{\"id\":\"x1\",\"libraryId\":\"lib1\"}")
    {
        var bodies = new List<string>();
        var handler = new FakeHandler(r =>
        {
            bodies.Add(r.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return FakeHandler.Json(json);
        });
        var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        return (api, handler, bodies);
    }

    private static List<(HttpMethod, string)> Calls(FakeHandler h) => h.Requests.Select(r => (r.Method, r.RequestUri!.PathAndQuery)).ToList();

    [Fact]
    public void Auswahl_NurSchlichteIdsOhneDoppelte_InReihenfolge()
        => Assert.Equal(["b2", "b1", "b3"], BatchRules.Ids(["b2", "b1", "../x", "b2", null, "", "a b", "b3"]));

    [Fact]
    public void Auswahl_HoechstensMax()
    {
        Assert.Equal(BatchRules.MaxItems, BatchRules.Ids(Enumerable.Range(0, BatchRules.MaxItems).Select(i => $"b{i}"))!.Count);
        Assert.Null(BatchRules.Ids(Enumerable.Range(0, BatchRules.MaxItems + 1).Select(i => $"b{i}")));
        // Doppelte zählen nicht mit
        Assert.NotNull(BatchRules.Ids(Enumerable.Range(0, BatchRules.MaxItems).Select(i => $"b{i}").Concat(["b0", "b1"])));
    }

    [Fact]
    public async Task Sammlung_BatchAdd_UndNeuMitMehreren()
    {
        var (api, handler, bodies) = Client();
        using var _api = api;
        await api.AddBooksToCollectionAsync("c1", ["b1", "b2"], Ct);
        await api.CreateCollectionAsync("lib1", "Regal", ["b1", "b2"], Ct);
        await api.CreateCollectionAsync("lib1", "Eins", "b3", Ct);
        Assert.Equal([(HttpMethod.Post, "/api/collections/c1/batch/add"), (HttpMethod.Post, "/api/collections"), (HttpMethod.Post, "/api/collections")], Calls(handler));
        Assert.Equal("{\"books\":[\"b1\",\"b2\"]}", bodies[0]);
        Assert.Equal("{\"libraryId\":\"lib1\",\"name\":\"Regal\",\"books\":[\"b1\",\"b2\"]}", bodies[1]);
        Assert.Equal("{\"libraryId\":\"lib1\",\"name\":\"Eins\",\"books\":[\"b3\"]}", bodies[2]);
    }

    [Fact]
    public async Task Wiedergabeliste_BatchAdd_UndNeuMitMehreren()
    {
        var (api, handler, bodies) = Client("{\"id\":\"pl1\",\"libraryId\":\"lib1\",\"items\":[]}");
        using var _api = api;
        await api.AddBooksToPlaylistAsync("pl1", ["b1", "b2"], Ct);
        await api.CreatePlaylistAsync("lib1", "Abend", ["b1", "b2"], Ct);
        Assert.Equal([(HttpMethod.Post, "/api/playlists/pl1/batch/add"), (HttpMethod.Post, "/api/playlists")], Calls(handler));
        Assert.Equal("{\"items\":[{\"libraryItemId\":\"b1\"},{\"libraryItemId\":\"b2\"}]}", bodies[0]);
        Assert.Equal("{\"libraryId\":\"lib1\",\"name\":\"Abend\",\"items\":[{\"libraryItemId\":\"b1\"},{\"libraryItemId\":\"b2\"}]}", bodies[1]);
    }

    [Theory]
    [MemberData(nameof(PlaylistTests.BoeseIds), MemberType = typeof(PlaylistTests))]
    public async Task BoeseId_GehtNieAnDenServer(string id)
    {
        var (api, handler, _) = Client();
        using var _api = api;
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToCollectionAsync("c1", ["b1", id], Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToCollectionAsync(id, ["b1"], Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.CreateCollectionAsync("lib1", "x", ["b1", id], Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToPlaylistAsync("pl1", [id], Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToPlaylistAsync(id, ["b1"], Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.CreatePlaylistAsync("lib1", "x", [id], Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LeereOderZuGrosseAuswahl_GehtNieAnDenServer()
    {
        var (api, handler, _) = Client();
        using var _api = api;
        var zuViele = Enumerable.Range(0, BatchRules.MaxItems + 1).Select(i => $"b{i}").ToList();
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToCollectionAsync("c1", [], Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToCollectionAsync("c1", zuViele, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToPlaylistAsync("pl1", zuViele, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.CreatePlaylistAsync("lib1", "x", [], Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Wiedergabeliste_400_KommtAlsFehlerAn()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.BadRequest));
        using var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.AddBooksToPlaylistAsync("pl1", ["b1"], Ct));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public void Ergebnis_Gezaehlt()
    {
        var r = BatchRules.Summarize(["b1", "b2", "b3", "b4", "b1"], before: ["b2", "x9"], after: ["b2", "x9", "b1", "b3"]);
        Assert.Equal(new BatchResult(Added: 2, AlreadyThere: 1, NotAdded: 1), r);
    }

    [Theory]
    [InlineData(3, 0, 0, "3 Bücher zu „Regal“ hinzugefügt.")]
    [InlineData(1, 1, 0, "1 Buch zu „Regal“ hinzugefügt. 1 war schon drin.")]
    [InlineData(2, 3, 1, "2 Bücher zu „Regal“ hinzugefügt. 3 waren schon drin. 1 Buch hat der Server nicht übernommen.")]
    [InlineData(0, 4, 0, "Alles Ausgewählte ist schon in „Regal“.")]
    [InlineData(0, 1, 2, "Nichts zu „Regal“ hinzugefügt. 1 war schon drin. 2 Bücher hat der Server nicht übernommen.")]
    public void Meldung(int added, int already, int missing, string expected)
        => Assert.Equal(expected, BatchRules.Message(new BatchResult(added, already, missing), "Regal"));

    [Fact]
    public void Sammlung_AntwortDesServers_ZaehltBuecher()
    {
        // So sieht die Antwort von batch/add aus: die ganze Sammlung mit allen Büchern.
        var json = "{\"id\":\"c1\",\"libraryId\":\"lib1\",\"books\":[{\"id\":\"b1\"},{\"id\":\"b2\"}]}";
        var c = JsonSerializer.Deserialize<Collection>(json, AbsApiClient.Json)!;
        Assert.Equal(new BatchResult(1, 1, 1), BatchRules.Summarize(["b1", "b2", "b3"], before: ["b1"], after: c.Books.Select(b => b.Id)));
    }
}
