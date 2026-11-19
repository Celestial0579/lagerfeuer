using System.Net;
using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Wiedergabelisten (#36): Aufrufe, Regeln und Live-Meldungen. Alle Serverantworten sind erfunden.</summary>
public class PlaylistTests
{
    private const string Lib = "lib1";

    private static string PlaylistJson(string id = "pl1", string name = "Abend", string user = "u1")
        => JsonSerializer.Serialize(new
        {
            id, libraryId = Lib, userId = user, name, description = (string?)null,
            items = new object[]
            {
                new { libraryItemId = "b1", libraryItem = new { id = "b1", mediaType = "book", media = new { metadata = new { title = "Buch eins", authorName = "Anna" } } } },
                new { libraryItemId = "p1", episodeId = "e1", libraryItem = new { id = "p1", mediaType = "podcast", media = new { metadata = new { title = "Podcast" } } }, episode = new { id = "e1", title = "Folge 1" } },
            },
        });

    private static (AbsApiClient Api, FakeHandler Handler, List<string> Bodies) Client(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var bodies = new List<string>();
        var handler = new FakeHandler(r =>
        {
            bodies.Add(r.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return respond?.Invoke(r) ?? FakeHandler.Json(PlaylistJson());
        });
        var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        return (api, handler, bodies);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Listen_EinerBibliothek()
    {
        var (api, handler, _) = Client(_ => FakeHandler.Json($"{{\"results\":[{PlaylistJson()}],\"total\":1}}"));
        using var _api = api;
        var lists = await api.GetPlaylistsAsync(Lib, Ct);
        Assert.Equal("https://abs.test/api/libraries/lib1/playlists", handler.Requests.Single().RequestUri!.ToString());
        var list = Assert.Single(lists);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("e1", list.Items[1].EpisodeId);
        Assert.Equal("Folge 1", PlaylistRules.DisplayTitle(list.Items[1]));
        Assert.Equal("Podcast", PlaylistRules.DisplaySubtitle(list.Items[1]));
        Assert.Equal("Anna", PlaylistRules.DisplaySubtitle(list.Items[0]));
    }

    [Fact]
    public async Task Anlegen_MitErstemEintrag()
    {
        var (api, handler, bodies) = Client();
        using var _api = api;
        await api.CreatePlaylistAsync(Lib, "Abend", "p1", "e1", Ct);
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://abs.test/api/playlists", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(bodies.Single());
        Assert.Equal("lib1", doc.RootElement.GetProperty("libraryId").GetString());
        Assert.Equal("Abend", doc.RootElement.GetProperty("name").GetString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("p1", item.GetProperty("libraryItemId").GetString());
        Assert.Equal("e1", item.GetProperty("episodeId").GetString());
    }

    [Fact]
    public async Task Hinzufuegen_Buch_OhneEpisodeId()
    {
        var (api, handler, bodies) = Client();
        using var _api = api;
        await api.AddToPlaylistAsync("pl1", "b2", null, Ct);
        Assert.Equal("https://abs.test/api/playlists/pl1/item", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal("{\"libraryItemId\":\"b2\"}", bodies.Single());
    }

    [Fact]
    public async Task Entfernen_FolgeUndBuch()
    {
        var (api, handler, _) = Client();
        using var _api = api;
        await api.RemoveFromPlaylistAsync("pl1", "p1", "e1", Ct);
        await api.RemoveFromPlaylistAsync("pl1", "b1", null, Ct);
        var urls = handler.Requests.Select(r => (r.Method, r.RequestUri!.ToString())).ToList();
        Assert.Equal((HttpMethod.Delete, "https://abs.test/api/playlists/pl1/item/p1/e1"), urls[0]);
        Assert.Equal((HttpMethod.Delete, "https://abs.test/api/playlists/pl1/item/b1"), urls[1]);
    }

    [Fact]
    public async Task UmbenennenUndReihenfolge_PerPatch()
    {
        var (api, handler, bodies) = Client();
        using var _api = api;
        await api.RenamePlaylistAsync("pl1", "Neu", Ct);
        var items = new List<PlaylistItem> { new() { LibraryItemId = "p1", EpisodeId = "e1" }, new() { LibraryItemId = "b1" } };
        await api.ReorderPlaylistAsync("pl1", items, Ct);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Patch, r.Method));
        Assert.Equal("{\"name\":\"Neu\"}", bodies[0]);
        Assert.Equal("{\"items\":[{\"libraryItemId\":\"p1\",\"episodeId\":\"e1\"},{\"libraryItemId\":\"b1\"}]}", bodies[1]);
    }

    [Fact]
    public async Task Loeschen()
    {
        var (api, handler, _) = Client(_ => FakeHandler.Status(HttpStatusCode.OK));
        using var _api = api;
        await api.DeletePlaylistAsync("pl1", Ct);
        Assert.Equal((HttpMethod.Delete, "https://abs.test/api/playlists/pl1"), (handler.Requests.Single().Method, handler.Requests.Single().RequestUri!.ToString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task FremdeOderFehlendeListe_GibtFehlerMitStatus(HttpStatusCode status)
    {
        var (api, _, _) = Client(_ => FakeHandler.Status(status));
        using var _api = api;
        var ex = await Assert.ThrowsAsync<AbsApiException>(() => api.GetPlaylistAsync("pl1", Ct));
        Assert.Equal(status, ex.Status);
    }

    public static TheoryData<string> BoeseIds() => new() { "..", "../me", "a/b", @"a\b", "a?x=1", "a#b", "%2e%2e", "", new string('a', 65), "a b" };

    [Theory]
    [MemberData(nameof(BoeseIds))]
    public async Task BoeseId_GehtNieAnDenServer(string id)
    {
        var (api, handler, _) = Client();
        using var _api = api;
        await Assert.ThrowsAsync<AbsApiException>(() => api.GetPlaylistAsync(id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.GetPlaylistsAsync(id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.DeletePlaylistAsync(id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.RemoveFromPlaylistAsync("pl1", id, null, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.RemoveFromPlaylistAsync("pl1", "b1", id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.AddToPlaylistAsync("pl1", "b1", id, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.CreatePlaylistAsync(Lib, "x", id, null, Ct));
        await Assert.ThrowsAsync<AbsApiException>(() => api.ReorderPlaylistAsync("pl1", [new PlaylistItem { LibraryItemId = id }], Ct));
        Assert.Empty(handler.Requests);
    }

    // ---------- Regeln ----------

    [Theory]
    [InlineData("  Abend  ", "Abend")]
    [InlineData("Zwei\nZeilen\tund  Lücken", "Zwei Zeilen und Lücken")]
    [InlineData("a‮b⁦c", "a b c")] // Steuerzeichen für die Schreibrichtung
    public void Name_WirdBereinigt(string input, string expected) => Assert.Equal(expected, PlaylistRules.NormalizeName(input));

    [Fact]
    public void Name_LeerOderUnveraendert_IstNichts()
    {
        Assert.Null(PlaylistRules.NormalizeName("   "));
        Assert.Null(PlaylistRules.NormalizeName(null));
        Assert.Null(PlaylistRules.NormalizeName(" Abend ", "Abend"));
    }

    [Fact]
    public void Name_WirdGekuerzt_OhneHalbesZeichen()
    {
        var name = new string('x', PlaylistRules.MaxNameLength - 1) + "😀😀";
        var result = PlaylistRules.NormalizeName(name)!;
        Assert.True(result.Length <= PlaylistRules.MaxNameLength);
        Assert.False(char.IsHighSurrogate(result[^1]));
    }

    [Fact]
    public void Anzeige_LangerOderLeererName()
    {
        Assert.Equal("Ohne Namen", PlaylistRules.DisplayName(new Playlist { Name = " \u0000 " }));
        Assert.Equal(PlaylistRules.MaxNameLength, PlaylistRules.DisplayName(new Playlist { Name = new string('n', 100_000) }).Length);
        Assert.Equal("Zeile 1\nZeile 2", PlaylistRules.DisplayDescription(new Playlist { Description = "Zeile 1\n\u0007Zeile 2" }));
    }

    [Fact]
    public void Verfuegbar_NurMitBuchUndSchlichtenIds()
    {
        var book = new LibraryItem { Id = "b1" };
        var podcast = new LibraryItem { Id = "p1", MediaType = "podcast" };
        Assert.True(PlaylistRules.IsAvailable(new PlaylistItem { LibraryItemId = "b1", LibraryItem = book }));
        Assert.False(PlaylistRules.IsAvailable(new PlaylistItem { LibraryItemId = "b1" })); // gelöscht
        Assert.False(PlaylistRules.IsAvailable(new PlaylistItem { LibraryItemId = "../x", LibraryItem = book }));
        Assert.False(PlaylistRules.IsAvailable(new PlaylistItem { LibraryItemId = "p1", LibraryItem = podcast })); // Podcast ohne Folge
        Assert.False(PlaylistRules.IsAvailable(new PlaylistItem { LibraryItemId = "p1", EpisodeId = "e1", LibraryItem = podcast }));
        Assert.True(PlaylistRules.IsAvailable(new PlaylistItem { LibraryItemId = "p1", EpisodeId = "e1", LibraryItem = podcast, Episode = new() { Id = "e1" } }));
        Assert.Equal("Nicht mehr verfügbar", PlaylistRules.DisplayTitle(new PlaylistItem { LibraryItemId = "b1" }));
    }

    [Fact]
    public void Verschieben_PerTasteOderMenue()
    {
        var items = new List<PlaylistItem> { new() { LibraryItemId = "a" }, new() { LibraryItemId = "b" }, new() { LibraryItemId = "c" } };
        Assert.Equal(["b", "a", "c"], PlaylistRules.Move(items, 0, 1)!.Select(i => i.LibraryItemId));
        Assert.Equal(["a", "c", "b"], PlaylistRules.Move(items, 2, -1)!.Select(i => i.LibraryItemId));
        Assert.Null(PlaylistRules.Move(items, 0, -1));
        Assert.Null(PlaylistRules.Move(items, 2, 1));
        Assert.Null(PlaylistRules.Move(items, 5, -1));
        Assert.Equal(["a", "b", "c"], items.Select(i => i.LibraryItemId)); // Original unverändert
    }

    // ---------- Live-Meldungen ----------

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("playlist_added", PlaylistChangeKind.Added)]
    [InlineData("playlist_updated", PlaylistChangeKind.Updated)]
    [InlineData("playlist_removed", PlaylistChangeKind.Removed)]
    public void LiveMeldung_WirdGelesen(string name, PlaylistChangeKind kind)
    {
        var change = SocketIoProtocol.TryParsePlaylistChange(name, Payload(PlaylistJson()));
        Assert.Equal(new PlaylistChange(kind, "pl1", Lib, "u1"), change);
    }

    [Theory]
    [InlineData("playlist_updated", "{\"id\":\"../x\",\"libraryId\":\"lib1\",\"userId\":\"u1\"}")]
    [InlineData("playlist_updated", "{\"id\":\"pl1\",\"libraryId\":\"lib1\"}")]
    [InlineData("playlist_updated", "[1,2]")]
    [InlineData("collection_updated", "{\"id\":\"pl1\",\"libraryId\":\"lib1\",\"userId\":\"u1\"}")]
    public void LiveMeldung_Unbrauchbar_WirdVerworfen(string name, string json)
        => Assert.Null(SocketIoProtocol.TryParsePlaylistChange(name, Payload(json)));

    [Fact]
    public void LiveMeldung_NurFuerEigenenNutzerUndBibliothek()
    {
        var change = new PlaylistChange(PlaylistChangeKind.Updated, "pl1", Lib, "u1");
        Assert.True(PlaylistRules.Applies(change, "u1", Lib));
        Assert.False(PlaylistRules.Applies(change, "u2", Lib));
        Assert.False(PlaylistRules.Applies(change, "u1", "lib2"));
        Assert.False(PlaylistRules.Applies(change, null, Lib));
    }

    // ---------- Warteschlange ----------

    private static Playlist ListWith(params PlaylistItem[] items) => new() { Id = "pl1", Name = "Abend", Items = items.ToList() };
    private static PlaylistItem Book(string id) => new() { LibraryItemId = id, LibraryItem = new LibraryItem { Id = id } };

    [Fact]
    public void Warteschlange_SpringtUeberNichtVerfuegbares()
    {
        var queue = new PlaylistQueue(ListWith(new PlaylistItem { LibraryItemId = "weg" }, Book("a"), new PlaylistItem { LibraryItemId = "../b", LibraryItem = new() }, Book("c")), 0);
        Assert.Equal("a", queue.Current!.LibraryItemId);
        Assert.Equal("Abend · 2 von 4", queue.Describe());
        Assert.True(queue.HasNext);
        Assert.Equal("c", queue.MoveNext()!.LibraryItemId);
        Assert.False(queue.HasNext);
        Assert.Null(queue.MoveNext());
        Assert.Null(queue.MoveNext());
    }

    [Fact]
    public void Warteschlange_AbEintrag_UndMomentaufnahme()
    {
        var list = ListWith(Book("a"), Book("b"), Book("c"));
        var queue = new PlaylistQueue(list, 1);
        list.Items.Clear(); // spätere Änderung der Liste wirkt nicht auf die laufende Wiedergabe
        Assert.True(queue.IsCurrent("b", null));
        Assert.False(queue.IsCurrent("b", "e1"));
        Assert.Equal("c", queue.MoveNext()!.LibraryItemId);
    }

    [Fact]
    public void Warteschlange_OhneAbspielbares_IstLeer()
    {
        var queue = new PlaylistQueue(ListWith(new PlaylistItem { LibraryItemId = "weg" }), 0);
        Assert.Null(queue.Current);
        Assert.False(queue.HasNext);
        Assert.Null(queue.MoveNext());
    }
}
