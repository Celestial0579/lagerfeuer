using System.Net;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// GHSA-wg65-jc44-jwjf: Angaben des Servers (Item-ID, Autor-ID, Dateiendung eines Tracks) dürfen keinen Pfad außerhalb
/// des Cache-Ordners ergeben. Die Serverantworten hier sind erfunden.
/// </summary>
public sealed class CachePathsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string Root => Path.Combine(_dir.Path, "cache");
    private string Outside => _dir.Path;

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> BoeseIds() => new()
    {
        "..", ".", "", "../..", @"..\..", "/etc/passwd", @"C:\Users\u\Pictures\urlaub", @"\\server\share\x",
        "a/../../b", @"a\..\..\b", "li_ok/../..", "x:y", new string('a', 65),
        // Gerätenamen von Windows, auch klein und mit Endung (Review zu GHSA-wg65)
        "CON", "con", "PRN", "AUX", "NUL", "nul", "COM1", "com9", "LPT1", "lpt9", "NUL.txt", "con.jpg",
    };

    public static TheoryData<string> BoeseEndungen() => new()
    {
        @"mp3\..\..\..\..\..\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.cmd",
        "mp3/../../../../x.cmd", @".mp3\..\..\x", "../x", "..", ".", @"C:\x.cmd", @"\\server\share\x", ".mp3 ", ".m p3",
        ".mp3:stream", ".abcdefghi",
        // schlicht, aber kein Audio: im Explorer gefährlich (zweites Review zu GHSA-wg65)
        ".cmd", "cmd", ".CMD", ".url", "URL", ".lnk", ".Lnk", ".scf", ".SCF", ".hta", "Hta", ".exe", ".bat", ".ps1",
        ".js", ".vbs", ".html", ".txt", ".jpg",
    };

    [Theory]
    [InlineData("li_8a7b6c5d4e3f2a1b0c9d")]
    [InlineData("4d3f2c1b-aaaa-bbbb-cccc-0123456789ab")]
    [InlineData("buch")]
    [InlineData("console")]
    [InlineData("com10")]
    [InlineData("nuller")]
    public void SchlichteId_BleibtUnveraendert(string id) => Assert.Equal(id, CachePaths.SafeId(id));

    [Theory]
    [MemberData(nameof(BoeseIds))]
    public void AndereId_WirdZumHash(string id)
    {
        var safe = CachePaths.SafeId(id);
        Assert.Matches(@"\Ah_[0-9a-f]{32}\z", safe);
        Assert.Equal(safe, CachePaths.SafeId(id)); // stabil
    }

    [Theory]
    [InlineData(".mp3", "audio/mpeg", ".mp3")]
    [InlineData("M4B", null, ".m4b")]
    [InlineData(null, "audio/flac", ".flac")]
    [InlineData("", "audio/ogg", ".ogg")]
    [InlineData(null, null, ".bin")]
    [InlineData(".OPUS", null, ".opus")]
    [InlineData("oga", null, ".oga")]
    [InlineData(".mp4", null, ".mp4")]
    [InlineData(".url", null, ".bin")]
    [InlineData(".cmd", "text/plain", ".bin")]
    public void Endung_SchlichtOderAusMime(string? ext, string? mime, string expected)
        => Assert.Equal(expected, CachePaths.SafeExtension(ext, mime));

    [Theory]
    [MemberData(nameof(BoeseEndungen))]
    public void BoeseEndung_FaelltAufMimeZurueck(string ext)
        => Assert.Equal(".mp3", CachePaths.SafeExtension(ext, "audio/mpeg"));

    [Fact]
    public void Within_LehntPfadeAusserhalbAb()
    {
        Assert.Throws<InvalidOperationException>(() => CachePaths.Within(Root, Path.Combine(Root, "..", "x")));
        Assert.Throws<InvalidOperationException>(() => CachePaths.Within(Root, Root));
        Assert.Throws<InvalidOperationException>(() => CachePaths.Within(Root, Root + "-nachbar" + Path.DirectorySeparatorChar + "x"));
        Assert.Throws<InvalidOperationException>(() => CachePaths.Within(Root, Path.Combine(Outside, "x")));
        Assert.Equal(Path.Combine(Root, "a", "b"), CachePaths.Within(Root, Path.Combine(Root, "a", "x", "..", "b")));
    }

    private static AudioTrack Track(string ext, int index = 1) => new()
    {
        Index = index,
        ContentUrl = "/api/items/buch/file/1",
        MimeType = "audio/mpeg",
        Metadata = new AudioTrackMetadata { Ext = ext },
    };

    private static string Full(string p) => Path.GetFullPath(p);

    private bool IsInsideRoot(string path)
        => Full(path).StartsWith(Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(BoeseEndungen))]
    public void TrackPfad_BleibtImCache(string ext)
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));
        var cache = new CacheManager(api, Root);
        var path = cache.GetTrackPath("buch", Track(ext));
        Assert.True(IsInsideRoot(path), path);
        Assert.EndsWith(".mp3", path);
    }

    [Theory]
    [MemberData(nameof(BoeseIds))]
    public void BuchOrdner_BleibtImCache(string id)
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));
        var cache = new CacheManager(api, Root);
        Assert.True(IsInsideRoot(cache.GetItemDirectory(id)));
        Assert.True(IsInsideRoot(cache.GetTrackPath(id, Track(".mp3"))));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(100000)]
    public void TrackIndex_AusserhalbDesBereichs_BleibtImCache(int index)
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));
        var cache = new CacheManager(api, Root);
        Assert.True(IsInsideRoot(cache.GetTrackPath("buch", Track(".mp3", index))));
    }

    [Fact]
    public void BuchLoeschen_MitIdPunktPunkt_LaesstDenCacheOrdnerStehen()
    {
        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound)));
        var cache = new CacheManager(api, Root);
        Directory.CreateDirectory(Path.Combine(Root, "anderes-buch"));
        File.WriteAllText(Path.Combine(Outside, "anmeldung.bin"), "geheim");

        cache.DeleteItem("..");
        cache.DeleteItem(".");

        Assert.True(Directory.Exists(Path.Combine(Root, "anderes-buch")));
        Assert.True(File.Exists(Path.Combine(Outside, "anmeldung.bin")));
    }

    [Fact]
    public async Task Streamen_MitBoeserEndung_SchreibtNurImCache()
    {
        var audio = new byte[CachingHttpStream.ChunkSize + 100];
        var server = new RangeFileServer(audio);
        using var api = new AbsApiClient(server.Handler);
        api.Configure(new Uri("https://abs.test/"), server.Token, null);
        var cache = new CacheManager(api, Root);
        var track = Track(@"mp3\..\..\..\ausserhalb.cmd");
        track.Metadata!.Ext = "mp3/../../../ausserhalb.cmd"; // auf jedem System ein Pfadtrenner

        await using (var stream = await cache.OpenTrackAsync("buch", track, TestContext.Current.CancellationToken))
        {
            for (var i = 0; i < 200 && !stream.IsPersisted; i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(Outside, "*", SearchOption.AllDirectories), f => !IsInsideRoot(f));
        Assert.NotEmpty(Directory.EnumerateFiles(Root, "*.mp3", SearchOption.AllDirectories));
    }

    [Theory]
    [MemberData(nameof(BoeseIds))]
    public async Task Cover_MitBoeserId_LandetImCoverOrdner(string id)
    {
        var covers = Path.Combine(_dir.Path, "covers");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        var cache = new CoverCache(api, covers);

        var cover = await cache.GetCoverPathAsync(id);
        var author = await cache.GetAuthorImagePathAsync(id);

        foreach (var p in new[] { cover, author })
        {
            if (!PlaylistRules.IsPlainId(id))
            {
                // keine schlichte ID: keine Anfrage, kein Bild (#63)
                Assert.Null(p);
                continue;
            }
            Assert.NotNull(p);
            Assert.StartsWith(Full(covers) + Path.DirectorySeparatorChar, Full(p!));
        }
        if (!PlaylistRules.IsPlainId(id)) Assert.Empty(handler.Requests);
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories),
            f => !Full(f).StartsWith(Full(covers) + Path.DirectorySeparatorChar));
    }
}
