using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Titelwechsel ohne überschriebenen Fortschritt (#36, vgl. audiobookshelf-app#1491): Eine verspätete Meldung
/// von Titel A darf weder Titel B noch den beim Schließen gemeldeten Endstand von A überschreiben.
/// </summary>
public class SessionSyncGuardTests
{
    private sealed class Server
    {
        public List<string> Log { get; } = [];
        public Task Send(string what) { lock (Log) Log.Add(what); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Wechsel_WartetAufLaufendenAbgleich_UndSchliesstAVorB()
    {
        var guard = new SessionSyncGuard();
        var server = new Server();
        var a = guard.Begin("sA", "buchA", null, 100);

        // Ein periodischer Abgleich von A hängt im Netz.
        var hang = new TaskCompletionSource();
        var slowSync = guard.SyncAsync(a, async t => { await hang.Task; await server.Send($"sync {t.SessionId} {t.ItemId} 40"); });

        // Wechsel auf B: A schließen (mit Endstand 100), dann B beginnen.
        var close = guard.EndAsync(a, t => server.Send($"close {t.SessionId} {t.ItemId} 100"));
        Assert.False(close.IsCompleted); // wartet auf den hängenden Abgleich
        hang.SetResult();
        await slowSync;
        await close;
        var b = guard.Begin("sB", "buchB", null, 50);
        await guard.SyncAsync(b, t => server.Send($"sync {t.SessionId} {t.ItemId} 5"));

        Assert.Equal(["sync sA buchA 40", "close sA buchA 100", "sync sB buchB 5"], server.Log);
    }

    [Fact]
    public async Task VerspaeteterAbgleich_DerAltenSession_GehtNichtMehrRaus()
    {
        var guard = new SessionSyncGuard();
        var server = new Server();
        var a = guard.Begin("sA", "buchA", null, 100);
        await guard.EndAsync(a, t => server.Send("close sA"));
        var b = guard.Begin("sB", "buchB", null, 50);

        // Ein Tick, der A noch kannte, will jetzt abgleichen.
        Assert.False(await guard.SyncAsync(a, t => server.Send($"sync {t.SessionId} {t.ItemId}")));
        Assert.False(guard.IsCurrent(a));
        Assert.True(guard.IsCurrent(b));
        Assert.Equal(["close sA"], server.Log);
    }

    [Fact]
    public async Task Abgleich_BleibtBeiSeinerSessionUndSeinemTitel()
    {
        var guard = new SessionSyncGuard();
        var seen = new List<(string, string, string?, double)>();
        var a = guard.Begin("sA", "buchA", null, 100);
        await guard.EndAsync(a, _ => Task.CompletedTask);
        var b = guard.Begin("sB", "podB", "folge1", 50);
        await guard.SyncAsync(b, t => { seen.Add((t.SessionId, t.ItemId, t.EpisodeId, t.Duration)); return Task.CompletedTask; });
        Assert.Equal([("sB", "podB", "folge1", 50d)], seen);
    }

    [Fact]
    public async Task GehoerteZeit_WandertNichtInDieNaechsteSession()
    {
        var guard = new SessionSyncGuard();
        var a = guard.Begin("sA", "buchA", null, 100);
        a.CarryOverListened = 30; // ein Abgleich von A ist gescheitert
        await guard.EndAsync(a, _ => Task.CompletedTask);
        var b = guard.Begin("sB", "buchB", null, 50);
        Assert.Equal(0, b.CarryOverListened);
    }

    [Fact]
    public async Task NeueSession_ErstNachDemSchliessen()
    {
        var guard = new SessionSyncGuard();
        var a = guard.Begin("sA", "buchA", null, 100);
        Assert.Throws<InvalidOperationException>(() => guard.Begin("sB", "buchB", null, 50));
        await guard.EndAsync(a, _ => Task.CompletedTask);
        guard.Begin("sB", "buchB", null, 50);
    }

    [Fact]
    public async Task SchliessenScheitert_SessionIstTrotzdemBeendet_UndZweitesSchliessenTutNichts()
    {
        var guard = new SessionSyncGuard();
        var a = guard.Begin("sA", "buchA", null, 100);
        await Assert.ThrowsAsync<HttpRequestException>(() => guard.EndAsync(a, _ => throw new HttpRequestException()));
        Assert.True(a.Ended);
        var calls = 0;
        Assert.False(await guard.EndAsync(a, _ => { calls++; return Task.CompletedTask; }));
        Assert.Equal(0, calls);
        guard.Begin("sB", "buchB", null, 50);
    }

    [Fact]
    public async Task EigeneSessions_AuchGeschlossene_WerdenErkannt()
    {
        var guard = new SessionSyncGuard();
        var a = guard.Begin("sA", "buchA", null, 100);
        await guard.EndAsync(a, _ => Task.CompletedTask);
        guard.Begin("sB", "buchB", null, 50);
        guard.RememberOwn("sB2"); // vom Server neu vergeben
        Assert.True(guard.IsOwnSession("sA"));
        Assert.True(guard.IsOwnSession("sB"));
        Assert.True(guard.IsOwnSession("sB2"));
        Assert.False(guard.IsOwnSession("fremd"));
        Assert.False(guard.IsOwnSession(null));
    }

    [Fact]
    public async Task ErneuerteSessionId_WirdBeimSchliessenVerwendet()
    {
        var guard = new SessionSyncGuard();
        var closed = "";
        var a = guard.Begin("sA", "buchA", null, 100);
        await guard.SyncAsync(a, t => { t.SessionId = "sA2"; return Task.CompletedTask; }); // Server hatte sA verloren
        await guard.EndAsync(a, t => { closed = t.SessionId; return Task.CompletedTask; });
        Assert.Equal("sA2", closed);
    }
}
