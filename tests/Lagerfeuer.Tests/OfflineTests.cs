using System.Net;
using System.Text;
using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class LocalItemTests
{
    private static LibraryItem Parse(string json) => JsonSerializer.Deserialize<LibraryItem>(json, AbsApiClient.Json)!;

    [Fact]
    public void Trackliste_WieDirectPlayDesServers()
    {
        var item = Parse("""
            {"id":"buch","media":{"duration":0,"metadata":{"title":"Titel","authorName":"Autorin","narratorName":"Sprecher","description":"<p>Text</p>"},
             "chapters":[{"id":0,"start":0,"end":30,"title":"K1"}],
             "audioFiles":[
               {"index":2,"ino":"22","duration":20.5,"mimeType":"audio/mpeg","metadata":{"filename":"b.mp3","ext":".mp3","size":2000}},
               {"index":1,"ino":"11","duration":10,"mimeType":"audio/mpeg","metadata":{"filename":"a.mp3","ext":".mp3","size":1000}},
               {"index":3,"ino":"33","duration":99,"mimeType":"audio/mpeg","exclude":true,"metadata":{"filename":"bonus.mp3","ext":".mp3"}}]}}
            """);

        var local = LocalItem.FromLibraryItem(item);

        Assert.Equal(["a.mp3", "b.mp3"], local.Tracks.Select(t => t.Title)); // sortiert, ausgeschlossene fehlen
        Assert.Equal([0, 10], local.Tracks.Select(t => t.StartOffset));
        Assert.Equal("/api/items/buch/file/11", local.Tracks[0].ContentUrl);
        Assert.Equal(30.5, local.Duration); // aus den Tracks, da media.duration fehlt
        Assert.Equal("Autorin", local.Author);
        Assert.Equal("Sprecher", local.Narrator);
        Assert.Single(local.Chapters);
    }

    [Fact]
    public void OfflineSession_StartetAnDerGespeichertenPosition()
    {
        var local = new LocalItem
        {
            LibraryItemId = "buch",
            Title = "Titel",
            Author = "Autorin",
            Duration = 100,
            Tracks = [new AudioTrack { Index = 1, Duration = 100, ContentUrl = "/api/items/buch/file/1" }],
        };

        var session = local.ToOfflineSession("local-1", 42);

        Assert.Equal("local-1", session.Id);
        Assert.Equal(42, session.CurrentTime);
        Assert.Equal("Titel", session.DisplayTitle);
        Assert.Single(session.AudioTracks);
    }
}

public class LocalSessionStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string StorePath => Path.Combine(_dir.Path, "offline-sessions.json");

    [Fact]
    public void Sessions_UeberlebenDenNeustart()
    {
        var store = new LocalSessionStore(StorePath);
        var session = new LocalSession { Id = "s1", LibraryItemId = "buch", CurrentTime = 10, TimeListening = 5 };
        store.Upsert(session);
        session.CurrentTime = 20;
        store.Upsert(session); // gleiche Id → aktualisiert

        var reloaded = new LocalSessionStore(StorePath);
        var s = Assert.Single(reloaded.Pending);
        Assert.Equal(20, s.CurrentTime);
    }

    [Fact]
    public async Task Sync_EntferntUebertrageneUndBehaeltAbgelehnte()
    {
        string? body = null;
        var handler = new FakeHandler(async req =>
        {
            Assert.Equal("/api/session/local-all", req.RequestUri!.AbsolutePath);
            body = await req.Content!.ReadAsStringAsync();
            return FakeHandler.Json("""{"results":[{"id":"ok","success":true,"progressSynced":true},{"id":"kaputt","success":false,"error":"Invalid"}]}""");
        });
        using var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        var store = new LocalSessionStore(StorePath);
        store.Upsert(new LocalSession { Id = "ok", LibraryItemId = "a", TimeListening = 60 });
        store.Upsert(new LocalSession { Id = "kaputt", LibraryItemId = "b" });

        var synced = await store.SyncAsync(api, "device-1", TestContext.Current.CancellationToken);

        Assert.Equal(1, synced);
        Assert.Equal("kaputt", Assert.Single(store.Pending).Id);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("device-1", json.RootElement.GetProperty("deviceInfo").GetProperty("deviceId").GetString());
        var first = json.RootElement.GetProperty("sessions")[0];
        Assert.Equal(3, first.GetProperty("playMethod").GetInt32());
        Assert.Equal(60, first.GetProperty("timeListening").GetDouble());
        Assert.Equal("device-1", first.GetProperty("deviceInfo").GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task Sync_OhneOffeneSessions_FragtDenServerNicht()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.InternalServerError));
        using var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);

        Assert.Equal(0, await new LocalSessionStore(StorePath).SyncAsync(api, "d", TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    public void Dispose() => _dir.Dispose();
}

public class ProgressStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Fortschritt_WirdGespeichertUndGeladen()
    {
        var path = Path.Combine(_dir.Path, "progress.json");
        var store = new ProgressStore(path);
        store.Reset([new MediaProgress { LibraryItemId = "buch", CurrentTime = 10, Duration = 100, Progress = 0.1 }]);
        store.Update("podcast", 30, 60, episodeId: "folge1");

        var reloaded = new ProgressStore(path);
        Assert.Equal(10, reloaded.Get("buch")!.CurrentTime);
        Assert.Equal(0.5, reloaded.Get("podcast", "folge1")!.Progress);
        Assert.Null(reloaded.Get("podcast")); // Folge, nicht der ganze Podcast
    }

    [Fact]
    public void Update_AmEnde_MarkiertAlsBeendet()
    {
        var store = new ProgressStore();
        string? changed = null;
        store.Changed += (_, key) => changed = key;

        store.Update("buch", 99.5, 100);

        Assert.True(store.Get("buch")!.IsFinished);
        Assert.Equal("buch", changed);
    }

    public void Dispose() => _dir.Dispose();
}

public class DownloadManagerTests : IDisposable
{
    private static readonly byte[] Audio = CreateData(CachingHttpStream.ChunkSize * 2 + 500);
    private readonly TempDir _dir = new();

    private static byte[] CreateData(int length)
    {
        var data = new byte[length];
        new Random(1).NextBytes(data);
        return data;
    }

    /// <summary>Server mit einem Buch aus zwei Dateien (Item-Details + Dateien mit Range-Support).</summary>
    private static (AbsApiClient Api, FakeHandler Handler) CreateServer(bool withAudio = true)
    {
        var files = new RangeFileServer(Audio);
        var itemJson = withAudio
            ? """{"id":"buch","media":{"duration":200,"metadata":{"title":"Das Buch"},"chapters":[],"audioFiles":[{"index":1,"ino":"1","duration":100,"mimeType":"audio/mpeg","metadata":{"ext":".mp3"}},{"index":2,"ino":"2","duration":100,"mimeType":"audio/mpeg","metadata":{"ext":".mp3"}}]}}"""
            : """{"id":"buch","media":{"metadata":{"title":"Nur E-Book"},"audioFiles":[]}}""";
        var handler = new FakeHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/items/buch") return FakeHandler.Json(itemJson);
            if (path.StartsWith("/api/items/buch/file/")) return await new HttpMessageInvoker(files.Handler).SendAsync(req, CancellationToken.None);
            return FakeHandler.Status(HttpStatusCode.NotFound);
        });
        var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), files.Token, null);
        return (api, handler);
    }

    private static async Task<DownloadState> WaitForEndAsync(DownloadManager downloads, string itemId)
    {
        for (var i = 0; i < 400; i++)
        {
            if (downloads.Get(itemId) is { IsActive: false } state) return state;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Download wurde nicht fertig.");
    }

    [Fact]
    public async Task Download_LaedtAllesUndHeftetAn()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));
        var fractions = new List<double>();
        downloads.Changed += (_, s) => { lock (fractions) fractions.Add(s.Fraction); };

        downloads.Enqueue("buch");
        var state = await WaitForEndAsync(downloads, "buch");

        Assert.Equal(DownloadStatus.Completed, state.Status);
        Assert.Equal("Das Buch", state.Title);
        Assert.True(cache.IsPinned("buch"));
        var local = cache.GetLocalItem("buch")!;
        Assert.Equal(2, local.Tracks.Count);
        Assert.True(cache.IsAvailableOffline(local));
        lock (fractions) Assert.Equal(1.0, fractions.Last());
    }

    [Fact]
    public async Task BuchOhneAudio_SchlaegtVerstaendlichFehl()
    {
        var (api, _) = CreateServer(withAudio: false);
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));

        downloads.Enqueue("buch");
        var state = await WaitForEndAsync(downloads, "buch");

        Assert.Equal(DownloadStatus.Failed, state.Status);
        Assert.Contains("keine Audiodateien", state.Error);
        Assert.False(cache.IsPinned("buch"));
    }

    [Fact]
    public async Task Entfernen_LoeschtDateienUndAnheftung()
    {
        var (api, _) = CreateServer();
        using var _api = api;
        var cache = new CacheManager(api, _dir.Path);
        var downloads = new DownloadManager(api, cache, covers: null, pollInterval: TimeSpan.FromMilliseconds(20));
        downloads.Enqueue("buch");
        await WaitForEndAsync(downloads, "buch");
        await Task.Delay(200, TestContext.Current.CancellationToken); // Streams schließen ihre Dateien im Hintergrund

        downloads.Remove("buch");

        Assert.False(cache.IsPinned("buch"));
        Assert.Null(cache.GetLocalItem("buch"));
        Assert.Null(downloads.Get("buch"));
    }

    public void Dispose() => _dir.Dispose();
}
