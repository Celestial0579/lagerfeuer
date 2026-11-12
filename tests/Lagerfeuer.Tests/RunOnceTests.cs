using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Das Herunterfahren des Players läuft über RunOnce: höchstens einmal, gleich wie oft und von wo es
/// angestoßen wird (Fenster schließen, Update).
/// </summary>
public class RunOnceTests
{
    [Fact]
    public async Task ZweiAufrufeNacheinander_EinVorgang()
    {
        var laeufe = 0;
        var einmal = new RunOnce(() => { laeufe++; return Task.CompletedTask; });

        await einmal.RunAsync();
        await einmal.RunAsync();

        Assert.Equal(1, laeufe);
    }

    [Fact]
    public async Task ZweiterAufrufWaehrendDesLaufs_WartetAufDenselbenVorgang()
    {
        var laeufe = 0;
        var fertig = new TaskCompletionSource();
        var einmal = new RunOnce(() => { laeufe++; return fertig.Task; });

        var erster = einmal.RunAsync();
        var zweiter = einmal.RunAsync();

        Assert.Same(erster, zweiter);
        Assert.False(zweiter.IsCompleted); // wartet, statt gleich zurückzukehren
        fertig.SetResult();
        await zweiter;
        Assert.Equal(1, laeufe);
    }

    [Fact]
    public async Task VieleAufruferGleichzeitig_EinVorgang()
    {
        var laeufe = 0;
        var einmal = new RunOnce(async () =>
        {
            Interlocked.Increment(ref laeufe);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        });

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(einmal.RunAsync, TestContext.Current.CancellationToken)));

        Assert.Equal(1, laeufe);
    }

    [Fact]
    public async Task ScheitertDerVorgang_BekommenAlleDenFehlerUndNiemandStartetNeu()
    {
        var laeufe = 0;
        var einmal = new RunOnce(() => { laeufe++; return Task.FromException(new IOException("Server nicht erreichbar")); });

        await Assert.ThrowsAsync<IOException>(einmal.RunAsync);
        await Assert.ThrowsAsync<IOException>(einmal.RunAsync);

        Assert.Equal(1, laeufe);
    }

    [Fact]
    public async Task ScheitertSchonDerAufruf_GiltErTrotzdemAlsAusgefuehrt()
    {
        var laeufe = 0;
        var einmal = new RunOnce(() => { laeufe++; throw new InvalidOperationException("sofort gescheitert"); });

        await Assert.ThrowsAsync<InvalidOperationException>(einmal.RunAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(einmal.RunAsync);

        Assert.Equal(1, laeufe);
    }

    [Fact]
    public void Started_ZeigtObSchonAngestossen()
    {
        var einmal = new RunOnce(() => Task.CompletedTask);

        Assert.False(einmal.Started);
        _ = einmal.RunAsync();
        Assert.True(einmal.Started);
    }
}
