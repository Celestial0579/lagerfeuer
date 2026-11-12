using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Was die Update-Leiste bei einem Fehlschlag zeigt: einen deutschen Satz, bei jeder Ursache gleich aufgebaut,
/// mit einem kurzen Grund. Der Text der .NET-Laufzeit ist englisch und nennt vollständige Pfade. Er bleibt in der
/// angehängten Ausnahme und kommt so ins Log, nicht in die Leiste.
/// </summary>
public sealed class UpdateFailureMessageTests : IDisposable
{
    private const string Bleibt = " – Lagerfeuer bleibt bei der bisherigen Version. Grund: ";

    private readonly TempDir _dir = new();

    private static readonly byte[] Bytes = TestProgram.Bytes(TestProgram.X86, "neues Setup");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Bytes));

    private string Exe => Path.Combine(_dir.Path, "Lagerfeuer.exe");
    private string New => Exe + ".new";

    private UpdateService Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new FakeHandler(respond)), new Version(0, 1, 0), false, "win-x64", _dir.Path);

    private static UpdateAsset Asset(string name = "Lagerfeuer-0.2.0-win-x64-setup.exe")
        => new(name, "https://example.test/" + name, Bytes.Length, Digest);

    private static IOException WindowsIo(int code) => new("The process cannot access the file 'C:\\Users\\jemand\\x.exe'.", unchecked((int)0x80070000) | code);

    public void Dispose() => _dir.Dispose();

    // ---------- Der Grund ----------

    [Theory]
    [InlineData(2, "Datei nicht gefunden")]
    [InlineData(3, "Datei nicht gefunden")]
    [InlineData(5, "Zugriff verweigert")]
    [InlineData(32, "Datei wird von einem anderen Programm verwendet")]
    [InlineData(193, "Datei ist kein lauffähiges Programm")]
    [InlineData(216, "Datei ist kein lauffähiges Programm")]
    [InlineData(225, "vom Virenschutz blockiert")]
    [InlineData(1223, "abgebrochen")]
    [InlineData(1260, "von einer Richtlinie des Systems blockiert")]
    [InlineData(1392, "Windows-Fehler 1392")] // kein eigener Text: wenigstens die Nummer, nie der Text der Laufzeit
    public void WindowsFehlerBeimStart_BekommenEinenKurzenGrund(int code, string expected)
        => Assert.Equal(expected, UpdateService.FailureReason(new Win32Exception(code, "An error occurred trying to start process 'C:\\Users\\jemand\\setup.exe'.")));

    [Theory]
    [InlineData(32, "Datei wird von einem anderen Programm verwendet")]
    [InlineData(33, "Datei wird von einem anderen Programm verwendet")]
    [InlineData(39, "Datenträger voll")]
    [InlineData(112, "Datenträger voll")]
    [InlineData(1117, "Fehler beim Zugriff auf die Datei")]
    public void DateifehlerVonWindows_BekommenEinenKurzenGrund(int code, string expected)
        => Assert.Equal(expected, UpdateService.FailureReason(WindowsIo(code)));

    [Fact]
    public void Dateifehler_OhneWindowsCode()
    {
        Assert.Equal("Zugriff verweigert", UpdateService.FailureReason(new UnauthorizedAccessException("Access to the path 'C:\\x' is denied.")));
        Assert.Equal("Datei nicht gefunden", UpdateService.FailureReason(new FileNotFoundException("Could not find file 'C:\\x'.")));
        Assert.Equal("Datei nicht gefunden", UpdateService.FailureReason(new DirectoryNotFoundException("Could not find a part of the path 'C:\\x'.")));
        Assert.Equal("Fehler beim Zugriff auf die Datei", UpdateService.FailureReason(new IOException("I/O error")));
    }

    [Fact]
    public void Netzfehler()
    {
        Assert.Equal("GitHub antwortet mit 404",
            UpdateService.FailureReason(new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound)));
        Assert.Equal("keine Verbindung zu GitHub",
            UpdateService.FailureReason(new HttpRequestException("No such host is known. (github.com:443)")));
        Assert.Equal("Verbindung zu GitHub abgebrochen",
            UpdateService.FailureReason(new IOException("Unable to read data from the transport connection.", new SocketException(10054))));
        Assert.Equal("Verbindung zu GitHub abgebrochen",
            UpdateService.FailureReason(new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")));
        Assert.Equal("Zeitüberschreitung",
            UpdateService.FailureReason(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 600 seconds elapsing.")));
    }

    [Fact]
    public void Unbekanntes_VerweistAufDasLog()
        => Assert.Equal("unbekannt, Einzelheiten stehen im Log", UpdateService.FailureReason(new NotSupportedException("Specified method is not supported.")));

    // ---------- Tausch der portablen EXE ----------

    [Fact]
    public void Tausch_ZweiterSchrittScheitert_MeldungOhneLaufzeitTextUndPfad()
    {
        File.WriteAllText(Exe, "alt");
        File.WriteAllText(New, "neu");
        var n = 0;
        void Move(string von, string nach, bool ueberschreiben)
        {
            if (++n == 2) throw new UnauthorizedAccessException($"Access to the path '{von}' is denied.");
            File.Move(von, nach, ueberschreiben);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.ReplaceExecutable(Exe, New, Move));

        Assert.Equal("Das Update ließ sich nicht einspielen" + Bleibt + "Zugriff verweigert.", ex.Message);
        Assert.DoesNotContain(_dir.Path, ex.Message);
        // Für das Log bleibt alles erhalten.
        var cause = Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
        Assert.Contains(New, cause.Message);
    }

    [Fact]
    public void Tausch_ErsterSchrittScheitert_MeldungOhneLaufzeitTextUndPfad()
    {
        File.WriteAllText(Exe, "alt");
        File.WriteAllText(New, "neu");

        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.ReplaceExecutable(Exe, New, (_, _, _) => throw WindowsIo(32)));

        Assert.Equal("Die laufende EXE ließ sich nicht umbenennen" + Bleibt + "Datei wird von einem anderen Programm verwendet.", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    // ---------- Start des Setups (installierte Fassung) ----------

    [Fact]
    public void SetupStartetNicht_MeldungAufgebautWieBeiDerPortablenFassung()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var setup = Path.Combine(_dir.Path, "Lagerfeuer-0.2.0-win-x64-setup.exe");
        var cause = new Win32Exception(5, $"An error occurred trying to start process '{setup}' with working directory 'C:\\WINDOWS\\system32'. Zugriff verweigert");

        var ex = Assert.Throws<InvalidOperationException>(() => service.LaunchInstaller(setup, _ => throw cause));

        Assert.Equal("Das Setup ließ sich nicht starten" + Bleibt + "Zugriff verweigert.", ex.Message);
        Assert.DoesNotContain(_dir.Path, ex.Message);
        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public void SetupStart_BekommtDateiUndSchalter()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var setup = Path.Combine(_dir.Path, "Lagerfeuer-0.2.0-win-x64-setup.exe");
        ProcessStartInfo? seen = null;

        service.LaunchInstaller(setup, info => { seen = info; return null; });

        Assert.NotNull(seen);
        Assert.Equal(setup, seen.FileName);
        Assert.Equal("/S /RESTART", seen.Arguments);
        Assert.True(seen.UseShellExecute);
    }

    // ---------- Suche nach Updates (Einstellungsseite: „Update-Prüfung fehlgeschlagen: …“) ----------

    [Fact]
    public async Task Suche_KeineVerbindung_NenntDenGrundOhneLaufzeitText()
    {
        var service = Create(_ => throw new HttpRequestException("No such host is known. (api.github.com:443)"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Keine Verbindung zu GitHub.", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Suche_AntwortIstKeinJson_VerweistAufDasLog()
    {
        var service = Create(_ => FakeHandler.Json("<html>Wartung</html>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Unbekannt, Einzelheiten stehen im Log.", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Suche_EigeneMeldungBleibtWieSieIst()
    {
        var service = Create(_ => FakeHandler.Status(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal("GitHub antwortet mit 403.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Suche_Abgebrochen_BleibtEinAbbruch()
    {
        var service = Create(_ => FakeHandler.Json("{}"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cts.Token));
    }

    // ---------- Download ----------

    [Fact]
    public async Task Download_GitHubAntwortetMitFehler()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(Asset(), null, TestContext.Current.CancellationToken));

        Assert.Equal("Das Update ließ sich nicht herunterladen" + Bleibt + "GitHub antwortet mit 404.", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Download_KeineVerbindung()
    {
        var service = Create(_ => throw new HttpRequestException("No such host is known. (github.com:443)"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(Asset(), null, TestContext.Current.CancellationToken));

        Assert.Equal("Das Update ließ sich nicht herunterladen" + Bleibt + "keine Verbindung zu GitHub.", ex.Message);
    }

    [Fact]
    public async Task Download_ZielLaesstSichNichtSchreiben_MeldungOhnePfad()
    {
        // Am Ziel liegt ein Ordner: Die Datei lässt sich nicht anlegen, und die Laufzeit nennt in ihrer Meldung den Pfad.
        var asset = Asset();
        Directory.CreateDirectory(Path.Combine(_dir.Path, asset.Name));
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.StartsWith("Das Update ließ sich nicht herunterladen" + Bleibt, ex.Message);
        Assert.DoesNotContain(_dir.Path, ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Download_Abgebrochen_BleibtEinAbbruch()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(Asset(), null, cts.Token));
    }

    [Fact]
    public async Task Download_EigeneMeldungenBleibenWieSieSind()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("manipuliert"u8.ToArray()) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(Asset(), null, TestContext.Current.CancellationToken));

        Assert.Equal("Die Prüfsumme des Updates stimmt nicht – Download verworfen.", ex.Message);
        Assert.Null(ex.InnerException);
    }
}
