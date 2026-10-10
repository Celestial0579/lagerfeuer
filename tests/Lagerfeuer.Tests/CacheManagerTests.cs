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
    public void SessionMetadaten_WerdenFuerOfflineGespeichert()
    {
        var session = new PlaybackSession { Id = "s1", LibraryItemId = "item9", DisplayTitle = "Titel", Chapters = [new Chapter { Id = 0, Start = 0, End = 60, Title = "Eins" }] };

        Cache.SaveSessionMetadata(session);

        var json = File.ReadAllText(Path.Combine(_dir.Path, "item9", "session.json"));
        Assert.Contains("\"Eins\"", json);
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
