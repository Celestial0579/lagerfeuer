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
}
