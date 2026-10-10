using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>Fortschritt, den ein anderes Gerät (oder diese App) gemeldet hat.</summary>
public sealed record RemoteProgress(string? SessionId, string? DeviceDescription, MediaProgress Progress);

/// <summary>
/// Live-Verbindung zum Audiobookshelf-Server (Socket.IO 4 / Engine.IO v4 über WebSocket).
/// Empfängt Fortschrittsänderungen anderer Geräte sofort, statt erst beim nächsten Laden.
///
/// Ablauf: WebSocket auf &lt;server&gt;/socket.io/?EIO=4&amp;transport=websocket → Engine.IO-"open"
/// → Socket.IO-Connect ("40") → Event "auth" mit dem Access-Token → "init" bzw. "auth_failed".
/// Nach jedem Token-Refresh wird "auth" erneut gesendet; bei Verbindungsabbruch automatisch neu verbinden.
/// </summary>
public sealed class LiveSyncClient : IAsyncDisposable
{
    private readonly AbsApiClient _api;
    private readonly Func<ClientWebSocket> _socketFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private Task? _loop;

    public LiveSyncClient(AbsApiClient api, Func<ClientWebSocket>? socketFactory = null)
    {
        _api = api;
        _socketFactory = socketFactory ?? (() => new ClientWebSocket());
        _api.TokensChanged += OnTokensChanged;
    }

    public bool IsConnected { get; private set; }

    /// <summary>user_item_progress_updated (Hintergrund-Thread).</summary>
    public event EventHandler<RemoteProgress>? ProgressUpdated;

    /// <summary>user_updated: kompletter Benutzer inkl. Fortschritt und Lesezeichen (Hintergrund-Thread).</summary>
    public event EventHandler<AbsUser>? UserUpdated;

    public event EventHandler<bool>? ConnectionChanged;

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_api.ServerUri is not null && _api.AccessToken is not null)
                {
                    await ConnectAndReceiveAsync(_cts.Token);
                    delay = TimeSpan.FromSeconds(2); // sauber beendet → schnell neu verbinden
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"Live-Sync getrennt: {ex.Message}");
            }
            SetConnected(false);
            try { await Task.Delay(delay, _cts.Token); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        using var socket = _socketFactory();
        _socket = socket;
        await socket.ConnectAsync(SocketIoProtocol.BuildUri(_api.ServerUri!), ct);

        var protocol = new SocketIoProtocol(() => _api.AccessToken);
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Server beendet die Verbindung (z.B. Neustart) → Close-Handshake sauber abschließen
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct); }
                    catch (WebSocketException) { /* bereits getrennt */ }
                    return;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var frame = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            var outcome = protocol.Handle(frame);
            foreach (var reply in outcome.Replies) await SendAsync(reply, ct);

            switch (outcome.Event)
            {
                case { Name: "init" }:
                    SetConnected(true);
                    break;
                case { Name: "auth_failed" }:
                    // Meist abgelaufenes Access-Token: eine API-Anfrage löst den Refresh aus,
                    // TokensChanged meldet den Socket dann neu an.
                    Log.Info("Live-Sync: Anmeldung abgelehnt – erneuere Token.");
                    SetConnected(false);
                    _ = RefreshTokenAsync();
                    break;
                case { Name: "user_item_progress_updated" } e when SocketIoProtocol.TryParseProgress(e.Payload) is { } p:
                    ProgressUpdated?.Invoke(this, p);
                    break;
                case { Name: "user_updated" } e:
                    var user = e.Payload.Deserialize<AbsUser>(AbsApiClient.Json);
                    if (user is not null) UserUpdated?.Invoke(this, user);
                    break;
            }
        }
    }

    private async Task SendAsync(string frame, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return;
        await _sendLock.WaitAsync(ct);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private void OnTokensChanged(object? sender, EventArgs e)
    {
        // Neues Access-Token → am bestehenden Socket neu anmelden (der Server prüft nur bei "auth").
        if (_api.AccessToken is { } token && _socket is { State: WebSocketState.Open })
            _ = SendAsync(SocketIoProtocol.AuthFrame(token), _cts.Token);
    }

    private async Task RefreshTokenAsync()
    {
        try { await _api.AuthorizeAsync(_cts.Token); }
        catch (Exception ex) { Log.Info($"Live-Sync: Token-Erneuerung fehlgeschlagen ({ex.Message})"); }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        ConnectionChanged?.Invoke(this, connected);
    }

    public async ValueTask DisposeAsync()
    {
        _api.TokensChanged -= OnTokensChanged;
        _cts.Cancel();
        try { _socket?.Abort(); } catch { /* egal */ }
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
        _cts.Dispose();
    }
}

/// <summary>Ein empfangenes Socket.IO-Event.</summary>
public sealed record SocketIoEvent(string Name, JsonElement Payload);

/// <summary>Antworten auf einen Frame und ggf. das darin enthaltene Event.</summary>
public sealed record SocketIoOutcome(IReadOnlyList<string> Replies, SocketIoEvent? Event);

/// <summary>
/// Minimaler Socket.IO-4-Client-Protokollzustand (nur Default-Namespace, nur Text-Frames).
/// Engine.IO: "0" open, "2" ping → "3" pong, "4" message. Socket.IO: "0" connect, "2" event, "4" connect_error.
/// </summary>
public sealed class SocketIoProtocol(Func<string?> token)
{
    private static readonly SocketIoOutcome Nothing = new([], null);

    public static Uri BuildUri(Uri server)
    {
        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = server.AbsolutePath.TrimEnd('/') + "/socket.io/",
            Query = "EIO=4&transport=websocket",
        };
        return builder.Uri;
    }

    public static string AuthFrame(string accessToken) => "42" + JsonSerializer.Serialize(new object[] { "auth", accessToken });

    public SocketIoOutcome Handle(string frame)
    {
        if (frame.Length == 0) return Nothing;
        switch (frame[0])
        {
            case '0': // Engine.IO open → Socket.IO connect zum Default-Namespace
                return new SocketIoOutcome(["40"], null);
            case '2': // Engine.IO ping → pong
                return new SocketIoOutcome(["3" + frame[1..]], null);
            case '4':
                return HandleSocketIo(frame[1..]);
            default:
                return Nothing;
        }
    }

    private SocketIoOutcome HandleSocketIo(string packet)
    {
        if (packet.Length == 0) return Nothing;
        switch (packet[0])
        {
            case '0': // connect bestätigt → anmelden
                return token() is { } t ? new SocketIoOutcome([AuthFrame(t)], null) : Nothing;
            case '2': // event: 2["name",payload]  (ggf. mit Namespace "/x," oder Ack-Id davor)
                var json = packet[1..];
                var start = json.IndexOf('[');
                if (start < 0) return Nothing;
                try
                {
                    using var doc = JsonDocument.Parse(json[start..]);
                    var array = doc.RootElement;
                    if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) return Nothing;
                    var name = array[0].GetString() ?? "";
                    var payload = array.GetArrayLength() > 1 ? array[1].Clone() : default;
                    return new SocketIoOutcome([], new SocketIoEvent(name, payload));
                }
                catch (JsonException)
                {
                    return Nothing;
                }
            case '4': // connect_error
                Log.Info($"Live-Sync: Verbindung abgelehnt {packet[1..]}");
                return Nothing;
            default:
                return Nothing;
        }
    }

    /// <summary>Payload von user_item_progress_updated: { id, sessionId, deviceDescription, data: MediaProgress }.</summary>
    public static RemoteProgress? TryParseProgress(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("data", out var data)) return null;
        var progress = data.Deserialize<MediaProgress>(AbsApiClient.Json);
        if (progress is null || string.IsNullOrEmpty(progress.LibraryItemId)) return null;
        return new RemoteProgress(
            payload.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
            payload.TryGetProperty("deviceDescription", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            progress);
    }
}
