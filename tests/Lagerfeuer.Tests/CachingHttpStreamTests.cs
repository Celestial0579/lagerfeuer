using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class CachingHttpStreamTests : IDisposable
{
    // 5 volle Blöcke + ein angebrochener
    private static readonly byte[] Audio = CreateData(CachingHttpStream.ChunkSize * 5 + 12345);

    private readonly TempDir _dir = new();
    private string DataPath => Path.Combine(_dir.Path, "item", "001_track.mp3");

    private static byte[] CreateData(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private static AbsApiClient CreateApi(RangeFileServer server)
    {
        var api = new AbsApiClient(server.Handler);
        api.Configure(new Uri("https://abs.test/"), server.Token, null);
        return api;
    }

    private static async Task WaitUntilCompleteAsync(CachingHttpStream stream)
    {
        for (var i = 0; i < 200 && !stream.IsPersisted; i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(stream.IsPersisted, "Datei wurde nicht vollständig gecacht.");
    }

    [Fact]
    public async Task KomplettesLesen_LiefertDieOriginaldaten()
    {
        var server = new RangeFileServer(Audio);
        using var api = CreateApi(server);
        await using var stream = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);

        Assert.Equal(Audio.Length, stream.Length);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(Audio, copy.ToArray());
    }

    [Fact]
    public async Task Springen_LiefertDieRichtigenBytes()
    {
        var server = new RangeFileServer(Audio);
        using var api = CreateApi(server);
        await using var stream = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);

        foreach (var position in new long[] { CachingHttpStream.ChunkSize * 4 + 100, 17, Audio.Length - 50 })
        {
            stream.Position = position;
            var buffer = new byte[40];
            var read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(40, read);
            Assert.Equal(Audio.AsSpan((int)position, 40).ToArray(), buffer);
        }
    }

    [Fact]
    public async Task VollstaendigGecachteDatei_WirdOhneServerGelesen()
    {
        var server = new RangeFileServer(Audio);
        using var api = CreateApi(server);
        await using (var first = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken))
        {
            await WaitUntilCompleteAsync(first); // Hintergrund-Download lädt alles
        }

        Assert.True(CachingHttpStream.IsFileComplete(DataPath));
        var requestsBefore = server.FileRequests;

        await using var second = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await second.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(Audio, copy.ToArray());
        Assert.Equal(requestsBefore, server.FileRequests); // keine einzige neue Anfrage
    }

    [Fact]
    public async Task TeilweiseGecachteDatei_WirdBeimNaechstenOeffnenFortgesetzt()
    {
        var server = new RangeFileServer(Audio);
        using var api = CreateApi(server);
        await using (var first = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken))
        {
            var buffer = new byte[100];
            await first.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
        }

        // Der erste Stream schreibt seine Map beim Schließen im Hintergrund.
        for (var i = 0; i < 100 && CachingHttpStream.GetCachedFraction(DataPath) == 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);

        await using var second = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);
        Assert.True(second.CachedFraction > 0, "Bereits geladene Blöcke wurden nicht übernommen.");
        using var copy = new MemoryStream();
        await second.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(Audio, copy.ToArray());
    }

    [Fact]
    public async Task ServerOhneRangeSupport_FunktioniertTrotzdem()
    {
        var server = new RangeFileServer(Audio, rangeSupported: false);
        using var api = CreateApi(server);
        await using var stream = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);

        stream.Position = CachingHttpStream.ChunkSize * 3 + 7;
        var buffer = new byte[64];
        await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(Audio.AsSpan(CachingHttpStream.ChunkSize * 3 + 7, 64).ToArray(), buffer);
    }

    [Fact]
    public async Task Completed_WirdAusgeloest()
    {
        var server = new RangeFileServer(Audio);
        using var api = CreateApi(server);
        var completed = new TaskCompletionSource();
        await using var stream = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);
        stream.Completed += (_, _) => completed.TrySetResult();
        if (stream.IsComplete) completed.TrySetResult();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1.0, stream.CachedFraction);
    }

    [Fact]
    public async Task UnbekannteDatei_WirftVerstaendlicheAusnahme()
    {
        var handler = new FakeHandler(_ => FakeHandler.Status(System.Net.HttpStatusCode.NotFound));
        using var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);

        await Assert.ThrowsAsync<AbsApiException>(() =>
            CachingHttpStream.OpenAsync(api, "/api/items/x/file/9", DataPath, TestContext.Current.CancellationToken));
    }

    public void Dispose() => _dir.Dispose();
}
