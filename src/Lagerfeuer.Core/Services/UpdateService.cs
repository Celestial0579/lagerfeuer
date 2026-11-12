using System.Diagnostics;
using System.Reflection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lagerfeuer.Services;

/// <summary>
/// Prüft die GitHub-Releases des Projekts auf eine neuere Version und spielt sie ein
/// (Prinzip wie in Lodestone: releases/latest abfragen, Tag als Versionsnummer vergleichen,
/// Entwürfe und Vorabversionen ignorieren).
///
/// Erwartete Release-Assets (erzeugt von build/publish.ps1):
///   Lagerfeuer-&lt;ver&gt;-win-x64-setup.exe   → installierte Version: Setup still mit /S /RESTART ausführen
///   Lagerfeuer-&lt;ver&gt;-win-x64.exe         → portable Version: laufende EXE austauschen und neu starten
/// Die SHA-256-Prüfsumme, die GitHub für jedes Asset liefert, wird vor dem Ausführen geprüft.
/// Liefert GitHub keine, wird nichts geladen und nichts gestartet.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryOwner = "Celestial0579";
    public const string RepositoryName = "lagerfeuer";
    public static readonly string RepositoryUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}";

    /// <summary>Argument für den Neustart nach einem Update: die neue Instanz wartet auf das Ende der alten.</summary>
    public const string AfterUpdateArg = "--after-update";

    private static readonly Assembly AppAssembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;

    // Reihenfolge wichtig: CreateClient() verwendet CurrentVersion.
    public static Version CurrentVersion { get; } = Normalize(AppAssembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient SharedHttp = CreateClient();

    /// <summary>Single-File-EXE (portabel) statt Installation?</summary>
    public static bool IsPortable => IsSingleFileExe(AppAssembly.Location, Environment.ProcessPath);

    /// <summary>
    /// Trägt die EXE des Prozesses die ganze App in sich? Nur dann darf der Updater sie gegen eine neue
    /// Single-File-EXE tauschen.
    ///
    /// build/publish.ps1 baut die portable Fassung mit IncludeAllContentForSelfExtract: Sie entpackt sich beim
    /// Start nach %TEMP%\.net und lädt ihre Bibliotheken von dort. <see cref="Assembly.Location"/> ist dann
    /// nicht leer, sondern zeigt in diesen Ordner. Die frühere Prüfung auf „leer“ hielt deshalb jede portable
    /// EXE für eine Installation: Sie lud das Setup und installierte Lagerfeuer, statt sich selbst zu tauschen.
    ///
    /// In einer Installation ist die EXE des Prozesses der Starter „Lagerfeuer.exe“, und die Bibliothek
    /// „Lagerfeuer.dll“ liegt neben ihm. Die Single-File-EXE trägt beides in sich und entpackt nur die
    /// Bibliothek. Portabel ist die App also, wenn die Bibliothek in einem anderen Ordner liegt als die EXE des
    /// Prozesses und neben ihr kein Starter liegt. Beides muss zutreffen; im Zweifel gilt die App als
    /// installiert, und der Updater benennt keine EXE um.
    /// </summary>
    /// <param name="assemblyLocation">Pfad der Programmbibliothek der App (<see cref="Assembly.Location"/>).</param>
    /// <param name="processPath">Pfad der EXE des Prozesses (<see cref="Environment.ProcessPath"/>).</param>
    internal static bool IsSingleFileExe(string? assemblyLocation, string? processPath)
    {
        // Nichts entpackt: Die Bibliothek liegt gar nicht auf der Platte, sondern nur in der EXE.
        if (string.IsNullOrEmpty(assemblyLocation)) return true;
        if (string.IsNullOrEmpty(processPath)) return false;

        var libraryDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyLocation));
        var processDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
        return !string.Equals(libraryDirectory, processDirectory, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(Path.ChangeExtension(assemblyLocation, ".exe"));
    }

    private static string CurrentRid => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    private readonly HttpClient _http;
    private readonly Version _current;
    private readonly bool _portable;
    private readonly string _rid;
    private readonly string? _downloadDirectory;

    public UpdateService() : this(SharedHttp, CurrentVersion, IsPortable, CurrentRid) { }

    /// <summary>Für Tests: HTTP, laufende Version, Variante und Architektur vorgeben.</summary>
    internal UpdateService(HttpClient http, Version current, bool portable, string rid, string? downloadDirectory = null)
    {
        _http = http;
        _current = current;
        _portable = portable;
        _rid = rid;
        _downloadDirectory = downloadDirectory;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            return await CheckCoreAsync(ct);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Die Einstellungsseite zeigt die Meldung hinter „Update-Prüfung fehlgeschlagen:“.
            var reason = FailureReason(ex);
            throw new InvalidOperationException(char.ToUpperInvariant(reason[0]) + reason[1..] + ".", ex);
        }
    }

    private async Task<UpdateInfo?> CheckCoreAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(
            $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // noch kein Release
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub antwortet mit {(int)resp.StatusCode}.");

        var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(ct);
        if (release is null || release.Draft || release.Prerelease) return null;
        if (!TryParseVersion(release.TagName, out var version) || version <= _current) return null;

        // Nur eine Datei, die genau so heißt, wie build/publish.ps1 sie für diese Variante und Architektur nennt.
        // Alles andere im Release wird nicht angeboten, auch wenn es auf dieselbe Endung hört.
        var asset = release.Assets.FirstOrDefault(a => IsExpectedAssetName(a.Name, _portable, _rid));

        return new UpdateInfo(version, release.Name is { Length: > 0 } n ? n : release.TagName, ReleasePage(release.HtmlUrl),
            asset is null ? null : new UpdateAsset(asset.Name, asset.BrowserDownloadUrl, asset.Size, ParseDigest(asset.Digest)));
    }

    /// <summary>
    /// Die Seite des Release für den Link „Was ist neu?“. Die Adresse stammt aus der Antwort von GitHub und
    /// wird beim Klick an Windows zum Öffnen gegeben. Ist sie keine Web-Adresse, steht dafür die Übersicht der
    /// Releases (siehe <see cref="WebLinks"/>).
    /// </summary>
    internal static string ReleasePage(string? htmlUrl)
        => Uri.TryCreate(htmlUrl, UriKind.Absolute, out var url) && WebLinks.IsWebAddress(url)
            ? url.AbsoluteUri
            : $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";

    /// <summary>
    /// Lädt das Update herunter und prüft die Prüfsumme. Gibt den lokalen Pfad zurück.
    /// Ohne Prüfsumme wird gar nicht erst geladen: Was sich nicht prüfen lässt, wird auch nicht gestartet.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress, CancellationToken ct = default)
    {
        try
        {
            return await DownloadCoreAsync(asset, progress, ct);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Netz, Platte, Rechte. Die eigenen Meldungen sind InvalidOperationException und gehen unverändert durch.
            throw Failure("Das Update ließ sich nicht herunterladen", ex);
        }
    }

    private async Task<string> DownloadCoreAsync(UpdateAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        if (asset.Sha256 is not { } expected || !IsSha256(expected))
            throw new InvalidOperationException(
                "Für diese Version liefert GitHub keine Prüfsumme. Ohne Prüfung installiert Lagerfeuer nichts – " +
                "du findest die Datei auf der Release-Seite.");

        // Portabel: neben die laufende EXE (gleiches Laufwerk → atomares Umbenennen möglich).
        // Der Dateiname stammt aus der Antwort der GitHub-API und geht in den Zielpfad ein. Er muss ein reiner
        // Dateiname nach dem Muster der Release-Dateien sein, sonst wird nichts geladen und nichts geschrieben.
        if (!IsExpectedAssetName(asset.Name, _portable, _rid))
            throw new InvalidOperationException("Der Dateiname des Updates ist unerwartet – es wird nicht geladen.");
        var name = Path.GetFileName(asset.Name);
        var target = _downloadDirectory is not null ? Path.Combine(_downloadDirectory, name)
            : _portable ? Environment.ProcessPath + ".new"
            : Path.Combine(Path.GetTempPath(), "Lagerfeuer-Update", name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using var resp = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? asset.Size;

        using var sha = SHA256.Create();
        await using (var net = await resp.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await net.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                sha.TransformBlock(buffer, 0, n, null, 0);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
            sha.TransformFinalBlock([], 0, 0);
        }

        if (!Convert.ToHexString(sha.Hash!).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException("Die Prüfsumme des Updates stimmt nicht – Download verworfen.");
        }

        // Die Prüfsumme sagt nur, dass die Datei so ankam, wie sie im Release liegt. Ob sie ein Programm ist,
        // zeigt erst ihr Kopf. Ohne diese Prüfung bekäme Windows die Datei zum Start, und für etwas, das kein
        // Programm ist, zeigt es einen eigenen Dialog, hinter dem die App wartet.
        if (!IsWindowsProgram(target, _portable, _rid))
        {
            File.Delete(target);
            throw new InvalidOperationException(
                "Die heruntergeladene Datei ist kein Windows-Programm für diesen Rechner – Download verworfen.");
        }
        return target;
    }

    private const ushort MachineX86 = 0x014C, MachineX64 = 0x8664, MachineArm64 = 0xAA64;

    /// <summary>
    /// Ist die Datei ein Windows-Programm, das hier laufen kann? Geprüft wird nur der Kopf: „MZ“, der Verweis
    /// auf den PE-Kopf, „PE\0\0“, die Prozessorart und dass es ein ausführbares Abbild und keine DLL ist.
    ///
    /// Die portable EXE muss zur Architektur der laufenden App passen (win-x64 oder win-arm64), denn sie tritt
    /// an deren Stelle. Das Setup baut NSIS als 32-Bit-Programm, gleich für welche Architektur es installiert;
    /// dort sind deshalb x86, x64 und ARM64 erlaubt.
    /// </summary>
    /// <param name="path">Pfad der heruntergeladenen Datei.</param>
    /// <param name="portable">Portable EXE (true) oder Setup (false).</param>
    /// <param name="rid">Architektur der laufenden App: „win-x64“ oder „win-arm64“.</param>
    internal static bool IsWindowsProgram(string path, bool portable, string rid)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[64];
            if (file.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length) return false;
            if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;

            // e_lfanew: Stelle des PE-Kopfs. Er liegt hinter dem DOS-Kopf und muss samt Dateikopf in die Datei passen.
            var peOffset = BitConverter.ToInt32(dos[0x3C..0x40]);
            const int peHeaderSize = 4 + 20; // Signatur + IMAGE_FILE_HEADER
            if (peOffset < dos.Length || peOffset > file.Length - peHeaderSize) return false;

            Span<byte> pe = stackalloc byte[peHeaderSize];
            file.Position = peOffset;
            if (file.ReadAtLeast(pe, pe.Length, throwOnEndOfStream: false) < pe.Length) return false;
            if (pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0) return false;

            var machine = BitConverter.ToUInt16(pe[4..6]);
            var characteristics = BitConverter.ToUInt16(pe[22..24]);
            const ushort executableImage = 0x0002, dll = 0x2000;
            if ((characteristics & executableImage) == 0 || (characteristics & dll) != 0) return false;

            return portable
                ? machine == (rid == "win-arm64" ? MachineArm64 : MachineX64)
                : machine is MachineX86 or MachineX64 or MachineArm64;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht lesbar heißt hier: nicht geprüft, also nicht starten.
            return false;
        }
    }

    /// <summary>Startet die Installation des heruntergeladenen Updates. Danach muss sich die App beenden.</summary>
    public void LaunchInstaller(string downloadedFile) => LaunchInstaller(downloadedFile, Process.Start);

    /// <param name="downloadedFile">Pfad der geprüften neuen Fassung.</param>
    /// <param name="start">Für Tests: ersetzt <see cref="Process.Start(ProcessStartInfo)"/>.</param>
    internal void LaunchInstaller(string downloadedFile, Func<ProcessStartInfo, Process?> start)
    {
        // Wie CheckAsync und DownloadAsync nach der Variante dieser Instanz, nicht nach der statischen Eigenschaft.
        if (_portable)
        {
            var exe = Environment.ProcessPath!;
            ReplaceExecutable(exe, downloadedFile);
            try
            {
                start(new ProcessStartInfo(exe, $"{AfterUpdateArg} {Environment.ProcessId}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // Die neue EXE liegt schon am Platz, startet aber nicht: zurück zur bisherigen.
                // Scheitert auch das, meldet RestoreExecutable selbst, wo die bisherige liegt.
                RestoreExecutable(exe);
                throw Failure("Die neue Version ließ sich nicht starten", ex);
            }
        }
        else
        {
            // Das Setup wartet, bis sich die App beendet hat, und startet sie danach neu.
            try
            {
                start(new ProcessStartInfo(downloadedFile, "/S /RESTART") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw Failure("Das Setup ließ sich nicht starten", ex);
            }
        }
        Log.Info($"Update gestartet: {downloadedFile}");
    }

    /// <summary>
    /// Tauscht die portable EXE gegen die heruntergeladene Fassung. Die bisherige wird zu „…exe.old“
    /// (eine laufende EXE darf umbenannt, aber nicht überschrieben werden), die neue rückt an ihren Platz.
    /// Scheitert der zweite Schritt, wird der erste rückgängig gemacht: Am ursprünglichen Pfad liegt danach
    /// wieder die bisherige EXE – oder die Meldung sagt, wo sie liegt.
    /// </summary>
    /// <param name="exe">Pfad der laufenden EXE.</param>
    /// <param name="downloadedFile">Pfad der geprüften neuen Fassung.</param>
    /// <param name="move">Für Tests: ersetzt <see cref="File.Move(string, string, bool)"/>.</param>
    internal static void ReplaceExecutable(string exe, string downloadedFile, Action<string, string, bool>? move = null)
    {
        move ??= File.Move;
        var old = exe + ".old";

        // Prüfen, solange noch nichts angefasst ist (z.B. Download inzwischen vom Virenscanner entfernt).
        if (!File.Exists(downloadedFile))
            throw new InvalidOperationException("Die heruntergeladene Datei ist nicht mehr da – das Update wurde nicht eingespielt.");

        try
        {
            File.Delete(old); // Rest vom vorigen Update
            move(exe, old, false);
        }
        catch (Exception ex)
        {
            throw Failure("Die laufende EXE ließ sich nicht umbenennen", ex);
        }

        try
        {
            move(downloadedFile, exe, false);
        }
        catch (Exception ex)
        {
            try
            {
                move(old, exe, true);
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException(StrandedMessage(exe, old), new AggregateException(ex, rollback));
            }
            throw Failure("Das Update ließ sich nicht einspielen", ex);
        }
    }

    /// <summary>
    /// Macht <see cref="ReplaceExecutable"/> rückgängig: Die bisherige EXE („…exe.old“) kommt zurück an ihren
    /// Platz, die neue wird dabei verworfen.
    /// </summary>
    internal static void RestoreExecutable(string exe, Action<string, string, bool>? move = null)
    {
        move ??= File.Move;
        var old = exe + ".old";
        try
        {
            move(old, exe, true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(StrandedMessage(exe, old), ex);
        }
    }

    private static string StrandedMessage(string exe, string old)
        => "Das Update ist gescheitert, und die bisherige Version ließ sich nicht an ihren Platz zurücklegen. " +
           $"Sie liegt unter „{old}“ – benenne sie von Hand wieder in „{Path.GetFileName(exe)}“ um.";

    /// <summary>
    /// Meldung für die Update-Leiste, immer gleich aufgebaut: was nicht ging, dass Lagerfeuer bei der bisherigen
    /// Version bleibt, und ein kurzer Grund. Die ursprüngliche Ausnahme hängt an und landet so im Log. Ihr Text
    /// ist englisch und nennt vollständige Pfade samt Kontonamen; in die Leiste gehört er nicht.
    /// </summary>
    private static InvalidOperationException Failure(string what, Exception cause)
        => new($"{what} – Lagerfeuer bleibt bei der bisherigen Version. Grund: {FailureReason(cause)}.", cause);

    /// <summary>Kurzer Grund auf Deutsch, ohne Pfade. Was hier nicht vorkommt, verweist auf das Log.</summary>
    internal static string FailureReason(Exception ex) => ex switch
    {
        System.ComponentModel.Win32Exception w => WindowsReason(w.NativeErrorCode) ?? $"Windows-Fehler {w.NativeErrorCode}",
        UnauthorizedAccessException => "Zugriff verweigert",
        FileNotFoundException or DirectoryNotFoundException => "Datei nicht gefunden",
        HttpIOException or IOException { InnerException: System.Net.Sockets.SocketException }
            => "Verbindung zu GitHub abgebrochen",
        IOException io => WindowsReason(Win32Code(io.HResult)) ?? "Fehler beim Zugriff auf die Datei",
        HttpRequestException { StatusCode: { } status } => $"GitHub antwortet mit {(int)status}",
        HttpRequestException => "keine Verbindung zu GitHub",
        OperationCanceledException or TimeoutException => "Zeitüberschreitung",
        _ => "unbekannt, Einzelheiten stehen im Log",
    };

    /// <summary>Die Windows-Fehler, die beim Laden, Tauschen und Starten vorkommen. Null: kein eigener Text.</summary>
    private static string? WindowsReason(int code) => code switch
    {
        2 or 3 => "Datei nicht gefunden",                              // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
        5 => "Zugriff verweigert",                                     // ERROR_ACCESS_DENIED
        32 or 33 => "Datei wird von einem anderen Programm verwendet", // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        39 or 112 => "Datenträger voll",                               // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
        193 or 216 => "Datei ist kein lauffähiges Programm",           // ERROR_BAD_EXE_FORMAT, ERROR_EXE_MACHINE_TYPE_MISMATCH
        225 or 226 => "vom Virenschutz blockiert",                     // ERROR_VIRUS_INFECTED, ERROR_VIRUS_DELETED
        1223 => "abgebrochen",                                         // ERROR_CANCELLED
        1260 => "von einer Richtlinie des Systems blockiert",          // ERROR_ACCESS_DISABLED_BY_POLICY
        _ => null,
    };

    /// <summary>Der Windows-Fehlercode aus einem HRESULT der Form 0x8007xxxx, sonst -1.</summary>
    private static int Win32Code(int hresult)
        => (hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hresult & 0xFFFF : -1;

    /// <summary>Räumt Reste eines vorherigen Updates auf (beim Start aufrufen).</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                File.Delete(exe + ".old");
                File.Delete(exe + ".new");
            }
            var temp = Path.Combine(Path.GetTempPath(), "Lagerfeuer-Update");
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
        catch
        {
            // z.B. alte EXE noch kurz gesperrt – beim nächsten Start erneut
        }

        // Nur die portable EXE entpackt sich. Im Hintergrund, damit der Start nicht darauf wartet.
        if (IsPortable)
            _ = Task.Run(() => RemoveStaleExtractions(AppAssembly.Location, Environment.ProcessPath, Path.GetTempPath()));
    }

    /// <summary>Endung eines Entpackordners, der geprüft, umbenannt und zum Löschen vorgesehen ist.</summary>
    private const string RemovalSuffix = ".entfernen";

    /// <summary>
    /// Löscht die Entpackordner früherer Fassungen derselben portablen EXE. Sie entpackt sich nach
    /// %TEMP%\.net\&lt;Name der EXE&gt;\&lt;Kennung&gt;, jede Fassung in einen eigenen Ordner von rund 150 MB.
    /// Nach einem Update bliebe der Ordner der vorigen Fassung sonst für immer liegen.
    ///
    /// Die App löscht hier Ordner, deshalb gilt: Gelöscht wird nur, was alle Bedingungen erfüllt. Im Zweifel
    /// bleibt es liegen.
    /// <list type="bullet">
    /// <item>Der eigene Entpackordner liegt dort, wo .NET ihn anlegt: %TEMP%\.net\&lt;Name der laufenden EXE&gt;\&lt;Kennung&gt;.
    /// Ein anderer Ort, auch ein über DOTNET_BUNDLE_EXTRACT_BASE_DIR verlegter, wird nicht angefasst. Die
    /// installierte Fassung erfüllt das nie.</item>
    /// <item>Der Kandidat ist ein direkter Nachbar des eigenen Ordners, heißt wie eine Kennung und enthält die
    /// Programmbibliothek, aber keinen Starter. Der eigene Ordner ist nie Kandidat.</item>
    /// <item>Weder der Kandidat noch irgendetwas darin ist eine Verknüpfung (Junction, Symlink). Verknüpfungen
    /// werden nicht verfolgt und nicht gelöscht.</item>
    /// <item>Der Kandidat lässt sich umbenennen. Solange eine andere Instanz daraus läuft, geht das nicht; dann
    /// bleibt er unberührt und ohne Meldung liegen. Gelöscht wird erst der umbenannte Ordner, nie ein benutzter.</item>
    /// </list>
    /// Entpackordner enthalten nur Programmdateien. Fehlt einer, entpackt sich die zugehörige EXE beim nächsten
    /// Start neu.
    /// </summary>
    /// <param name="ownLibrary">Pfad der geladenen Programmbibliothek (<see cref="Assembly.Location"/>).</param>
    /// <param name="processPath">Pfad der EXE des Prozesses (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="tempPath">Temp-Ordner des Kontos (<see cref="Path.GetTempPath"/>).</param>
    /// <param name="moveDirectory">Für Tests: ersetzt <see cref="Directory.Move(string, string)"/>.</param>
    /// <returns>Zahl der gelöschten Ordner.</returns>
    internal static int RemoveStaleExtractions(string? ownLibrary, string? processPath, string tempPath,
        Action<string, string>? moveDirectory = null)
    {
        var removed = 0;
        try
        {
            if (string.IsNullOrEmpty(ownLibrary) || string.IsNullOrEmpty(processPath)) return 0;
            moveDirectory ??= Directory.Move;

            var library = Path.GetFileName(ownLibrary);
            var own = new FileInfo(ownLibrary).Directory;  // %TEMP%\.net\<Name der EXE>\<Kennung>
            var app = own?.Parent;                         // %TEMP%\.net\<Name der EXE>
            var root = app?.Parent;                        // %TEMP%\.net
            if (own is null || app is null || root is null) return 0;

            // Liegt alles da, wo .NET die laufende EXE entpackt? Sonst verstehen wir die Lage nicht und löschen nichts.
            if (!SamePath(root.FullName, Path.Combine(tempPath, ".net"))) return 0;
            if (!string.Equals(app.Name, Path.GetFileNameWithoutExtension(processPath), StringComparison.OrdinalIgnoreCase)) return 0;
            if (!IsBundleId(own.Name) || !HasLibraryWithoutStarter(own, library)) return 0;
            if (IsLink(root) || IsLink(app) || IsLink(own)) return 0;

            foreach (var candidate in app.EnumerateDirectories())
            {
                try
                {
                    if (SamePath(candidate.FullName, own.FullName) || IsLink(candidate)) continue;

                    string doomed;
                    if (candidate.Name.EndsWith(RemovalSuffix, StringComparison.Ordinal)
                        && IsBundleId(candidate.Name[..^RemovalSuffix.Length]))
                    {
                        // Rest eines früheren Laufs: schon geprüft und umbenannt, das Löschen kam nicht zu Ende.
                        doomed = candidate.FullName;
                    }
                    else
                    {
                        if (!IsBundleId(candidate.Name) || !HasLibraryWithoutStarter(candidate, library)) continue;
                        if (ContainsLink(candidate)) continue;
                        doomed = candidate.FullName + RemovalSuffix;
                        if (Directory.Exists(doomed) || File.Exists(doomed)) continue;
                        // Scheitert, solange eine Datei darin geöffnet ist: Dann läuft noch eine Instanz daraus.
                        moveDirectory(candidate.FullName, doomed);
                    }

                    if (ContainsLink(new DirectoryInfo(doomed))) continue;
                    Directory.Delete(doomed, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In Benutzung oder gesperrt: bleibt liegen, beim nächsten Start erneut.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Entpackordner früherer Fassungen ließen sich nicht aufräumen", ex);
        }

        if (removed > 0) Log.Info($"Entpackordner früherer Fassungen entfernt: {removed}");
        return removed;
    }

    /// <summary>So heißen die Entpackordner: zwölf Zeichen aus Buchstaben, Ziffern, „-“ und „_“.</summary>
    private static bool IsBundleId(string name)
        => name.Length == 12 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Wie in <see cref="IsSingleFileExe"/>: Im Entpackordner liegt die Bibliothek ohne ihren Starter.</summary>
    private static bool HasLibraryWithoutStarter(DirectoryInfo directory, string library)
        => File.Exists(Path.Combine(directory.FullName, library))
           && !File.Exists(Path.Combine(directory.FullName, Path.ChangeExtension(library, ".exe")));

    private static bool IsLink(FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Liegt irgendwo im Ordner eine Verknüpfung? Geht Ebene für Ebene vor und folgt selbst keiner.</summary>
    private static bool ContainsLink(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (IsLink(entry)) return true;
            if (entry is DirectoryInfo sub && ContainsLink(sub)) return true;
        }
        return false;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) return false; // Vorabversion (z.B. 1.2.0-beta) nie automatisch anbieten
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return false;
        version = Normalize(v);
        return true;
    }

    /// <summary>
    /// Heißt die Datei so, wie build/publish.ps1 die Release-Dateien dieser Variante und Architektur nennt:
    /// „Lagerfeuer-&lt;Version&gt;-win-x64-setup.exe“ für die installierte, „Lagerfeuer-&lt;Version&gt;-win-x64.exe“
    /// für die portable Fassung? Als Version zählen nur Ziffern und Punkte. Damit scheiden Pfadtrenner,
    /// Laufwerksangaben und angehängte Endungen von selbst aus.
    /// </summary>
    internal static bool IsExpectedAssetName(string? name, bool portable, string rid)
        => name is not null && Regex.IsMatch(name,
            $@"\ALagerfeuer-[0-9]+(\.[0-9]+){{1,3}}-{Regex.Escape(rid)}{(portable ? "" : "-setup")}\.exe\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// GitHub liefert die Prüfsumme als „sha256:&lt;64 Hex-Zeichen&gt;“. Alles andere (fehlend, anderes Verfahren,
    /// verstümmelt) gilt als „keine Prüfsumme“ – und dann wird nichts installiert, siehe <see cref="DownloadAsync"/>.
    /// </summary>
    internal static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..];
        return IsSha256(hex) ? hex : null;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lagerfeuer", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}

public sealed record UpdateInfo(Version Version, string Name, string ReleaseUrl, UpdateAsset? Asset);

public sealed record UpdateAsset(string Name, string DownloadUrl, long Size, string? Sha256);
