using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class SocketIoProtocolTests
{
    [Theory]
    [InlineData("https://abs.test/", "wss://abs.test/socket.io/?EIO=4&transport=websocket")]
    [InlineData("http://nas:13378/", "ws://nas:13378/socket.io/?EIO=4&transport=websocket")]
    [InlineData("https://host/audiobookshelf/", "wss://host/audiobookshelf/socket.io/?EIO=4&transport=websocket")]
    public void Uri_MitUnterpfad(string server, string expected)
        => Assert.Equal(expected, SocketIoProtocol.BuildUri(new Uri(server)).ToString());

    [Fact]
    public void Handshake_OpenConnectAuth()
    {
        var protocol = new SocketIoProtocol(() => "jwt-token");

        Assert.Equal(["40"], protocol.Handle("""0{"sid":"abc","upgrades":[],"pingInterval":25000,"pingTimeout":20000}""").Replies);
        Assert.Equal(["""42["auth","jwt-token"]"""], protocol.Handle("""40{"sid":"xyz"}""").Replies);
    }

    [Fact]
    public void Ping_WirdMitPongBeantwortet()
        => Assert.Equal(["3"], new SocketIoProtocol(() => null).Handle("2").Replies);

    [Fact]
    public void Event_WirdGelesen()
    {
        var outcome = new SocketIoProtocol(() => null).Handle("""42["init",{"userId":"u1","username":"anna"}]""");

        Assert.Empty(outcome.Replies);
        Assert.Equal("init", outcome.Event!.Name);
        Assert.Equal("anna", outcome.Event.Payload.GetProperty("username").GetString());
    }

    [Fact]
    public void Fortschritt_WirdAusgewertet()
    {
        var outcome = new SocketIoProtocol(() => null).Handle("""
            42["user_item_progress_updated",{"id":"p1","sessionId":"sess-9","deviceDescription":"Pixel 8",
              "data":{"id":"p1","libraryItemId":"buch","episodeId":null,"currentTime":1234.5,"duration":3600,"progress":0.34,"isFinished":false,"lastUpdate":1760000000000}}]
            """);

        var progress = SocketIoProtocol.TryParseProgress(outcome.Event!.Payload);

        Assert.Equal("sess-9", progress!.SessionId);
        Assert.Equal("Pixel 8", progress.DeviceDescription);
        Assert.Equal("buch", progress.Progress.LibraryItemId);
        Assert.Equal(1234.5, progress.Progress.CurrentTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("6")]
    [InlineData("42kein json")]
    [InlineData("42[]")]
    [InlineData("""44{"message":"nope"}""")]
    public void Unbekanntes_WirdIgnoriert(string frame)
    {
        var outcome = new SocketIoProtocol(() => "t").Handle(frame);
        Assert.Empty(outcome.Replies);
        Assert.Null(outcome.Event);
    }
}

/// <summary>Kompletter Ablauf gegen einen minimalen Socket.IO-Server (HttpListener + WebSocket).</summary>
public class LiveSyncClientTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Verbindet_MeldetSichAn_UndEmpfaengtFortschritt()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        using var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{port}/socket.io/");
        http.Start();

        var received = new List<string>();
        var serverTask = Task.Run(async () =>
        {
            var context = await http.GetContextAsync();
            Assert.Equal("4", context.Request.QueryString["EIO"]);
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;

            async Task Send(string s) => await ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, ct);
            async Task<string> Receive()
            {
                var buffer = new byte[4096];
                var r = await ws.ReceiveAsync(buffer, ct);
                var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                lock (received) received.Add(text);
                return text;
            }

            await Send("""0{"sid":"e1","upgrades":[],"pingInterval":25000,"pingTimeout":20000,"maxPayload":1000000}""");
            Assert.Equal("40", await Receive());
            await Send("""40{"sid":"s1"}""");
            Assert.Equal("""42["auth","access-token"]""", await Receive());
            await Send("""42["init",{"userId":"u1","username":"anna"}]""");
            await Send("2");
            Assert.Equal("3", await Receive());
            await Send("""42["user_item_progress_updated",{"id":"p","sessionId":"fremd","deviceDescription":"Handy","data":{"libraryItemId":"buch","currentTime":42,"duration":100,"progress":0.42}}]""");
            await Task.Delay(500, ct);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        }, ct);

        using var api = new AbsApiClient(new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK)));
        api.Configure(new Uri($"http://localhost:{port}/"), "access-token", null);
        await using var client = new LiveSyncClient(api);
        var progress = new TaskCompletionSource<RemoteProgress>();
        var connected = new TaskCompletionSource();
        client.ProgressUpdated += (_, p) => progress.TrySetResult(p);
        client.ConnectionChanged += (_, c) => { if (c) connected.TrySetResult(); };

        client.Start();

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var update = await progress.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("buch", update.Progress.LibraryItemId);
        Assert.Equal(42, update.Progress.CurrentTime);
        Assert.Equal("Handy", update.DeviceDescription);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
}
