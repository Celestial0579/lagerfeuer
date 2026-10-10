using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class CacheManagerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AbsApiClient _api = new(new FakeHandler(_ => FakeHandler.Status(System.Net.HttpStatusCode.OK)));

    private CacheManager Cache => new(_api, _dir.Path);

    private static AudioTrack Track(int index, string url, string? ext = null, string? mime = null) => new()
    {
        Index = index,
        ContentUrl = url,
        MimeType = mime,
        Metadata = ext is null ? null : new AudioTrackMetadata { Ext = ext },
    };

    [Fact]
    public void TrackPfad_IstStabilUndJeDateiEindeutig()
    {
        var a = Cache.GetTrackPath("item1", Track(1, "/api/items/item1/file/100", ".mp3"));
        var b = Cache.GetTrackPath("item1", Track(1, "/api/items/item1/file/100", ".mp3"));
        var c = Cache.GetTrackPath("item1", Track(2, "/api/items/item1/file/101", ".mp3"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith(Path.Combine(_dir.Path, "item1"), a);
        Assert.EndsWith(".mp3", a);
    }

    [Theory]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("audio/x-m4b", ".m4b")]
    [InlineData("audio/flac", ".flac")]
    [InlineData(null, ".bin")]
    public void Dateiendung_AusMimeTypOhneMetadaten(string? mime, string expected)
        => Assert.EndsWith(expected, Cache.GetTrackPath("item", Track(1, "/x", mime: mime)));

    [Fact]
    public void Bereinigung_LoeschtAelteste_UndSchontGeschuetzte()
    {
        var now = DateTime.UtcNow;
        CreateItem("alt", 3000, now.AddDays(-3));
        CreateItem("mittel", 3000, now.AddDays(-2));
        CreateItem("geschuetzt", 3000, now.AddDays(-10));
        CreateItem("neu", 3000, now);

        Cache.EnforceLimit(7000, ["geschuetzt"]);

        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "alt")));
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "mittel")));
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "geschuetzt")));
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "neu")));
    }

    [Fact]
    public void LokaleBuchdaten_WerdenGespeichertUndGelistet()
    {
        var item = new LocalItem { LibraryItemId = "item9", Title = "Zweites", Chapters = [new Chapter { Id = 0, Start = 0, End = 60, Title = "Eins" }] };
        Cache.SaveLocalItem(item);
        Cache.SaveLocalItem(new LocalItem { LibraryItemId = "item1", Title = "Erstes" });

        var loaded = Cache.GetLocalItem("item9");
        Assert.Equal("Eins", Assert.Single(loaded!.Chapters).Title);
        Assert.Equal(["Erstes", "Zweites"], Cache.ListLocalItems().Select(i => i.Title));
        Assert.Null(Cache.GetLocalItem("unbekannt"));
    }

    [Fact]
    public void AngehefteteBuecher_UeberlebenDieBereinigung()
    {
        CreateItem("download", 5000, DateTime.UtcNow.AddDays(-30));
        CreateItem("gestreamt", 5000, DateTime.UtcNow.AddDays(-1));
        Cache.SetPinned("download", true);

        Cache.Clear([]);

        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "download")));
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "gestreamt")));
        Assert.True(Cache.IsPinned("download"));

        Cache.SetPinned("download", false);
        Assert.False(Cache.IsPinned("download"));
    }

    [Fact]
    public void CacheSchluessel_IstUnabhaengigVomServerUnterpfad()
    {
        var mitUnterpfad = Cache.GetTrackPath("i", Track(1, "/abs/api/items/i/file/5", ".mp3"));
        var ohne = Cache.GetTrackPath("i", Track(1, "/api/items/i/file/5", ".mp3"));
        Assert.Equal(mitUnterpfad, ohne);
    }

    [Fact]
    public async Task OfflineVerfuegbar_ErstWennAlleTracksKomplettSind()
    {
        var audio = new byte[CachingHttpStream.ChunkSize + 100];
        var server = new RangeFileServer(audio);
        using var api = new AbsApiClient(server.Handler);
        api.Configure(new Uri("https://abs.test/"), server.Token, null);
        var cache = new CacheManager(api, _dir.Path);
        var item = new LocalItem
        {
            LibraryItemId = "buch",
            Tracks = [Track(1, "/api/items/buch/file/1", ".mp3"), Track(2, "/api/items/buch/file/2", ".mp3")],
        };
        item.Tracks[0].Duration = item.Tracks[1].Duration = 100;

        Assert.False(cache.IsAvailableOffline(item));
        Assert.Equal(0, cache.GetLocalFraction(item));

        await DownloadCompletelyAsync(cache, item, item.Tracks[0]);
        Assert.False(cache.IsAvailableOffline(item));
        Assert.Equal(0.5, cache.GetLocalFraction(item), 3);

        await DownloadCompletelyAsync(cache, item, item.Tracks[1]);
        Assert.True(cache.IsAvailableOffline(item));
    }

    private static async Task DownloadCompletelyAsync(CacheManager cache, LocalItem item, AudioTrack track)
    {
        await using var stream = await cache.OpenTrackAsync(item.LibraryItemId, track, TestContext.Current.CancellationToken);
        for (var i = 0; i < 200 && !stream.IsPersisted; i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(stream.IsPersisted);
    }
    private void CreateItem(string id, int bytes, DateTime lastUse)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir.Path, id));
        var file = Path.Combine(dir.FullName, "001_track.mp3");
        File.WriteAllBytes(file, new byte[bytes]);
        File.SetLastWriteTimeUtc(file, lastUse);
    }

    public void Dispose()
    {
        _api.Dispose();
        _dir.Dispose();
    }
}
