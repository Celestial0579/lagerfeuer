using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Erst das Update starten, dann herunterfahren. Scheitert der Start, bleibt die App unangetastet –
/// samt Player, und ein zweiter Versuch ist möglich.
/// </summary>
public class UpdateInstallationTests
{
    private readonly List<string> _schritte = [];

    private Task Herunterfahren()
    {
        _schritte.Add("herunterfahren");
        return Task.CompletedTask;
    }

    private void Beenden() => _schritte.Add("beenden");

    [Fact]
    public async Task StartGelingt_ErstHerunterfahrenDannBeenden()
    {
        await UpdateInstallation.RunAsync(() => _schritte.Add("start"), Herunterfahren, Beenden);

        Assert.Equal(["start", "herunterfahren", "beenden"], _schritte);
    }

    [Fact]
    public async Task StartScheitert_NichtsWirdHeruntergefahrenUndNichtsBeendet()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstallation.RunAsync(
            () => throw new InvalidOperationException("Setup nicht ausführbar"), Herunterfahren, Beenden));

        Assert.Equal("Setup nicht ausführbar", ex.Message);
        Assert.Empty(_schritte);
    }

    [Fact]
    public async Task NachGescheitertemStart_GelingtEinZweiterVersuch()
    {
        var versuch = 0;
        void Start()
        {
            if (++versuch == 1) throw new InvalidOperationException("beim ersten Mal scheitert der Start");
            _schritte.Add("start");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstallation.RunAsync(Start, Herunterfahren, Beenden));
        Assert.Empty(_schritte); // nach dem Fehlschlag ist nichts heruntergefahren

        await UpdateInstallation.RunAsync(Start, Herunterfahren, Beenden);
        Assert.Equal(["start", "herunterfahren", "beenden"], _schritte);
    }

    [Fact]
    public async Task BeendetWirdErstWennDasHerunterfahrenFertigIst()
    {
        // Solange der Fortschritt noch an den Server gemeldet wird, darf die App nicht enden.
        var gemeldet = new TaskCompletionSource();

        var lauf = UpdateInstallation.RunAsync(() => { }, () => gemeldet.Task, Beenden);
        Assert.Empty(_schritte);

        gemeldet.SetResult();
        await lauf;
        Assert.Equal(["beenden"], _schritte);
    }

    [Fact]
    public async Task HerunterfahrenScheitert_TrotzdemWirdBeendet()
    {
        // Das Update wartet schon auf das Ende dieser Instanz.
        await UpdateInstallation.RunAsync(() => { }, () => throw new IOException("Server nicht erreichbar"), Beenden);

        Assert.Equal(["beenden"], _schritte);
    }

    [Fact]
    public async Task HerunterfahrenScheitertVerzoegert_TrotzdemWirdBeendet()
    {
        await UpdateInstallation.RunAsync(() => { }, () => Task.FromException(new IOException("Server nicht erreichbar")), Beenden);

        Assert.Equal(["beenden"], _schritte);
    }

    // ---------- Der Start hält den Aufrufer nicht an ----------

    [Fact]
    public async Task StartDauert_AufruferBekommtSofortDieKontrolleZurueck()
    {
        // Windows prüft eine frisch geladene EXE, bevor es sie startet; in der Test-VM dauerte das 17 Sekunden.
        // So lange darf das Fenster nicht stehen.
        using var windowsGibtFrei = new ManualResetEventSlim();
        var uhr = System.Diagnostics.Stopwatch.StartNew();

        var lauf = UpdateInstallation.RunAsync(
            () => { windowsGibtFrei.Wait(TimeSpan.FromSeconds(30)); _schritte.Add("start"); }, Herunterfahren, Beenden);

        Assert.True(uhr.Elapsed < TimeSpan.FromSeconds(5), "RunAsync hat auf den Start gewartet");
        Assert.False(lauf.IsCompleted);
        Assert.Empty(_schritte); // heruntergefahren wird erst nach dem Start

        windowsGibtFrei.Set();
        await lauf;
        Assert.Equal(["start", "herunterfahren", "beenden"], _schritte);
    }

    [Fact]
    public void NachDemStart_GehtEsImKontextDesAufrufersWeiter()
    {
        // In der App ist der Aufrufer der Thread der Oberfläche: Der Start läuft woanders, Herunterfahren und
        // Beenden müssen dorthin zurück.
        var vorher = SynchronizationContext.Current;
        var oberflaeche = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(oberflaeche);
        try
        {
            int start = 0, herunterfahren = 0, beenden = 0;
            var lauf = UpdateInstallation.RunAsync(
                () => start = Environment.CurrentManagedThreadId,
                () => { herunterfahren = Environment.CurrentManagedThreadId; return Task.CompletedTask; },
                () => beenden = Environment.CurrentManagedThreadId);
            oberflaeche.RunUntil(lauf);
            Assert.True(lauf.IsCompletedSuccessfully, lauf.Exception?.ToString());

            var hier = Environment.CurrentManagedThreadId;
            Assert.NotEqual(hier, start);
            Assert.Equal(hier, herunterfahren);
            Assert.Equal(hier, beenden);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(vorher);
        }
    }

    [Fact]
    public void StartScheitertImHintergrund_FehlerKommtImKontextDesAufrufersAn()
    {
        var vorher = SynchronizationContext.Current;
        var oberflaeche = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(oberflaeche);
        try
        {
            var gefangenIn = 0;
            async Task Aufrufer()
            {
                try
                {
                    await UpdateInstallation.RunAsync(() => throw new InvalidOperationException("Setup nicht ausführbar"), Herunterfahren, Beenden);
                }
                catch (InvalidOperationException)
                {
                    gefangenIn = Environment.CurrentManagedThreadId; // hier setzt die App die Leiste auf „Fehler“
                }
            }

            oberflaeche.RunUntil(Aufrufer());

            Assert.Equal(Environment.CurrentManagedThreadId, gefangenIn);
            Assert.Empty(_schritte);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(vorher);
        }
    }

    /// <summary>Ein Thread, der seine Aufgaben selbst abarbeitet, wie der Thread der Oberfläche.</summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void RunUntil(Task task)
        {
            var ende = DateTime.UtcNow.AddSeconds(30);
            while (!task.IsCompleted && DateTime.UtcNow < ende)
                if (_queue.TryTake(out var work, millisecondsTimeout: 20)) work.Callback(work.State);
            Assert.True(task.IsCompleted, "Der Ablauf ist nach 30 Sekunden nicht fertig");
        }
    }
}
