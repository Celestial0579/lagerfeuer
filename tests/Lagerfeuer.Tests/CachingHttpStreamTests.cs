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

    // ---------- Ab einer Stelle vollständig? (Vorladen der nächsten Datei) ----------

    /// <summary>
    /// Legt eine teilweise geladene Datei an, wie der Cache sie nach einem abgebrochenen Download hinterlässt:
    /// die Daten in voller Länge und daneben die Karte der geladenen Blöcke (Länge, Blockgröße, je Block ein Byte).
    /// </summary>
    private void PreparePartialFile(params int[] loadedChunks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllBytes(DataPath, Audio);
        var count = (Audio.Length + CachingHttpStream.ChunkSize - 1) / CachingHttpStream.ChunkSize;
        var map = new byte[sizeof(long) + sizeof(int) + count];
        BitConverter.TryWriteBytes(map.AsSpan(0), (long)Audio.Length);
        BitConverter.TryWriteBytes(map.AsSpan(sizeof(long)), CachingHttpStream.ChunkSize);
        foreach (var chunk in loadedChunks) map[sizeof(long) + sizeof(int) + chunk] = 1;
        File.WriteAllBytes(CachingHttpStream.MapPathFor(DataPath), map);
    }

    /// <summary>Server, der keine Anfrage beantwortet, bis der Test ihn freigibt: Am Stand des Caches ändert sich nichts.</summary>
    private sealed class SilentServer
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SilentServer() => Handler = new FakeHandler(_ => _answer.Task);

        public FakeHandler Handler { get; }

        public void Release() => _answer.TrySetResult(FakeHandler.Status(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    private async Task<(CachingHttpStream Stream, AbsApiClient Api, SilentServer Server)> OpenPartialAsync(params int[] loadedChunks)
    {
        PreparePartialFile(loadedChunks);
        var server = new SilentServer();
        var api = new AbsApiClient(server.Handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        var stream = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);
        return (stream, api, server);
    }

    [Fact]
    public async Task NachEinemSprung_IstDerRestDaAuchWennDavorNochEtwasFehlt()
    {
        // Die Lage nach einem Sprung in den vierten Block: Geladen ist der Anfang (Block 0) und alles ab der
        // Sprungstelle (Blöcke 3 bis 5). Die Blöcke 1 und 2 fehlen, sie kämen erst danach.
        const int chunk = CachingHttpStream.ChunkSize;
        var (stream, api, server) = await OpenPartialAsync(0, 3, 4, 5);
        try
        {
            Assert.True(stream.IsCompleteFrom(3L * chunk + 100));    // die Sprungstelle
            Assert.True(stream.IsCompleteFrom(3L * chunk));          // Anfang ihres Blocks
            Assert.True(stream.IsCompleteFrom(Audio.Length - 1));    // letzter Block
            Assert.False(stream.IsCompleteFrom(3L * chunk - 1));     // der Block davor fehlt
            Assert.False(stream.IsCompleteFrom(1L * chunk));
            Assert.False(stream.IsCompleteFrom(0));                  // Block 0 ist da, aber dahinter klafft die Lücke
            Assert.False(stream.IsComplete);                         // die Datei als Ganzes ist es nicht
        }
        finally
        {
            server.Release();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task FehltDasEnde_IstAbKeinerStelleVollstaendig()
    {
        // Alles da bis auf den letzten Block: Für die Wiedergabe bis zum Dateiende fehlt noch etwas.
        const int chunk = CachingHttpStream.ChunkSize;
        var (stream, api, server) = await OpenPartialAsync(0, 1, 2, 3, 4);
        try
        {
            Assert.False(stream.IsCompleteFrom(0));
            Assert.False(stream.IsCompleteFrom(4L * chunk));
            Assert.False(stream.IsCompleteFrom(Audio.Length - 1));
            Assert.False(stream.IsCompleteFrom(long.MaxValue)); // Positionen dahinter zählen als Ende
        }
        finally
        {
            server.Release();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task NichtsGeladen_IstAbKeinerStelleVollstaendig()
    {
        var (stream, api, server) = await OpenPartialAsync();
        try
        {
            Assert.False(stream.IsCompleteFrom(0));
            Assert.False(stream.IsCompleteFrom(Audio.Length - 1));
        }
        finally
        {
            server.Release();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task VollstaendigeDatei_IstAbJederStelleVollstaendig()
    {
        var server = new RangeFileServer(Audio);
        using var api = CreateApi(server);
        await using var stream = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);
        await WaitUntilCompleteAsync(stream);

        Assert.True(stream.IsCompleteFrom(0));
        Assert.True(stream.IsCompleteFrom(Audio.Length / 2));
        // Positionen außerhalb zählen als Anfang bzw. Ende.
        Assert.True(stream.IsCompleteFrom(-1));
        Assert.True(stream.IsCompleteFrom(Audio.Length));
        Assert.True(stream.IsCompleteFrom(long.MaxValue));
    }

    [Fact]
    public async Task SprungBeimLaden_DerRestKommtVorDerLuecke()
    {
        // Der ganze Ablauf an einem Stück, mit einem Server, der prompt antwortet: Nach dem Sprung ist der Rest
        // ab der Sprungstelle da, und am Ende die ganze Datei.
        const int chunk = CachingHttpStream.ChunkSize;
        var server = new RangeFileServer(Audio);
        using var api = CreateApi(server);
        await using var stream = await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken);

        stream.Position = 3L * chunk + 100;
        var buffer = new byte[40];
        Assert.Equal(40, await stream.ReadAsync(buffer, TestContext.Current.CancellationToken));
        for (var i = 0; i < 200 && !stream.IsCompleteFrom(stream.Position); i++) await Task.Delay(25, TestContext.Current.CancellationToken);

        Assert.True(stream.IsCompleteFrom(stream.Position), "Der Rest ab der Sprungstelle wurde nicht geladen.");
        await WaitUntilCompleteAsync(stream);
        Assert.True(stream.IsCompleteFrom(0));
    }

    public void Dispose() => _dir.Dispose();
}
