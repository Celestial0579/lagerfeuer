using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Lagerfeuer.Services;

/// <summary>
/// Macht aus einer gescheiterten Verbindung zum Server einen kurzen deutschen Satz für die Oberfläche.
///
/// Die Texte der .NET-Laufzeit sind englisch und nennen den eigentlichen Grund oft nicht („The SSL connection
/// could not be established, see inner exception.“); Windows steuert je nach Fehler einen deutschen Satz samt
/// Adresse bei. Hier entsteht aus der Art des Fehlers ein Satz in Du-Form, der wenn möglich den Ausweg nennt.
/// Die vollständige Ausnahme bleibt als <see cref="Exception.InnerException"/> erhalten und gehört ins Log.
/// </summary>
public static class ConnectionFailure
{
    public const string Refused = "Unter dieser Adresse antwortet kein Server (Verbindung abgelehnt).";
    public const string NoRoute = "Der Server ist von diesem Rechner aus nicht erreichbar.";
    public const string Timeout = "Der Server antwortet nicht (Zeitüberschreitung).";
    public const string Aborted = "Der Server hat die Verbindung abgebrochen.";
    public const string Certificate = "Das Zertifikat des Servers ist nicht vertrauenswürdig. Lagerfeuer verbindet sich dann nicht.";
    public const string NoHttps = "Über HTTPS kam keine Verbindung zustande. Läuft dein Server ohne HTTPS, gib die Adresse mit http:// ein.";
    public const string NotAudiobookshelf = "Unter dieser Adresse antwortet kein Audiobookshelf-Server.";
    public const string Generic = "Keine Verbindung zum Server.";

    /// <param name="ex">Die Ausnahme der Anfrage, wie <see cref="HttpClient"/> sie wirft.</param>
    /// <param name="server">Adresse des Servers, für den Namen in der Meldung. Darf fehlen.</param>
    public static string Describe(Exception ex, Uri? server = null)
    {
        var chain = Chain(ex).ToList();

        // Die Antwort kam an, war aber kein JSON von Audiobookshelf (etwa die Seite eines anderen Dienstes).
        if (chain.Any(e => e is JsonException or NotSupportedException)) return NotAudiobookshelf;

        var http = chain.OfType<HttpRequestException>().FirstOrDefault();
        var socket = chain.OfType<SocketException>().FirstOrDefault();
        var auth = chain.OfType<AuthenticationException>().FirstOrDefault();

        // TLS. Ein nicht vertrauenswürdiges Zertifikat ist etwas anderes als ein Server, der gar kein HTTPS
        // spricht: Im zweiten Fall hilft http://, im ersten soll niemand dorthin ausweichen.
        if (auth is not null || http?.HttpRequestError == HttpRequestError.SecureConnectionError)
            return auth is not null && auth.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ? Certificate : NoHttps;

        if (http?.HttpRequestError == HttpRequestError.NameResolutionError
            || socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
            return server is { Host.Length: > 0 }
                ? $"Der Servername „{server.Host}“ ist unbekannt. Prüfe die Adresse."
                : "Der Servername ist unbekannt. Prüfe die Adresse.";

        if (socket is not null)
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => Refused,
                SocketError.TimedOut => Timeout,
                SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostDown => NoRoute,
                SocketError.ConnectionReset or SocketError.ConnectionAborted => Aborted,
                _ => Generic,
            };

        // Abgelaufene Zeit: HttpClient meldet sie als abgebrochene Aufgabe.
        if (chain.Any(e => e is OperationCanceledException or TimeoutException)) return Timeout;

        return http?.HttpRequestError switch
        {
            HttpRequestError.ResponseEnded => Aborted,
            HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError or HttpRequestError.VersionNegotiationError => NotAudiobookshelf,
            _ => Generic,
        };
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) yield return e;
    }
}
