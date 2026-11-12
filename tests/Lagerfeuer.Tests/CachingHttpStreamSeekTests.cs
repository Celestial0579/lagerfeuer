using System.Net;
using System.Net.Http.Headers;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Ein Sprung an eine fehlende Stelle setzt den Download dort sofort neu an. Die laufende Anfrage wird dafür
/// abgebrochen; der Sprung wartet nicht, bis ihr Block fertig ist oder der Server überhaupt geantwortet hat.
/// </summary>
public class CachingHttpStreamSeekTests : IDisposable
{
    private const int Chunk = CachingHttpStream.ChunkSize;

    // 12 Blöcke: genug, damit eine Stelle außerhalb des Fensters liegt, in dem der Download ohnehin gleich
    // vorbeikommt (acht Blöcke voraus).
    private static readonly byte[] Audio = CreateData(Chunk * 11 + 4321);

    private readonly TempDir _dir = new();
    private string DataPath => Path.Combine(_dir.Path, "item", "001_track.mp3");

    public void Dispose() => _dir.Dispose();

    private static byte[] CreateData(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    /// <summary>
    /// Server wie Audiobookshelf (Range-Anfragen, 206). Anfragen, die in einem gesperrten Bereich der Datei
    /// beginnen, hält er zurück, bis der Test die Sperre freigibt. Anders als <see cref="FakeHandler"/> beachtet
    /// er den Abbruch einer Anfrage, so wie eine echte Verbindung es tut, und schreibt mit.
    /// </summary>
    private sealed class HoldingServer(byte[] data, bool rangeSupported = true) : HttpMessageHandler
    {
        public sealed class Gate(long from, long below)
        {
            private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Covers(long start) => start >= from && start < below;
            public Task Opened => _open.Task;
            public void Release() => _open.TrySetResult();
        }

        private readonly List<Gate> _gates = [];
        private readonly List<long> _starts = [];
        private readonly List<long> _canceled = [];

        /// <summary>Sperrt Anfragen, die in diesem Bereich beginnen.</summary>
        public Gate Hold(long from, long below)
        {
            var gate = new Gate(from, below);
            lock (_gates) _gates.Add(gate);
            return gate;
        }

        public void ReleaseAll()
        {
            lock (_gates) _gates.ForEach(g => g.Release());
        }

        /// <summary>Byte-Stellen, an denen Anfragen an die Datei begannen, in ihrer Reihenfolge (ohne die Längenabfrage 0-0).</summary>
        public IReadOnlyList<long> Starts
        {
            get { lock (_starts) return _starts.ToList(); }
        }

        /// <summary>Byte-Stellen der Anfragen, die der Client aufgegeben hat, während sie gesperrt waren.</summary>
        public IReadOnlyList<long> Canceled
        {
            get { lock (_canceled) return _canceled.ToList(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = range?.From ?? 0;
            var probe = range is { From: 0, To: 0 };
            if (!probe)
            {
                lock (_starts) _starts.Add(from);
                Gate? gate;
                lock (_gates) gate = _gates.FirstOrDefault(g => g.Covers(from));
                if (gate is not null)
                {
                    try
                    {
                        await gate.Opened.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_canceled) _canceled.Add(from);
                        throw;
                    }
                }
            }

            if (!rangeSupported)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

            var to = Math.Min(range?.To ?? data.Length - 1, data.Length - 1);
            var content = new ByteArrayContent(data, (int)from, (int)(to - from + 1));
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
    }

    private async Task<(CachingHttpStream Stream, AbsApiClient Api)> OpenAsync(HoldingServer server)
    {
        var api = new AbsApiClient(server);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        return (await CachingHttpStream.OpenAsync(api, "/api/items/item/file/1", DataPath, TestContext.Current.CancellationToken), api);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition(), what);
    }

    private async Task<byte[]> ReadAtAsync(CachingHttpStream stream, long position, int count = 40)
    {
        stream.Position = position;
        var buffer = new byte[count];
        // Scheitert der Abbruch, hinge das Lesen bis zur Zeitüberschreitung des Streams (45 s). So lange wartet kein Test.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        Assert.Equal(count, await stream.ReadAsync(buffer, limit.Token));
        return buffer;
    }

    [Fact]
    public async Task SprungWeitVoraus_WartetNichtAufDieLaufendeAnfrage()
    {
        // Der Server antwortet auf die erste Anfrage (ab Byte 0) nicht. Früher hing damit auch der Sprung.
        var server = new HoldingServer(Audio);
        server.Hold(0, 10L * Chunk);
        var (stream, api) = await OpenAsync(server);
        try
        {
            await WaitUntilAsync(() => server.Starts.Contains(0), "Der Download hat nicht am Anfang begonnen.");

            var position = 10L * Chunk + 100;
            var bytes = await ReadAtAsync(stream, position);

            Assert.Equal(Audio.AsSpan((int)position, 40).ToArray(), bytes);
            Assert.Equal([0L], server.Canceled);                       // die hängende Anfrage wurde aufgegeben
            Assert.Equal([0L, 10L * Chunk], server.Starts.Take(2));    // und am Block der Sprungstelle neu angesetzt
            Assert.False(stream.IsComplete);                           // der Anfang fehlt weiter, er kommt erst danach
        }
        finally
        {
            server.ReleaseAll();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    // ---------- Der Ablauf beim Fortsetzen (#9) ----------

    // 24 Blöcke: Die Zielstelle (Block 14) liegt weiter als acht Blöcke hinter der Stelle, an der der Download
    // nach dem Öffnen weitermacht (Block 2).
    private static readonly byte[] Buch = CreateData(Chunk * 23 + 999);

    /// <summary>Legt die Datei so in den Cache, als wären nur diese Blöcke schon geladen.</summary>
    private void PrepareCached(byte[] data, params int[] loadedChunks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllBytes(DataPath, data);
        var count = (data.Length + Chunk - 1) / Chunk;
        var map = new byte[sizeof(long) + sizeof(int) + count];
        BitConverter.TryWriteBytes(map.AsSpan(0), (long)data.Length);
        BitConverter.TryWriteBytes(map.AsSpan(sizeof(long)), Chunk);
        foreach (var chunk in loadedChunks) map[sizeof(long) + sizeof(int) + chunk] = 1;
        File.WriteAllBytes(CachingHttpStream.MapPathFor(DataPath), map);
    }

    [Fact]
    public async Task WieBeimFortsetzen_SprungTrifftEinWaehrendDieAntwortAussteht()
    {
        // So stand es im Mitschnitt zu #9: Beim Öffnen hat der Player den Anfang und das Dateiende gelesen
        // (Blöcke 0, 1 und der letzte sind da), der Download setzt danach bei Block 2 neu an. Noch bevor der
        // Server darauf antwortet, springt der Player zur gespeicherten Stelle, hier Block 14.
        //
        // Früher ging dieser Sprung verloren. Die Schleife merkte sich den Stand der Wünsche erst, als die
        // Antwort da war, hielt den Sprung damit für berücksichtigt und lud von Block 2 an der Reihe nach.
        // Über eine langsame Verbindung dauerte es Minuten, bis die Zielstelle kam.
        PrepareCached(Buch, 0, 1, 23);
        var server = new HoldingServer(Buch);
        server.Hold(2L * Chunk, 3L * Chunk); // die Antwort auf „ab Block 2“ steht aus
        var (stream, api) = await OpenAsync(server);
        try
        {
            await WaitUntilAsync(() => server.Starts.Contains(2L * Chunk), "Der Download hat nicht bei Block 2 angesetzt.");

            var position = 14L * Chunk + 100;
            var bytes = await ReadAtAsync(stream, position);

            Assert.Equal(Buch.AsSpan((int)position, 40).ToArray(), bytes);
            Assert.Equal([2L * Chunk, 14L * Chunk], server.Starts.Take(2)); // sofort an der Zielstelle angesetzt
            Assert.Contains(2L * Chunk, server.Canceled);
        }
        finally
        {
            server.ReleaseAll();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task WieBeimFortsetzen_AntwortKommtErstNachDemSprung()
    {
        // Derselbe Ablauf, nur antwortet der Server kurz nach dem Sprung doch noch auf „ab Block 2“.
        // Früher lief der Download dann dort weiter, und an der Zielstelle wurde nie angefragt.
        PrepareCached(Buch, 0, 1, 23);
        var server = new HoldingServer(Buch);
        var abBlock2 = server.Hold(2L * Chunk, 3L * Chunk);
        var (stream, api) = await OpenAsync(server);
        try
        {
            await WaitUntilAsync(() => server.Starts.Contains(2L * Chunk), "Der Download hat nicht bei Block 2 angesetzt.");

            var position = 14L * Chunk + 100;
            stream.Position = position;
            var lesen = stream.ReadAsync(new byte[40], TestContext.Current.CancellationToken).AsTask();
            // Dem Sprung einen Augenblick lassen, dann antwortet der Server auf die alte Anfrage.
            for (var i = 0; i < 12 && server.Starts.Count < 2; i++) await Task.Delay(25, TestContext.Current.CancellationToken);
            abBlock2.Release();

            Assert.Equal(40, await lesen);
            // Früher stand hier nur die Anfrage ab Block 2: Die Daten der Zielstelle kamen erst, als der
            // Download der Reihe nach dort ankam.
            Assert.Equal([2L * Chunk, 14L * Chunk], server.Starts.Take(2));
        }
        finally
        {
            server.ReleaseAll();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task SprungZurueck_WartetNichtAufDieLaufendeAnfrage()
    {
        // Der Player springt weit nach hinten, der Server hängt dort. Dann springt er zurück an den Anfang.
        var server = new HoldingServer(Audio);
        var anfang = server.Hold(0, Chunk);
        server.Hold(9L * Chunk, long.MaxValue);
        var (stream, api) = await OpenAsync(server);
        try
        {
            await WaitUntilAsync(() => server.Starts.Contains(0), "Der Download hat nicht am Anfang begonnen.");

            // Lesen in Block 9: Der Download wechselt dorthin und bekommt keine Antwort. Dieses Lesen gibt
            // der Player auf, wenn er weiterspringt; hier steht dafür der Abbruch des Lesens.
            using var aufgegeben = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            stream.Position = 9L * Chunk;
            var hinten = stream.ReadAsync(new byte[40], aufgegeben.Token).AsTask();
            await WaitUntilAsync(() => server.Starts.Contains(9L * Chunk), "Der Download hat nicht in Block 9 angesetzt.");
            aufgegeben.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hinten);
            Assert.Equal([0L], server.Canceled); // das aufgegebene Lesen allein bricht die Anfrage in Block 9 nicht ab

            anfang.Release(); // am Anfang antwortet der Server jetzt
            var bytes = await ReadAtAsync(stream, 17);

            Assert.Equal(Audio.AsSpan(17, 40).ToArray(), bytes);
            Assert.Equal([0L, 9L * Chunk], server.Canceled);              // die hängende Anfrage in Block 9
            Assert.Equal([0L, 9L * Chunk, 0L], server.Starts.Take(3));    // danach neu angesetzt am Anfang
        }
        finally
        {
            server.ReleaseAll();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task StelleKurzVoraus_BrichtNichtsAb()
    {
        // Block 3 liegt im Fenster, in dem der Download ohnehin gleich vorbeikommt: Die Anfrage bleibt.
        var server = new HoldingServer(Audio);
        var alles = server.Hold(0, long.MaxValue);
        var (stream, api) = await OpenAsync(server);
        try
        {
            await WaitUntilAsync(() => server.Starts.Contains(0), "Der Download hat nicht am Anfang begonnen.");

            stream.Position = 3L * Chunk + 5;
            var lesen = stream.ReadAsync(new byte[40], TestContext.Current.CancellationToken).AsTask();
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.False(lesen.IsCompleted);          // wartet auf die laufende Anfrage
            Assert.Empty(server.Canceled);
            Assert.Equal([0L], server.Starts);        // keine zweite Anfrage

            alles.Release();
            Assert.Equal(40, await lesen);
        }
        finally
        {
            server.ReleaseAll();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task FortlaufendesLesen_BrichtNichtsAb()
    {
        // Der Player liest Block für Block: eine Anfrage für die ganze Datei, kein Abbruch.
        var server = new HoldingServer(Audio);
        var (stream, api) = await OpenAsync(server);
        try
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);

            Assert.Equal(Audio, copy.ToArray());
            Assert.Empty(server.Canceled);
            Assert.Equal([0L], server.Starts);
        }
        finally
        {
            await stream.DisposeAsync();
            api.Dispose();
        }
    }

    [Fact]
    public async Task ServerOhneRange_SprungBrichtNichtsAb()
    {
        // Ohne Range kommt die Datei immer von vorn. Ein Abbruch brächte nichts außer einem Neubeginn.
        var server = new HoldingServer(Audio, rangeSupported: false);
        var start = server.Hold(0, long.MaxValue);
        var (stream, api) = await OpenAsync(server);
        try
        {
            // Die erste Antwort (200, ganze Datei) lässt den Stream wissen, dass der Server kein Range kann.
            // Solange sie aussteht, weiß er es nicht; deshalb erst freigeben, dann springen.
            await WaitUntilAsync(() => server.Starts.Contains(0), "Der Download hat nicht am Anfang begonnen.");
            start.Release();
            await WaitUntilAsync(() => stream.IsComplete, "Die Datei kam nicht an.");

            var position = 10L * Chunk + 100;
            var bytes = await ReadAtAsync(stream, position);

            Assert.Equal(Audio.AsSpan((int)position, 40).ToArray(), bytes);
            Assert.Empty(server.Canceled);
            Assert.Equal([0L], server.Starts);
        }
        finally
        {
            server.ReleaseAll();
            await stream.DisposeAsync();
            api.Dispose();
        }
    }
}
