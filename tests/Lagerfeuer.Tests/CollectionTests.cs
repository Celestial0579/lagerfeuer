using System.Net;
using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Sammlungen (#35): Aufrufe, Rechte, Reihenfolge und Live-Meldungen. Alle Serverantworten sind erfunden.</summary>
public class CollectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CollectionJson(string id = "c1", params string[] books)
        => JsonSerializer.Serialize(new
        {
            id, libraryId = "lib1", name = "Regal",
            books = (books.Length == 0 ? ["b1", "b2"] : books).Select(b => new { id = b, mediaType = "book", media = new { metadata = new { title = "Buch " + b } } }),
        });

    private static (AbsApiClient Api, FakeHandler Handler, List<string> Bodies) Client(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var bodies = new List<string>();
        var handler = new FakeHandler(r =>
        {
            bodies.Add(r.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return respond?.Invoke(r) ?? FakeHandler.Json(CollectionJson());
        });
        var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        return (api, handler, bodies);
    }

    private static List<(HttpMethod, string)> Calls(FakeHandler h) => h.Requests.Select(r => (r.Method, r.RequestUri!.PathAndQuery)).ToList();

    [Fact]
    public async Task Aufrufe_PfadeUndInhalte()
    {
        var (api, handler, bodies) = Client(r => r.RequestUri!.AbsolutePath.EndsWith("/collections")
            ? FakeHandler.Json($"{{\"results\":[{CollectionJson()}],\"total\":1}}") : FakeHandler.Json(CollectionJson()));
        using var _api = api;
        Assert.Single(await api.GetCollectionsAsync("lib1", Ct));
        await api.CreateCollectionAsync("lib1", "Regal", "b1", Ct);
        await api.RenameCollectionAsync("c1", "Neu", Ct);
        await api.AddToCollectionAsync("c1", "b3", Ct);
        await api.RemoveFromCollectionAsync("c1", "b3", Ct);
        Assert.Equal(new List<(HttpMethod, string)>
        {
            (HttpMethod.Get, "/api/libraries/lib1/collections?limit=500"),
            (HttpMethod.Post, "/api/collections"),
            (HttpMethod.Patch, "/api/collections/c1"),
            (HttpMethod.Post, "/api/collections/c1/book"),
            (HttpMethod.Delete, "/api/collections/c1/book/b3"),
        }, Calls(handler));
        Assert.Equal("{\"libraryId\":\"lib1\",\"name\":\"Regal\",\"books\":[\"b1\"]}", bodies[1]);
        Assert.Equal("{\"name\":\"Neu\"}", bodies[2]);
        Assert.Equal("{\"id\":\"b3\"}", bodies[3]);
    }

    [Fact]
    public async Task Beschreibung_PatchNurMitBeschreibung()
    {
        var (api, handler, bodies) = Client();
        using var _api = api;
        await api.SetCollectionDescriptionAsync("c1", "Zeile 1\nZeile 2", Ct);
        await api.SetCollectionDescriptionAsync("c1", "", Ct);
        Assert.Equal([(HttpMethod.Patch, "/api/collections/c1"), (HttpMethod.Patch, "/api/collections/c1")], Calls(handler));
        Assert.Equal("{\"description\":\"Zeile 1\\nZeile 2\"}", bodies[0]);
        Assert.Equal("{\"description\":\"\"}", bodies[1]);
    }

    [Theory]
    [InlineData("Neu", "", "Neu")]
    [InlineData("  Neu  ", "", "Neu")]
    [InlineData("A\r\nB", "", "A\nB")]       // Windows-Zeilenende
    [InlineData("A\rB", "", "A\nB")]          // so liefert es ein WinUI-Textfeld
    [InlineData("A\u0000B\u001bC\tD", "", "ABCD")] // Steuerzeichen fallen weg
    [InlineData("", "Alt", "")]               // leeren entfernt die Beschreibung
    [InlineData("   ", "Alt", "")]
    public void Beschreibung_Bereinigt(string input, string current, string expected)
        => Assert.Equal(expected, CollectionRules.NormalizeDescription(input, current));

    [Theory]
    [InlineData("Alt", "Alt")]
    [InlineData(" Alt\r\n", "Alt")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Beschreibung_Unveraendert_Null(string? input, string current)
        => Assert.Null(CollectionRules.NormalizeDescription(input, current));

    [Fact]
    public void Beschreibung_Gekuerzt()
    {
        var lang = new string('x', PlaylistRules.MaxDescriptionLength + 50);
        Assert.Equal(PlaylistRules.MaxDescriptionLength, CollectionRules.NormalizeDescription(lang, "")!.Length);
    }

    [Fact]
    public async Task Reihenfolge_GleicheBuecher_WirdGespeichert()
    {
        var (api, handler, bodies) = Client();
        using var _api = api;
        var result = await api.ReorderCollectionAsync("c1", ["b2", "b1"], Ct);
        Assert.True(result.Saved);
        Assert.Equal([(HttpMethod.Get, "/api/collections/c1"), (HttpMethod.Patch, "/api/collections/c1")], Calls(handler));
        Assert.Equal("{\"books\":[\"b2\",\"b1\"]}", bodies[1]);
    }

    [Theory]
    [InlineData("b1", "b2", "b3")] // inzwischen hinzugefügt
    [InlineData("b1")]             // inzwischen entfernt
    [InlineData("b1", "b9")]       // ausgetauscht
    public async Task Reihenfolge_ServerHatAndereBuecher_NichtsGeschickt(params string[] onServer)
    {
        var (api, handler, _) = Client(_ => FakeHandler.Json(CollectionJson("c1", onServer)));
        using var _api = api;
        var result = await api.ReorderCollectionAsync("c1", ["b2", "b1"], Ct);
        Assert.False(result.Saved);
        Assert.Equal(onServer, result.Current.Books.Select(b => b.Id));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task OhneRecht_403_MitStatus()
    {
        var (api, _, _) = Client(_ => FakeHandler.Status(HttpStatusCode.Forbidden));
        using var _api = api;
        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.AddToCollectionAsync("c1", "b1", Ct));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
    }

    [Theory]
    [MemberData(nameof(PlaylistTests.BoeseIds), MemberType = typeof(PlaylistTests))]
    public async Task BoeseId_GehtNieAnDenServer(string id)
    {
        var (api, handler, _) = Client();
        using var _api = api;
        await Assert.ThrowsAsync<AbsApiException>(() => api.GetCollectionAsync(id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.GetCollectionsAsync(id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.DeleteCollectionAsync(id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddToCollectionAsync("c1", id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.RemoveFromCollectionAsync("c1", id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.CreateCollectionAsync("lib1", "x", id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.ReorderCollectionAsync("c1", ["b1", id], Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.RenameCollectionAsync(id, "x", Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.SetCollectionDescriptionAsync(id, "x", Ct));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("root", false, false, true, true)]
    [InlineData("admin", false, false, true, true)]
    [InlineData("user", true, false, true, false)]
    [InlineData("user", false, true, false, true)]
    [InlineData("user", false, false, false, false)]
    [InlineData("guest", false, false, false, false)]
    public void Rechte(string type, bool update, bool delete, bool canEdit, bool canDelete)
    {
        var rights = CollectionRules.For(new AbsUser { Type = type, Permissions = new UserPermissions { Update = update, Delete = delete } });
        Assert.Equal(new CollectionRules.Rights(canEdit, canDelete), rights);
    }

    [Fact]
    public void Rechte_OhneNutzerOderAngaben_Nichts()
    {
        Assert.Equal(new CollectionRules.Rights(false, false), CollectionRules.For(null));
        Assert.Equal(new CollectionRules.Rights(false, false), CollectionRules.For(new AbsUser { Type = "user" }));
    }

    [Fact]
    public void Rechte_AusDemJsonDesServers()
    {
        var user = JsonSerializer.Deserialize<AbsUser>("{\"id\":\"u1\",\"type\":\"user\",\"permissions\":{\"download\":true,\"update\":true,\"delete\":false}}", AbsApiClient.Json)!;
        Assert.Equal(new CollectionRules.Rights(true, false), CollectionRules.For(user));
    }

    [Fact]
    public void Anzeige_UndBuecher()
    {
        var c = new Collection { Name = "‮Reg\nal" + new string('x', 500), Description = "a\u0007b", Books = [new() { Id = "b1" }, new() { Id = "../x" }] };
        Assert.StartsWith("Reg al", CollectionRules.DisplayName(c));
        Assert.Equal(PlaylistRules.MaxNameLength, CollectionRules.DisplayName(c).Length);
        Assert.Equal("ab", CollectionRules.DisplayDescription(c));
        Assert.Equal(["b1"], CollectionRules.Books(c).Select(b => b.Id));
        Assert.Equal("Ohne Namen", CollectionRules.DisplayName(new Collection()));
    }

    [Fact]
    public void AlleAbspielen_AlsWarteschlange()
    {
        var c = new Collection { Id = "c1", Name = "Regal", Books = [new() { Id = "b1" }, new() { Id = "../x" }, new() { Id = "b3" }] };
        var queue = new PlaylistQueue(CollectionRules.AsPlaylist(c), 0);
        Assert.Equal("b1", queue.Current!.LibraryItemId);
        Assert.Equal("b3", queue.MoveNext()!.LibraryItemId);
        Assert.Null(queue.MoveNext());
    }

    [Theory]
    [InlineData("collection_added", CollectionChangeKind.Added)]
    [InlineData("collection_updated", CollectionChangeKind.Updated)]
    [InlineData("collection_removed", CollectionChangeKind.Removed)]
    public void LiveMeldung(string name, CollectionChangeKind kind)
    {
        var payload = JsonDocument.Parse(CollectionJson()).RootElement.Clone();
        var change = SocketIoProtocol.TryParseCollectionChange(name, payload);
        Assert.Equal(new CollectionChange(kind, "c1", "lib1"), change);
        Assert.True(CollectionRules.Applies(change!, ["lib1"]));
        Assert.False(CollectionRules.Applies(change!, ["lib2"]));
    }

    [Theory]
    [InlineData("{\"id\":\"../c\",\"libraryId\":\"lib1\"}")]
    [InlineData("{\"id\":\"c1\"}")]
    [InlineData("\"c1\"")]
    public void LiveMeldung_Unbrauchbar(string json)
        => Assert.Null(SocketIoProtocol.TryParseCollectionChange("collection_updated", JsonDocument.Parse(json).RootElement.Clone()));
}
