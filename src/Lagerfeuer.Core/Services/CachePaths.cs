using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lagerfeuer.Services;

/// <summary>
/// Dateinamen im Audio- und Cover-Cache aus Angaben des Servers (IDs, Dateiendung). Der Server ist keine
/// Vertrauensgrenze: Eine ID wie „..“ oder eine Endung wie „mp3\..\..\x.cmd“ darf nie zu einem Pfad außerhalb
/// des Cache-Ordners führen (GHSA-wg65-jc44-jwjf). Darum gilt dreierlei:
/// <list type="bullet">
/// <item>IDs kommen nur unverändert in den Dateinamen, wenn sie aus Buchstaben, Ziffern, „_“ und „-“ bestehen
/// (so sehen die IDs von Audiobookshelf aus) und kein Gerätename von Windows sind (CON, NUL, COM1 …). Alles
/// andere wird zu einem Hash, bleibt also eindeutig.</item>
/// <item>Endungen nur aus einer festen Liste von Audioformaten, sonst aus dem MIME-Typ, ersatzweise „.bin“.</item>
/// <item>Jeder fertige Pfad wird vollständig aufgelöst und muss danach im Cache-Ordner liegen, sonst gibt es
/// eine Ausnahme statt einer Datei.</item>
/// </list>
/// </summary>
public static partial class CachePaths
{
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainId();

    /// <summary>
    /// Endungen, die der Cache annimmt: nur Audioformate. Im Cache-Ordner, den die Einstellungen im Explorer öffnen,
    /// sollen keine .url, .lnk, .scf, .cmd oder .hta vom Server liegen (eine .url mit Symbol auf einer Freigabe
    /// verrät beim bloßen Anzeigen den NTLM-Hash; was die App schreibt, trägt kein Mark-of-the-Web).
    /// </summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".opus", ".oga", ".wav", ".webm", ".mp4",
    };

    // Gerätenamen von Windows: Als Datei- oder Ordnername (auch mit Endung) lassen sie sich nicht anlegen.
    [GeneratedRegex(@"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    /// <summary>Teil eines Dateinamens für eine ID vom Server: unverändert, wenn schlicht, sonst „h_“ plus Hash.</summary>
    public static string SafeId(string? id)
    {
        id ??= "";
        return PlainId().IsMatch(id) && !ReservedName().IsMatch(id)
            ? id
            : "h_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32].ToLowerInvariant();
    }

    /// <summary>Dateiendung mit Punkt, klein geschrieben: die des Servers nur, wenn sie ein bekanntes Audioformat ist,
    /// sonst aus dem MIME-Typ, ersatzweise „.bin“.</summary>
    public static string SafeExtension(string? ext, string? mimeType)
    {
        if (!string.IsNullOrEmpty(ext))
        {
            var dotted = (ext.StartsWith('.') ? ext : "." + ext).ToLowerInvariant();
            if (AudioExtensions.Contains(dotted)) return dotted;
        }
        return ExtensionFromMime(mimeType);
    }

    /// <summary>
    /// Löst <paramref name="path"/> vollständig auf und gibt ihn zurück, wenn er innerhalb von
    /// <paramref name="root"/> liegt (nicht der Ordner selbst). Sonst <see cref="InvalidOperationException"/>.
    /// </summary>
    public static string Within(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || full.Length == fullRoot.Length)
            throw new InvalidOperationException("Ein Pfad aus Angaben des Servers liegt nicht im Cache-Ordner – nichts gespeichert.");
        return full;
    }

    internal static string ExtensionFromMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/x-m4a" or "audio/aac" => ".m4a",
        "audio/x-m4b" => ".m4b",
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/ogg" or "audio/opus" => ".ogg",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/webm" => ".webm",
        _ => ".bin",
    };
}
