using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

public sealed class AbsApiException(string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// HTTP-Client für die Audiobookshelf-API. Unterstützt Legacy-Tokens und
/// JWT Access-/Refresh-Tokens (ABS ≥ 2.26) inkl. automatischem Refresh bei 401.
/// </summary>
public sealed class AbsApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public const string ClientName = "Lagerfeuer für Windows";
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public Uri? ServerUri { get; private set; }
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }

    /// <summary>Tokens wurden neu gesetzt oder rotiert und sollten persistiert werden.</summary>
    public event EventHandler? TokensChanged;

    /// <summary>Refresh ist fehlgeschlagen – Benutzer muss sich neu anmelden.</summary>
    public event EventHandler? SessionExpired;

    public AbsApiClient() : this(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    })
    {
    }

    /// <summary>Mit eigenem HTTP-Handler (z.B. für Tests).</summary>
    public AbsApiClient(HttpMessageHandler handler)
    {
        // Kein globales Timeout: Audio-Downloads laufen lange. API-Aufrufe nutzen ApiTimeout.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lagerfeuer-Windows/0.1");
    }

    public bool IsConfigured => ServerUri is not null && AccessToken is not null;

    public static Uri NormalizeServerUrl(string input)
    {
        var s = input.Trim();
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new AbsApiException("Ungültige Server-Adresse.");
        return uri;
    }

    public void Configure(Uri server, string? accessToken, string? refreshToken)
    {
        ServerUri = server;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
    }

    public void ClearAuth()
    {
        AccessToken = null;
        RefreshToken = null;
    }

    /// <summary>Löst einen Server-Pfad (z.B. contentUrl "/api/items/…") relativ zur Server-Basis auf.</summary>
    public Uri Resolve(string path, Uri? server = null)
    {
        server ??= ServerUri ?? throw new InvalidOperationException("Kein Server konfiguriert.");
        if (Uri.TryCreate(path, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http")) return abs;
        var basePath = server.AbsolutePath; // endet immer auf "/"
        // Läuft ABS unter einem Unterpfad, enthalten contentUrls diesen bereits.
        if (path.StartsWith('/') && basePath.Length > 1 && path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return new Uri(server, path);
        return new Uri(server, path.TrimStart('/'));
    }

    // ---------- Anmeldung ----------

    public async Task<ServerStatus> GetStatusAsync(Uri server, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        try
        {
            using var resp = await _http.GetAsync(Resolve("status", server), cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new AbsApiException($"Server antwortet mit {(int)resp.StatusCode}. Ist das ein Audiobookshelf-Server?", resp.StatusCode);
            return await resp.Content.ReadFromJsonAsync<ServerStatus>(Json, cts.Token) ?? new ServerStatus();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new AbsApiException($"Server nicht erreichbar: {ex.Message}", null, ex);
        }
    }

    public async Task<LoginResponse> LoginAsync(Uri server, string username, string password, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, Resolve("login", server))
        {
            Content = JsonContent.Create(new { username, password }, options: Json),
        };
        req.Headers.Add("x-return-tokens", "true");
        using var resp = await _http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AbsApiException("Benutzername oder Passwort ist falsch.", resp.StatusCode);
        await EnsureSuccessAsync(resp);
        var login = await resp.Content.ReadFromJsonAsync<LoginResponse>(Json, cts.Token)
                    ?? throw new AbsApiException("Leere Antwort vom Server.");
        ApplyLogin(server, login);
        return login;
    }

    /// <summary>Übernimmt Tokens aus einem Login-Payload (lokal oder SSO, siehe <see cref="OpenIdLogin"/>).</summary>
    public void ApplyLogin(Uri server, LoginResponse login)
    {
        ServerUri = server;
        // accessToken gibt es seit ABS 2.26 (JWT, 1 h gültig). user.token ist das veraltete,
        // nicht ablaufende Token und dient nur als Fallback für ältere Server.
        AccessToken = login.User.AccessToken ?? login.User.Token
                      ?? throw new AbsApiException("Server hat kein Token geliefert.");
        RefreshToken = login.User.RefreshToken;
        TokensChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<LoginResponse> AuthorizeAsync(CancellationToken ct = default)
        => SendJsonAsync<LoginResponse>(HttpMethod.Post, "api/authorize", null, ct);

    public async Task LogoutAsync()
    {
        try
        {
            if (ServerUri is not null && RefreshToken is not null)
            {
                using var cts = WithTimeout(default);
                using var req = new HttpRequestMessage(HttpMethod.Post, Resolve("logout"));
                req.Headers.Add("x-refresh-token", RefreshToken);
                if (AccessToken is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                using var _ = await _http.SendAsync(req, cts.Token);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Logout am Server fehlgeschlagen", ex);
        }
        finally
        {
            ClearAuth();
        }
    }

    // ---------- Bibliothek ----------

    public async Task<List<Library>> GetLibrariesAsync(CancellationToken ct = default)
        => (await SendJsonAsync<LibrariesResponse>(HttpMethod.Get, "api/libraries", null, ct)).Libraries;

    public Task<LibraryItemsResponse> GetLibraryItemsAsync(string libraryId, int page, int limit, CancellationToken ct = default)
        => SendJsonAsync<LibraryItemsResponse>(HttpMethod.Get,
            $"api/libraries/{libraryId}/items?limit={limit}&page={page}&sort=media.metadata.title&minified=1", null, ct);

    public async Task<List<LibraryItem>> SearchAsync(string libraryId, string query, CancellationToken ct = default)
        => (await SendJsonAsync<SearchResponse>(HttpMethod.Get,
                $"api/libraries/{libraryId}/search?q={Uri.EscapeDataString(query)}&limit=50", null, ct))
            .Book.Select(b => b.LibraryItem).ToList();

    public Task<LibraryItem> GetItemAsync(string itemId, CancellationToken ct = default)
        => SendJsonAsync<LibraryItem>(HttpMethod.Get, $"api/items/{itemId}?expanded=1&include=progress", null, ct);

    public async Task<List<LibraryItem>> GetItemsInProgressAsync(CancellationToken ct = default)
        => (await SendJsonAsync<ItemsInProgressResponse>(HttpMethod.Get, "api/me/items-in-progress?limit=50", null, ct)).LibraryItems;

    public Task<AbsUser> GetMeAsync(CancellationToken ct = default)
        => SendJsonAsync<AbsUser>(HttpMethod.Get, "api/me", null, ct);

    /// <summary>Gesamter Hörfortschritt des Benutzers (ABS ≥ 2.36).</summary>
    public async Task<List<MediaProgress>> GetAllProgressAsync(CancellationToken ct = default)
        => (await SendJsonAsync<MediaProgressResponse>(HttpMethod.Get, "api/me/progress", null, ct)).MediaProgress;

    public async Task<byte[]?> GetCoverAsync(string itemId, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            Resolve($"api/items/{itemId}/cover?width=600&format=jpeg")), HttpCompletionOption.ResponseContentRead, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync(cts.Token);
    }

    // ---------- Wiedergabe ----------

    public Task<PlaybackSession> StartPlaybackAsync(string itemId, string deviceId, CancellationToken ct = default)
        => SendJsonAsync<PlaybackSession>(HttpMethod.Post, $"api/items/{itemId}/play", new
        {
            deviceInfo = new
            {
                deviceId,
                clientName = ClientName,
                clientVersion = "0.1.0",
                manufacturer = "Microsoft",
                model = "Windows",
                deviceName = Environment.MachineName,
            },
            forceDirectPlay = true,
            forceTranscode = false,
            mediaPlayer = "WinUI MediaPlayer",
            supportedMimeTypes = new[]
            {
                "audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/x-m4b", "audio/aac", "audio/flac",
                "audio/x-flac", "audio/wav", "audio/x-wav", "audio/ogg", "audio/opus", "audio/webm",
            },
        }, ct);

    public Task SyncSessionAsync(string sessionId, double currentTime, double timeListened, double duration,
        CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Post, $"api/session/{sessionId}/sync",
            new { currentTime, timeListened, duration }, ct);

    public Task CloseSessionAsync(string sessionId, double currentTime, double timeListened, double duration,
        CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Post, $"api/session/{sessionId}/close",
            new { currentTime, timeListened, duration }, ct);

    public Task UpdateProgressAsync(string itemId, double currentTime, double duration, bool isFinished,
        CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Patch, $"api/me/progress/{itemId}", new
        {
            currentTime,
            duration,
            progress = duration > 0 ? Math.Clamp(currentTime / duration, 0, 1) : 0,
            isFinished,
        }, ct);

    // ---------- Infrastruktur ----------

    /// <summary>Sendet einen authentifizierten Request; bei 401 wird einmal per Refresh-Token erneuert.</summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, HttpCompletionOption option,
        CancellationToken ct)
    {
        var usedToken = AccessToken;
        var resp = await SendOnceAsync(factory, usedToken, option, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

        resp.Dispose();
        if (RefreshToken is not null && await TryRefreshAsync(usedToken, ct))
        {
            resp = await SendOnceAsync(factory, AccessToken, option, ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;
            resp.Dispose();
        }

        SessionExpired?.Invoke(this, EventArgs.Empty);
        throw new AbsApiException("Die Anmeldung ist abgelaufen. Bitte erneut anmelden.", HttpStatusCode.Unauthorized);
    }

    private Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> factory, string? token,
        HttpCompletionOption option, CancellationToken ct)
    {
        var req = factory();
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(req, option, ct);
    }

    private async Task<bool> TryRefreshAsync(string? failedToken, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (AccessToken != failedToken) return AccessToken is not null; // parallel bereits erneuert
            using var req = new HttpRequestMessage(HttpMethod.Post, Resolve("auth/refresh"));
            req.Headers.Add("x-refresh-token", RefreshToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"Token-Refresh abgelehnt: {(int)resp.StatusCode}");
                return false;
            }
            var login = await resp.Content.ReadFromJsonAsync<LoginResponse>(Json, ct);
            var newAccess = login?.User.AccessToken ?? login?.User.Token;
            if (newAccess is null) return false;
            AccessToken = newAccess;
            RefreshToken = login!.User.RefreshToken ?? RefreshToken;
            TokensChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var cts = WithTimeout(ct);
        try
        {
            using var resp = await SendAsync(() => CreateRequest(method, path, body),
                HttpCompletionOption.ResponseContentRead, cts.Token);
            await EnsureSuccessAsync(resp);
            return await resp.Content.ReadFromJsonAsync<T>(Json, cts.Token)
                   ?? throw new AbsApiException("Leere Antwort vom Server.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new AbsApiException($"Serverfehler: {ex.Message}", null, ex);
        }
    }

    private async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var cts = WithTimeout(ct);
        try
        {
            using var resp = await SendAsync(() => CreateRequest(method, path, body),
                HttpCompletionOption.ResponseContentRead, cts.Token);
            await EnsureSuccessAsync(resp);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new AbsApiException($"Serverfehler: {ex.Message}", null, ex);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        var req = new HttpRequestMessage(method, Resolve(path));
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return req;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string detail;
        try { detail = (await resp.Content.ReadAsStringAsync()).Trim(); }
        catch { detail = ""; }
        if (detail.Length > 200) detail = detail[..200];
        throw new AbsApiException(
            $"Server meldet {(int)resp.StatusCode} {resp.ReasonPhrase}{(detail.Length > 0 ? ": " + detail : "")}",
            resp.StatusCode);
    }

    private static CancellationTokenSource WithTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ApiTimeout);
        return cts;
    }

    public void Dispose() => _http.Dispose();
}
