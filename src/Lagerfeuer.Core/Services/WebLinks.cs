using System.Diagnostics;

namespace Lagerfeuer.Services;

/// <summary>
/// Adressen, die Lagerfeuer an Windows zum Öffnen weitergibt (Anmeldeseite der SSO-Anmeldung, Seite eines
/// Release). Sie stammen aus Antworten eines Servers. Windows öffnet über die Shell aber nicht nur Web-Seiten:
/// Eine Adresse wie „file:///C:/…/programm.exe“ startet das Programm, andere Schemata rufen andere
/// Anwendungen auf. Weitergegeben werden deshalb nur Web-Adressen.
/// </summary>
public static class WebLinks
{
    /// <summary>Ist das eine vollständige Web-Adresse, also http oder https mit einem Rechnernamen?</summary>
    public static bool IsWebAddress(Uri? url)
        => url is { IsAbsoluteUri: true }
           && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
           && !string.IsNullOrEmpty(url.Host);

    /// <summary>
    /// Startangaben, um eine Web-Adresse im Standardbrowser zu öffnen. Alles andere wird abgelehnt, auch wenn
    /// der Aufrufer schon geprüft hat: Hier ist die letzte Stelle vor Windows.
    /// </summary>
    public static ProcessStartInfo OpenInBrowser(Uri url)
    {
        if (!IsWebAddress(url))
            throw new ArgumentException("Geöffnet werden nur Web-Adressen (http oder https).", nameof(url));
        // AbsoluteUri ist die kodierte Form: keine Leerzeichen und keine Anführungszeichen, die sich in die
        // Befehlszeile des Browsers mogeln könnten.
        return new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
    }
}
