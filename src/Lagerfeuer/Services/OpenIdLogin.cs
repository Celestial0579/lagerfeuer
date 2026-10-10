using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Lagerfeuer.Models;
using System.Diagnostics;

namespace Lagerfeuer.Services;

/// <summary>
/// SSO-Anmeldung (z.B. Authentik) über den OpenID-Connect-Flow von Audiobookshelf für native Clients
/// (geprüft gegen ABS v2.37.1, server/Auth.js):
///
/// 1. Client ruft selbst  GET {server}/auth/openid?code_challenge&amp;code_challenge_method=S256&amp;redirect_uri&amp;response_type=code&amp;state
///    auf (ohne Redirect-Folgen, mit Cookie-Jar). ABS antwortet mit 302 → Authorize-URL des IdP und setzt
///    die Cookies "connect.sid" + "auth_method=openid-mobile".
/// 2. Die Authorize-URL wird im Standardbrowser geöffnet – so wird eine bestehende IdP-Sitzung
///    (z.B. Authentik) wiederverwendet. Nach dem Login leitet der IdP auf {server}/auth/openid/mobile-redirect,
///    ABS leitet weiter auf  audiobookshelf://oauth?code&amp;state.
/// 3. Windows startet dafür Lagerfeuer (Protokoll-Handler, siehe <see cref="ProtocolRegistration"/>); die neue
///    Instanz reicht die URL an die laufende weiter (<see cref="SingleInstance"/>) → <see cref="OnCallback"/>.
///    state wird geprüft.
/// 4. Code-Tausch: GET {server}/auth/openid/callback?state&amp;code&amp;code_verifier – MIT den Cookies aus Schritt 1.
///    Antwort ist das Login-Payload inkl. accessToken + refreshToken.
///
/// Server-Voraussetzungen: "audiobookshelf://oauth" in den erlaubten Mobile-Redirect-URIs (ABS-Standard);
/// im IdP muss  https://&lt;abs&gt;/auth/openid/mobile-redirect  als Redirect-URI eingetragen sein.
/// </summary>
public static class OpenIdLogin
{
    public const string RedirectUri = "audiobookshelf://oauth";

    public static async Task<LoginResponse?> LoginAsync(AbsApiClient api, Uri server, CancellationToken ct = default)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        // Eigener HttpClient: Cookie-Jar für die ABS-Session, Redirects nicht automatisch folgen.
        var cookies = new CookieContainer();
        using var http = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
        })
        { Timeout = TimeSpan.FromSeconds(30) };

        // Schritt 1
        var startUrl = api.Resolve(
            "auth/openid" +
            $"?code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&response_type=code" +
            $"&state={state}",
            server);

        Uri authorizeUrl;
        using (var resp = await http.GetAsync(startUrl, ct))
        {
            if ((int)resp.StatusCode is < 300 or >= 400 || resp.Headers.Location is null)
            {
                var body = await SafeReadAsync(resp);
                throw new AbsApiException(resp.StatusCode == HttpStatusCode.BadRequest && body.Contains("redirect_uri")
                    ? $"Der Server erlaubt die Redirect-URI „{RedirectUri}“ nicht. Bitte in Audiobookshelf unter " +
                      "Einstellungen → Authentifizierung bei den erlaubten Mobile-Redirect-URIs eintragen."
                    : $"SSO konnte nicht gestartet werden ({(int)resp.StatusCode}): {body}", resp.StatusCode);
            }
            authorizeUrl = resp.Headers.Location.IsAbsoluteUri
                ? resp.Headers.Location
                : new Uri(startUrl, resp.Headers.Location);
        }

        // Schritte 2 + 3
        ProtocolRegistration.EnsureRegistered();
        var pending = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        _lastAuthorizeUrl = authorizeUrl;
        Uri callback;
        try
        {
            OpenInBrowser(authorizeUrl);
            // ABS hält den state nur 10 Minuten vor.
            callback = await pending.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
        }
        catch (TimeoutException)
        {
            throw new AbsApiException("Die SSO-Anmeldung wurde nicht innerhalb von 10 Minuten abgeschlossen.");
        }
        catch (OperationCanceledException)
        {
            return null; // vom Benutzer abgebrochen
        }
        finally
        {
            Interlocked.CompareExchange(ref _pending, null, pending);
            _lastAuthorizeUrl = null;
        }

        var query = HttpUtility.ParseQueryString(callback.Query);
        var error = query["error"];
        if (!string.IsNullOrEmpty(error))
            throw new AbsApiException($"SSO-Anmeldung fehlgeschlagen: {query["error_description"] ?? error}");

        var code = query["code"];
        if (string.IsNullOrEmpty(code))
            throw new AbsApiException("SSO-Anmeldung fehlgeschlagen: kein Code erhalten.");
        if (query["state"] != state)
            throw new AbsApiException("SSO-Anmeldung fehlgeschlagen: state stimmt nicht überein.");

        // Schritt 4
        var exchangeUrl = api.Resolve(
            $"auth/openid/callback?state={Uri.EscapeDataString(state)}&code={Uri.EscapeDataString(code)}" +
            $"&code_verifier={Uri.EscapeDataString(verifier)}",
            server);
        using var exchange = await http.GetAsync(exchangeUrl, ct);
        if (!exchange.IsSuccessStatusCode) // inkl. 3xx (Server leitet bei Fehlern auf /login?error=… um)
        {
            var detail = exchange.Headers.Location?.ToString() ?? await SafeReadAsync(exchange);
            throw new AbsApiException($"SSO-Code-Tausch fehlgeschlagen ({(int)exchange.StatusCode}): {detail}",
                exchange.StatusCode);
        }

        var login = await exchange.Content.ReadFromJsonAsync<LoginResponse>(AbsApiClient.Json, ct)
                    ?? throw new AbsApiException("Leere Antwort vom Server.");
        api.ApplyLogin(server, login);
        return login;
    }

    private static TaskCompletionSource<Uri>? _pending;
    private static Uri? _lastAuthorizeUrl;

    /// <summary>Wartet gerade eine SSO-Anmeldung auf den Browser?</summary>
    public static bool IsWaiting => _pending is not null;

    /// <summary>Wird aufgerufen, wenn Windows Lagerfeuer mit audiobookshelf://oauth?… aktiviert.</summary>
    public static bool OnCallback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ProtocolRegistration.IsCallback(url)) return false;
        var pending = _pending;
        if (pending is null)
        {
            Log.Info("SSO-Callback empfangen, aber keine Anmeldung aktiv – ignoriert.");
            return false;
        }
        return pending.TrySetResult(uri);
    }

    /// <summary>Öffnet die Login-Seite erneut im Browser (z.B. wenn der Tab geschlossen wurde).</summary>
    public static void ReopenBrowser()
    {
        if (_lastAuthorizeUrl is { } url) OpenInBrowser(url);
    }

    private static void OpenInBrowser(Uri url)
        => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try
        {
            var s = (await resp.Content.ReadAsStringAsync()).Trim();
            return s.Length > 200 ? s[..200] : s;
        }
        catch
        {
            return "";
        }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
