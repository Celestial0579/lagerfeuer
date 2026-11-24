using System.Net;
using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Podcast-Folgen herunterladen (#76): Modell, Ablage im Cache, Download, Entfernen, Bereinigung.</summary>
public class EpisodeDownloadTests : IDisposable
{
    private static readonly byte[] Audio = CreateData(CachingHttpStream.ChunkSize + 300);
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] CreateData(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    private const string PodcastJson = """
        {"id":"pod","libraryId":"lib","mediaType":"podcast","media":{"metadata":{"title":"Der Podcast"},"episodes":[
          {"id":"ep1","libraryItemId":"pod","title":"Folge 1","duration":60,"audioFile":{"index":1,"ino":"11","duration":60,"mimeType":"audio/mpeg","metadata":{"filename":"f1.mp3","ext":".mp3"}}},
          {"id":"ep2","libraryItemId":"pod","title":"Folge 2","duration":70,"audioFile":{"index":1,"ino":"12","duration":70,"mimeType":"audio/mpeg","metadata":{"filename":"f2.mp3","ext":".mp3"}}}]}}
        """;

    private static LibraryItem Podcast() => JsonSerializer.Deserialize<LibraryItem>(PodcastJson, AbsApiClient.Json)!;

    private static (AbsApiClient Api, FakeHandler Handler) CreateServer()
    {
        var files = new RangeFileServer(Audio);
        var handler = new FakeHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/items/pod") return FakeHandler.Json(PodcastJson);
            if (path.StartsWith("/api/items/pod/file/")) return await new HttpMessageInvoker(files.Handler).SendAsync(req, CancellationToken.None);
            return FakeHandler.Status(HttpStatusCode.NotFound);
        });
        var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), files.Token, null);
        return (api, handler);
    }

    private static async Task<DownloadState> WaitForEndAsync(DownloadManager downloads, string itemId, string? episodeId)
    {
        for (var i = 0; i < 400; i++)
        {
            if (downloads.Get(itemId, episodeId) is { IsActive: false } state) return state;
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException("Download wurde nicht fertig.");
    }

    [Fact]
    public void Folge_WirdZumLokalenEintrag_MitDerAdresseDesStreamings()
    {
        var podcast = Podcast();
        var local = LocalItem.FromEpisode(podcast, podcast.Media.Episodes![1]);

        Assert.Equal("pod", local.LibraryItemId);
        Assert.Equal("ep2", local.EpisodeId);
        Assert.Equal("Folge 2", local.Title);
        Assert.Equal("Der Podcast", local.Author);
        Assert.Equal(70, local.Duration);
        var track = Assert.Single(local.Tracks);
        Assert.Equal("/api/items/pod/file/12", track.ContentUrl);
        Assert.Equal(1, track.Index);

        var session = local.ToOfflineSession("local-1", 5);
        Assert.Equal("ep2", session.EpisodeId);
        Assert.Equal("pod", session.LibraryItemId);
    }

    [Theory]
    [InlineData("../me", "pod")]
    [InlineData("12?x=1", "pod")]
    [InlineData("12", "../pod")]
    public void Folge_MitBoeserKennung_HatKeineTracks(string ino, string podcastId)
    {
        var podcast = Podcast();
        podcast.Id = podcastId;
        var episode = podcast.Media.Episodes![0];
        episode.AudioFile!.Ino = ino;

        Assert.Empty(LocalItem.FromEpisode(podcast, episode).Tracks);
    }

    [Fact]
    public async Task Download_Folge_LiegtImPodcastOrdnerUndIstAngeheftet()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));

        downloads.Enqueue("pod", "Folge 1", "ep1");
        var state = await WaitForEndAsync(downloads, "pod", "ep1");

        Assert.Equal(DownloadStatus.Completed, state.Status);
        Assert.Equal("pod/ep1", state.Key);
        Assert.True(cache.IsEpisodePinned("pod", "ep1"));
        Assert.False(cache.IsEpisodePinned("pod", "ep2"));
        Assert.False(cache.IsPinned("pod")); // nur die Folge, nicht der ganze Podcast
        var local = cache.GetLocalEpisode("pod", "ep1")!;
        Assert.True(cache.IsAvailableOffline(local));
        Assert.StartsWith(cache.GetItemDirectory("pod"), cache.GetTrackPath("pod", local.Tracks[0]));
        Assert.Null(cache.GetLocalItem("pod")); // kein item.json für den Podcast
        Assert.Equal(["ep1"], cache.ListLocalEpisodes().Select(e => e.EpisodeId));
        Assert.True(cache.GetEpisodeSize(local) >= Audio.Length);
    }

    [Fact]
    public async Task Download_FolgeGibtEsNichtMehr_SchlaegtVerstaendlichFehl()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));

        downloads.Enqueue("pod", null, "ep9");
        var state = await WaitForEndAsync(downloads, "pod", "ep9");

        Assert.Equal(DownloadStatus.Failed, state.Status);
        Assert.Contains("gibt es auf dem Server nicht mehr", state.Error);
        Assert.False(cache.IsEpisodePinned("pod", "ep9"));
    }

    [Theory]
    [InlineData("pod", "../ep1")]
    [InlineData("pod", "ep 1")]
    [InlineData("..", "ep1")]
    public void Download_BoeseKennung_GehtNieAnDenServer(string itemId, string episodeId)
    {
        var (api, handler) = CreateServer();
        using var _api = api;
        var downloads = new DownloadManager(api, new CacheManager(api, _dir.Path), covers: null);

        var state = downloads.Enqueue(itemId, "x", episodeId);

        Assert.Equal(DownloadStatus.Failed, state.Status);
        Assert.Null(downloads.Get(itemId, episodeId));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Entfernen_LoeschtNurDieseFolge()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));
        downloads.Enqueue("pod", null, "ep1");
        await WaitForEndAsync(downloads, "pod", "ep1");
        downloads.Enqueue("pod", null, "ep2");
        await WaitForEndAsync(downloads, "pod", "ep2");
        await Task.Delay(200, Ct); // Streams schließen ihre Dateien im Hintergrund
        var ep1Track = cache.GetTrackPath("pod", cache.GetLocalEpisode("pod", "ep1")!.Tracks[0]);

        downloads.Remove("pod", "ep1");

        Assert.False(cache.IsEpisodePinned("pod", "ep1"));
        Assert.Null(cache.GetLocalEpisode("pod", "ep1"));
        Assert.False(File.Exists(ep1Track));
        Assert.False(File.Exists(CachingHttpStream.MapPathFor(ep1Track)));
        Assert.Null(downloads.Get("pod", "ep1"));
        // Die andere Folge bleibt vollständig da.
        Assert.True(cache.IsEpisodePinned("pod", "ep2"));
        Assert.True(cache.IsAvailableOffline(cache.GetLocalEpisode("pod", "ep2")!));

        downloads.Remove("pod", "ep2");
        Assert.False(Directory.Exists(cache.GetItemDirectory("pod"))); // leer, also weg
    }

    [Fact]
    public async Task Entfernen_ZugriffVerweigert_FolgeBleibtUndNenntDenGrund()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));
        downloads.Enqueue("pod", null, "ep1");
        await WaitForEndAsync(downloads, "pod", "ep1");
        await Task.Delay(200, Ct);
        var track = cache.GetTrackPath("pod", cache.GetLocalEpisode("pod", "ep1")!.Tracks[0]);

        string? reason;
        using (DeleteDenied.For(track))
            reason = downloads.Remove("pod", "ep1");

        Assert.Equal("Zugriff verweigert", reason);
        Assert.True(cache.IsEpisodePinned("pod", "ep1"));
        Assert.True(cache.IsAvailableOffline(cache.GetLocalEpisode("pod", "ep1")!));

        Assert.Null(downloads.Remove("pod", "ep1"));
        Assert.False(Directory.Exists(cache.GetItemDirectory("pod")));
    }

    [Fact]
    public async Task Bereinigung_SchontAngehefteteFolgen_UndLoeschtDenRestDesPodcasts()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));
        downloads.Enqueue("pod", null, "ep1");
        await WaitForEndAsync(downloads, "pod", "ep1");
        downloads.Enqueue("pod", null, "ep2");
        await WaitForEndAsync(downloads, "pod", "ep2");
        await Task.Delay(200, Ct);
        cache.SetEpisodePinned("pod", "ep2", false); // Folge 2 ist jetzt nur noch Streaming-Cache
        var ep2Track = cache.GetTrackPath("pod", cache.GetLocalEpisode("pod", "ep2")!.Tracks[0]);

        cache.EnforceLimit(0, []);

        Assert.True(cache.IsAvailableOffline(cache.GetLocalEpisode("pod", "ep1")!));
        Assert.True(cache.IsEpisodePinned("pod", "ep1"));
        Assert.False(File.Exists(ep2Track));
        Assert.Null(cache.GetLocalEpisode("pod", "ep2"));
    }

    [Fact]
    public async Task Bereinigung_UnlesbareDatenEinerAngeheftetenFolge_BehaeltDenOrdner()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));
        downloads.Enqueue("pod", null, "ep1");
        await WaitForEndAsync(downloads, "pod", "ep1");
        await Task.Delay(200, Ct);
        var dir = cache.GetItemDirectory("pod");
        var track = cache.GetTrackPath("pod", cache.GetLocalEpisode("pod", "ep1")!.Tracks[0]);
        var streamed = Path.Combine(dir, "002_gestreamt.mp3");
        File.WriteAllBytes(streamed, [1, 2, 3]);
        File.WriteAllText(Path.Combine(dir, "episode_ep1.json"), "{kaputt");

        cache.EnforceLimit(0, []);

        // Lieber den ganzen Ordner behalten als eine heruntergeladene Folge löschen.
        Assert.True(File.Exists(track));
        Assert.True(File.Exists(streamed));
        Assert.True(cache.IsEpisodePinned("pod", "ep1"));
    }

    [Fact]
    public async Task Bereinigung_GestreamteDateiGeschuetzt_RaeumtDieUebrigenOrdnerTrotzdemAuf()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));
        downloads.Enqueue("pod", null, "ep1");
        await WaitForEndAsync(downloads, "pod", "ep1");
        await Task.Delay(200, Ct);
        var podcast = cache.GetItemDirectory("pod");
        var streamed = Path.Combine(podcast, "002_gestreamt.mp3");
        File.WriteAllBytes(streamed, [1, 2, 3]);
        foreach (var file in Directory.GetFiles(podcast)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        var other = Directory.CreateDirectory(Path.Combine(_dir.Path, "anderes")).FullName;
        File.WriteAllBytes(Path.Combine(other, "001_track.mp3"), new byte[100]);
        File.SetLastWriteTimeUtc(Path.Combine(other, "001_track.mp3"), DateTime.UtcNow.AddDays(-2));

        using (DeleteDenied.For(streamed))
            cache.EnforceLimit(0, []);

        // Bis #85 brach die Bereinigung am Podcast ab, und das Buch danach blieb liegen.
        Assert.False(Directory.Exists(other));
        Assert.True(File.Exists(streamed));
        Assert.True(cache.IsAvailableOffline(cache.GetLocalEpisode("pod", "ep1")!));
    }

    [Fact]
    public void Liste_IgnoriertFolgendatenDieNichtZumDateinamenPassen()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var podcast = Podcast();
        cache.SaveLocalEpisode(LocalItem.FromEpisode(podcast, podcast.Media.Episodes![0]));
        // Eine Datei, deren Inhalt eine andere Folge behauptet, als ihr Name sagt.
        var dir = cache.GetItemDirectory("pod");
        File.WriteAllText(Path.Combine(dir, "episode_ep7.json"), JsonSerializer.Serialize(new LocalItem { LibraryItemId = "pod", EpisodeId = "ep8" }, AbsApiClient.Json));

        Assert.Equal(["ep1"], cache.ListLocalEpisodes().Select(e => e.EpisodeId));
        Assert.Null(cache.GetLocalEpisode("pod", "ep7"));
    }

    [Fact]
    public void Ablage_FolgenIdAlsGeraetename_WirdGehasht()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        cache.SetEpisodePinned("pod", "CON", true);

        var files = Directory.GetFiles(cache.GetItemDirectory("pod")).Select(Path.GetFileName).ToList();
        Assert.Single(files);
        Assert.Matches(@"\A\.pinned_h_[0-9a-f]{32}\z", files[0]!);
        Assert.True(cache.IsEpisodePinned("pod", "CON"));
    }

    public void Dispose() => _dir.Dispose();
}

public class DownloadKeyTests
{
    [Theory]
    [InlineData("buch", null, "buch")]
    [InlineData("pod", "ep1", "pod/ep1")]
    public void Schluessel_HinUndZurueck(string itemId, string? episodeId, string key)
    {
        Assert.Equal(key, DownloadManager.Key(itemId, episodeId));
        Assert.Equal((itemId, episodeId), DownloadManager.ParseKey(key));
    }
}

public class EpisodeDeleteTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Entfernen_OhneOrdner_TutNichts()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(System.Net.HttpStatusCode.NotFound)));
        var cache = new CacheManager(api, _dir.Path);

        cache.DeleteEpisode("pod", "ep1"); // kein Ordner, keine Ausnahme

        Assert.False(Directory.Exists(cache.GetItemDirectory("pod")));
    }

    public void Dispose() => _dir.Dispose();
}
