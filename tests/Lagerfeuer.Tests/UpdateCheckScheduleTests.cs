using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Eine Update-Prüfung kurz nach dem Start, danach alle zwölf Stunden. Der frühere Zeitgeber prüfte nach
/// 8 Sekunden und nach 16 Sekunden noch einmal.
/// </summary>
public class UpdateCheckScheduleTests
{
    /// <summary>Wartezeiten, die erst ablaufen, wenn der Test es sagt.</summary>
    private sealed class Clock
    {
        private readonly List<(TimeSpan Wait, TaskCompletionSource Done)> _waits = [];
        private readonly SemaphoreSlim _started = new(0);

        public List<TimeSpan> Waits => _waits.Select(w => w.Wait).ToList();

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            _waits.Add((wait, done));
            _started.Release();
            return done.Task;
        }

        /// <summary>Wartet, bis die Schleife die nächste Wartezeit begonnen hat.</summary>
        public Task NextWaitAsync() => _started.WaitAsync(TestContext.Current.CancellationToken);

        /// <summary>Lässt die zuletzt begonnene Wartezeit ablaufen.</summary>
        public void Elapse() => _waits[^1].Done.SetResult();
    }

    private static readonly TimeSpan First = UpdateCheckSchedule.FirstDelay;
    private static readonly TimeSpan Later = UpdateCheckSchedule.Interval;

    [Fact]
    public void ZeitenWieInDerReadme()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), First);
        Assert.Equal(TimeSpan.FromHours(12), Later);
    }

    [Fact]
    public async Task EinePruefungNachDemStart_DanachImGrossenAbstand()
    {
        var clock = new Clock();
        var checks = 0;
        using var cts = new CancellationTokenSource();
        var run = UpdateCheckSchedule.RunAsync(() => { checks++; return Task.CompletedTask; }, clock.Delay, cts.Token);

        await clock.NextWaitAsync();
        Assert.Equal(0, checks); // vor Ablauf der ersten Wartezeit wird nicht geprüft
        Assert.Equal([First], clock.Waits);

        clock.Elapse();
        await clock.NextWaitAsync();
        Assert.Equal(1, checks);
        // Der Fehler von früher: Hier stand noch einmal die kurze Wartezeit, also eine zweite Prüfung nach 16 Sekunden.
        Assert.Equal([First, Later], clock.Waits);

        clock.Elapse();
        await clock.NextWaitAsync();
        Assert.Equal(2, checks);
        Assert.Equal([First, Later, Later], clock.Waits);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task GewartetWirdErstWennDiePruefungFertigIst()
    {
        var clock = new Clock();
        var check = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var run = UpdateCheckSchedule.RunAsync(() => { started.SetResult(); return check.Task; }, clock.Delay, cts.Token);

        await clock.NextWaitAsync();
        clock.Elapse();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal([First], clock.Waits); // die Prüfung läuft noch: keine neue Wartezeit

        check.SetResult();
        await clock.NextWaitAsync();
        Assert.Equal([First, Later], clock.Waits);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task PruefungScheitert_NaechstePruefungKommtTrotzdem()
    {
        var clock = new Clock();
        var checks = 0;
        using var cts = new CancellationTokenSource();
        var run = UpdateCheckSchedule.RunAsync(
            () => ++checks == 1 ? throw new HttpRequestException("keine Verbindung") : Task.CompletedTask, clock.Delay, cts.Token);

        await clock.NextWaitAsync();
        clock.Elapse();
        await clock.NextWaitAsync();
        Assert.Equal([First, Later], clock.Waits);

        clock.Elapse();
        await clock.NextWaitAsync();
        Assert.Equal(2, checks);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task AbbruchVorDerErstenPruefung_EsWirdNichtGeprueft()
    {
        var clock = new Clock();
        var checks = 0;
        using var cts = new CancellationTokenSource();
        var run = UpdateCheckSchedule.RunAsync(() => { checks++; return Task.CompletedTask; }, clock.Delay, cts.Token);

        await clock.NextWaitAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, checks);
    }
}
