using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class LibraryQueryTests
{
    [Fact]
    public void Standard_SortiertNachTitel()
        => Assert.Equal("sort=media.metadata.title", LibraryQuery.Default.ToQueryString());

    [Fact]
    public void Absteigend_UndFortschrittsfilter()
    {
        var query = new LibraryQuery(LibraryQuery.SortAdded, Descending: true, FilterGroup: "progress", FilterValue: "in-progress");
        Assert.Equal("sort=addedAt&desc=1&filter=progress.aW4tcHJvZ3Jlc3M%3D", query.ToQueryString());
    }

    [Fact]
    public void FilterWert_WirdBase64UndUrlKodiert()
    {
        // Werte, deren Base64 "+" und "/" enthält, dürfen nicht roh in der URL stehen
        var value = "Ö>?~";
        var encoded = LibraryQuery.EncodeFilter("series", value);

        Assert.StartsWith("series.", encoded);
        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded[7..]);
        var base64 = Uri.UnescapeDataString(encoded["series.".Length..]);
        Assert.Equal(value, Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
    }
}

public class SeriesAndPodcastModelTests
{
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, AbsApiClient.Json)!;

    [Fact]
    public void Serie_AlsListe_ImErweitertenItem()
    {
        var meta = Parse<BookMetadata>("""{"title":"T","series":[{"id":"s1","name":"Saga","sequence":"2"},{"id":"s2","name":"Zyklus","sequence":null}]}""");
        Assert.Equal(["Saga", "Zyklus"], meta.Series.Select(s => s.Name));
        Assert.Equal("2", meta.Series[0].Sequence);
    }

    [Fact]
    public void Serie_AlsEinzelobjekt_BeimSerienfilter()
    {
        var meta = Parse<BookMetadata>("""{"title":"T","series":{"id":"s1","name":"Saga","sequence":"2.5"}}""");
        Assert.Equal("2.5", Assert.Single(meta.Series).Sequence);
    }

    [Fact]
    public void Serie_FehltOderNull()
    {
        Assert.Empty(Parse<BookMetadata>("""{"title":"T"}""").Series);
        Assert.Empty(Parse<BookMetadata>("""{"title":"T","series":null}""").Series);
    }

    [Fact]
    public void Podcast_MitFolgen()
    {
        var item = Parse<LibraryItem>("""
            {"id":"p","mediaType":"podcast","media":{"numEpisodes":2,
              "metadata":{"title":"Der Podcast","author":"Redaktion","description":"<b>Info</b>"},
              "episodes":[
                {"id":"e1","libraryItemId":"p","title":"Folge 1","pubDate":"Mon, 01 Sep 2025","publishedAt":1756684800000,"audioFile":{"index":1,"ino":"9","duration":1800}},
                {"id":"e2","libraryItemId":"p","title":"Folge 2","publishedAt":null,"duration":2400.5,"season":"1","episode":"2"}]}}
            """);

        Assert.True(item.IsPodcast);
        Assert.Equal("Redaktion", item.Media.Metadata.DisplayAuthor);
        Assert.Equal(1800, item.Media.Episodes![0].DisplayDuration); // aus audioFile
        Assert.Equal(2400.5, item.Media.Episodes[1].DisplayDuration);
        Assert.Equal(new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero), item.Media.Episodes[0].Published);
        Assert.Null(item.Media.Episodes[1].Published);
    }

    [Fact]
    public void Weiterhoeren_PodcastMitAngefangenerFolge()
    {
        var item = Parse<LibraryItem>("""{"id":"p","mediaType":"podcast","media":{"metadata":{"title":"Der Podcast"}},"recentEpisode":{"id":"e7","title":"Folge 7"}}""");
        Assert.Equal("e7", item.RecentEpisode!.Id);
    }
}

public class LibraryApiTests
{
    private static (AbsApiClient Api, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        return (api, handler);
    }

    [Fact]
    public async Task LesezeichenLoeschen_ZeitKulturunabhaengigUndVerlustfrei()
    {
        var (api, handler) = Create(_ => FakeHandler.Status(HttpStatusCode.OK));
        using var _ = api;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // würde "12,3" erzeugen
            await api.DeleteBookmarkAsync("buch", 12.3, TestContext.Current.CancellationToken);
            await api.DeleteBookmarkAsync("buch", 1234.5678901, TestContext.Current.CancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var paths = handler.Requests.Select(r => r.RequestUri!.AbsolutePath).ToList();
        Assert.Equal("/api/me/item/buch/bookmark/12.3", paths[0]);
        Assert.Equal("/api/me/item/buch/bookmark/1234.5678901", paths[1]);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Delete, r.Method));
    }

    [Fact]
    public async Task LesezeichenAnlegen_SendetZeitAlsZahl()
    {
        string? body = null;
        var handler = new FakeHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return FakeHandler.Json("""{"libraryItemId":"buch","title":"Kapitel 3","time":61.5,"createdAt":1}""");
        });
        using var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);

        var bookmark = await api.CreateBookmarkAsync("buch", 61.5, "Kapitel 3", TestContext.Current.CancellationToken);

        Assert.Equal("/api/me/item/buch/bookmark", handler.Requests.Single().RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("time").ValueKind);
        Assert.Equal(61.5, bookmark.Time);
    }

    [Fact]
    public async Task PodcastFolge_EigenerPlayUndProgressPfad()
    {
        var (api, handler) = Create(req => req.Method == HttpMethod.Post
            ? FakeHandler.Json("""{"id":"s","libraryItemId":"p","episodeId":"e1","audioTracks":[],"chapters":[]}""")
            : FakeHandler.Status(HttpStatusCode.OK));
        using var _ = api;

        var session = await api.StartPlaybackAsync("p", "dev", "e1", TestContext.Current.CancellationToken);
        await api.UpdateProgressAsync("p", 10, 100, false, "e1", TestContext.Current.CancellationToken);
        await api.UpdateProgressAsync("buch", 10, 100, false, ct: TestContext.Current.CancellationToken);

        var paths = handler.Requests.Select(r => r.RequestUri!.AbsolutePath).ToList();
        Assert.Equal(["/api/items/p/play/e1", "/api/me/progress/p/e1", "/api/me/progress/buch"], paths);
        Assert.Equal("e1", session.EpisodeId);
    }

    [Fact]
    public async Task SerienBuecher_MitKodiertemSerienfilter()
    {
        var (api, handler) = Create(_ => FakeHandler.Json("""{"results":[{"id":"b1","media":{"metadata":{"title":"Band 1","series":{"id":"s1","name":"Saga","sequence":"1"}}}}],"total":1}"""));
        using var _ = api;

        var books = await api.GetSeriesBooksAsync("lib", "s1", TestContext.Current.CancellationToken);

        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.Contains("filter=" + LibraryQuery.EncodeFilter("series", "s1"), query);
        Assert.Equal("1", Assert.Single(books).Media.Metadata.Series.Single().Sequence);
    }

    [Fact]
    public async Task Suche_LiefertAuchPodcasts()
    {
        var (api, _) = Create(_ => FakeHandler.Json("""{"book":[],"podcast":[{"libraryItem":{"id":"p1","mediaType":"podcast"}}]}"""));
        using var __ = api;

        var results = await api.SearchAsync("lib", "news", TestContext.Current.CancellationToken);

        Assert.Equal("p1", Assert.Single(results).Id);
    }

    [Fact]
    public async Task Autor_MitBuechernUndSerien()
    {
        var (api, handler) = Create(_ => FakeHandler.Json("""
            {"id":"a1","name":"Autorin","description":"Bio","libraryItems":[{"id":"b1"},{"id":"b2"}],
             "series":[{"id":"s1","name":"Saga","items":[{"id":"b1","media":{"metadata":{"series":{"id":"s1","name":"Saga","sequence":"1"}}}}]}]}
            """));
        using var _ = api;

        var author = await api.GetAuthorAsync("a1", TestContext.Current.CancellationToken);

        Assert.Equal("?include=items,series", handler.Requests.Single().RequestUri!.Query);
        Assert.Equal(2, author.LibraryItems.Count);
        Assert.Equal("1", author.Series.Single().Items.Single().Media.Metadata.Series.Single().Sequence);
    }
}
