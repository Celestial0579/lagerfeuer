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
///    ABS leitet weiter auf  lagerfeuer://oauth?code&amp;state  (Rückfall: audiobookshelf://oauth).
/// 3. Windows startet dafür Lagerfeuer (Protokoll-Handler, siehe <see cref="ProtocolRegistration"/>); die neue
///    Instanz reicht die URL an die laufende weiter (<see cref="SingleInstance"/>) → <see cref="OnCallback"/>.
///    state wird geprüft.
/// 4. Code-Tausch: GET {server}/auth/openid/callback?state&amp;code&amp;code_verifier – MIT den Cookies aus Schritt 1.
///    Antwort ist das Login-Payload inkl. accessToken + refreshToken.
///
/// Server-Voraussetzungen: "lagerfeuer://oauth" in den erlaubten Mobile-Redirect-URIs. Fehlt die Adresse dort
/// (ABS antwortet in Schritt 1 mit 400 „Invalid redirect_uri“), versucht Lagerfeuer "audiobookshelf://oauth",
/// den ABS-Standard der offiziellen Apps;
/// im IdP muss  https://&lt;abs&gt;/auth/openid/mobile-redirect  als Redirect-URI eingetragen sein.
/// </summary>
public static class OpenIdLogin
{
    public const string RedirectUri = ProtocolRegistration.Scheme + "://oauth";
    public const string FallbackRedirectUri = ProtocolRegistration.FallbackScheme + "://oauth";

    public static Task<LoginResponse?> LoginAsync(AbsApiClient api, Uri server, CancellationToken ct = default)
        // Eigener HttpClient: Cookie-Jar für die ABS-Session, Redirects nicht automatisch folgen.
        => LoginAsync(api, server, new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        }, OpenInBrowser, ct);

    /// <param name="handler">Zugang zum Server; für Tests von außen vorgegeben. Wird am Ende freigegeben.</param>
    /// <param name="openBrowser">Öffnet die Anmeldeseite; für Tests von außen vorgegeben.</param>
    /// <param name="register">Trägt Lagerfeuer für ein Protokoll ein (false: gehört einem anderen Programm);
    /// für Tests von außen vorgegeben.</param>
    internal static async Task<LoginResponse?> LoginAsync(AbsApiClient api, Uri server, HttpMessageHandler handler,
        Action<Uri> openBrowser, CancellationToken ct = default, Func<string, bool>? register = null)
    {
        register ??= ProtocolRegistration.EnsureRegistered;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // Schritt 1: erst die eigene Adresse, dann die der offiziellen Apps
        Uri? authorizeUrl = null;
        string? scheme = null;
        foreach (var (redirectUri, candidate) in new[]
                 {
                     (RedirectUri, ProtocolRegistration.Scheme),
                     (FallbackRedirectUri, ProtocolRegistration.FallbackScheme),
                 })
        {
            var startUrl = api.Resolve(
                "auth/openid" +
                $"?code_challenge={challenge}" +
                "&code_challenge_method=S256" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                $"&state={state}",
                server);

            using var resp = await http.GetAsync(startUrl, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is not null)
            {
                authorizeUrl = ResolveAuthorizeUrl(startUrl, resp.Headers.Location);
                scheme = candidate;
                break;
            }

            var body = await SafeReadAsync(resp);
            if (resp.StatusCode != HttpStatusCode.BadRequest || !body.Contains("redirect_uri"))
                throw new AbsApiException($"SSO konnte nicht gestartet werden ({(int)resp.StatusCode}): {body}", resp.StatusCode);
            Log.Info($"Server erlaubt die Redirect-URI {redirectUri} nicht.");
        }

        if (authorizeUrl is null || scheme is null)
            throw new AbsApiException(
                $"Der Server erlaubt weder die Redirect-URI „{RedirectUri}“ noch „{FallbackRedirectUri}“. Bitte in " +
                $"Audiobookshelf unter Einstellungen → Authentifizierung bei den erlaubten Mobile-Redirect-URIs „{RedirectUri}“ eintragen.",
                HttpStatusCode.BadRequest);

        // Schritte 2 + 3
        if (!register(scheme))
            throw new AbsApiException(
                $"Der Server erlaubt nur „{FallbackRedirectUri}“, und diese Adresse gehört unter Windows einem anderen " +
                $"Programm. Bitte in Audiobookshelf unter Einstellungen → Authentifizierung bei den erlaubten " +
                $"Mobile-Redirect-URIs „{RedirectUri}“ eintragen.");
        var pending = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingState = state;
        _pending = pending;
        _lastAuthorizeUrl = authorizeUrl;
        Uri callback;
        try
        {
            openBrowser(authorizeUrl);
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
            if (Interlocked.CompareExchange(ref _pending, null, pending) == pending) _pendingState = null;
            _lastAuthorizeUrl = null;
        }

        // Den state zuerst: Nur ein Rückruf zu genau dieser Anmeldung darf sie beenden, auch mit einem Fehler
        // (GHSA-9cq7-jcr5-vp2c). OnCallback lässt fremde Rückrufe schon gar nicht durch; das hier ist die zweite Sicherung.
        var query = HttpUtility.ParseQueryString(callback.Query);
        if (!StateMatches(query["state"], state))
            throw new AbsApiException("SSO-Anmeldung fehlgeschlagen: state stimmt nicht überein.");

        var error = query["error"];
        if (!string.IsNullOrEmpty(error))
            throw new AbsApiException($"SSO-Anmeldung fehlgeschlagen: {DescribeError(error)}");

        var code = query["code"];
        if (string.IsNullOrEmpty(code))
            throw new AbsApiException("SSO-Anmeldung fehlgeschlagen: kein Code erhalten.");

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
    private static string? _pendingState;
    private static Uri? _lastAuthorizeUrl;

    /// <summary>Vergleicht den state in konstanter Zeit; fehlt einer von beiden, passt er nicht.</summary>
    internal static bool StateMatches(string? received, string? expected)
        => !string.IsNullOrEmpty(received) && !string.IsNullOrEmpty(expected)
           && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));

    /// <summary>
    /// Text zum Fehlercode aus dem Rückruf. „error_description“ wird nie angezeigt: Den Rückruf kann jede Webseite
    /// auslösen, der Text käme also von irgendwem. Bekannte OAuth-Codes bekommen einen deutschen Satz, andere
    /// schlichte Codes erscheinen als Code, alles andere als allgemeiner Hinweis.
    /// </summary>
    internal static string DescribeError(string error) => error switch
    {
        "access_denied" => "Die Anmeldung wurde abgelehnt oder abgebrochen.",
        "login_required" or "interaction_required" or "consent_required" => "Der Anbieter verlangt eine erneute Anmeldung.",
        "server_error" or "temporarily_unavailable" => "Der Anmeldedienst hat einen Fehler gemeldet.",
        _ when System.Text.RegularExpressions.Regex.IsMatch(error, @"\A[a-z_]{1,40}\z") => $"Fehlercode {error}.",
        _ => "Der Anmeldedienst hat einen Fehler gemeldet.",
    };

    /// <summary>Wartet gerade eine SSO-Anmeldung auf den Browser?</summary>
    public static bool IsWaiting => _pending is not null;

    /// <summary>Wird aufgerufen, wenn Windows Lagerfeuer mit lagerfeuer://oauth?… (oder audiobookshelf://oauth?…)
    /// aktiviert. Welcher Anmeldung der Rückruf gehört, entscheidet danach der state.</summary>
    public static bool OnCallback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ProtocolRegistration.IsCallback(url)) return false;
        var pending = _pending;
        if (pending is null)
        {
            Log.Info("SSO-Callback empfangen, aber keine Anmeldung aktiv – ignoriert.");
            return false;
        }
        // Nur der Rückruf mit dem state dieser Anmeldung darf sie beenden. Einen fremden (etwa von einer Webseite,
        // die lagerfeuer://oauth?error=… aufruft) übergeht Lagerfeuer und wartet weiter (GHSA-9cq7-jcr5-vp2c).
        if (!StateMatches(HttpUtility.ParseQueryString(uri.Query)["state"], _pendingState))
        {
            Log.Info("SSO-Callback mit fremdem state – ignoriert.");
            return false;
        }
        return pending.TrySetResult(uri);
    }

    /// <summary>Öffnet die Login-Seite erneut im Browser (z.B. wenn der Tab geschlossen wurde).</summary>
    public static void ReopenBrowser()
    {
        if (_lastAuthorizeUrl is { } url) OpenInBrowser(url);
    }

    /// <summary>
    /// Die Adresse der Anmeldeseite aus der Antwort des Servers (Kopfzeile Location), bei Bedarf ergänzt um die
    /// Adresse der Anfrage.
    ///
    /// Sie kommt vom Server und geht an Windows zum Öffnen. Angenommen wird deshalb nur eine Web-Adresse: Mit
    /// „file:“ ließe ein Server sonst ein Programm auf diesem Rechner oder einer Netzfreigabe starten, mit
    /// anderen Schemata andere Anwendungen aufrufen.
    /// </summary>
    internal static Uri ResolveAuthorizeUrl(Uri startUrl, Uri location)
    {
        var url = location.IsAbsoluteUri ? location : new Uri(startUrl, location);
        if (!WebLinks.IsWebAddress(url))
            throw new AbsApiException(
                "Der Server nennt als Anmeldeseite keine Web-Adresse (http oder https). " +
                "Die SSO-Anmeldung wurde abgebrochen, geöffnet wurde nichts.");
        return url;
    }

    private static void OpenInBrowser(Uri url) => Process.Start(WebLinks.OpenInBrowser(url));

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
