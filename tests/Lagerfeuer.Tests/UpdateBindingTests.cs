using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// #60: Die Datei eines Updates gehört zur Version des Release, und gestartet wird nur, was geprüft wurde –
/// mit genau dem geprüften Inhalt.
/// </summary>
public sealed class UpdateBindingTests : IDisposable
{
    private readonly TempDir _dir = new();

    private static readonly byte[] Bytes = TestProgram.Bytes(TestProgram.X86, "neues Setup");
    private static readonly string Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private static readonly CancellationToken Ct = CancellationToken.None;

    public void Dispose() => _dir.Dispose();

    private static string ReleaseJson(string tag, params string[] assetNames)
        => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = "Lagerfeuer " + tag,
            html_url = $"https://github.com/Celestial0579/lagerfeuer/releases/tag/{tag}",
            draft = false,
            prerelease = false,
            assets = assetNames.Select(name => new
            {
                name,
                browser_download_url = $"https://github.com/Celestial0579/lagerfeuer/releases/download/{tag}/{name}",
                size = Bytes.Length,
                digest = "sha256:" + Sha256,
            }),
        });

    private UpdateService Create(Func<HttpRequestMessage, HttpResponseMessage> respond, bool portable = false, string? dir = "")
        => new(new HttpClient(new FakeHandler(respond)), new Version(0, 2, 0), portable, "win-x64", dir == "" ? _dir.Path : dir);

    private static HttpResponseMessage Datei() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };

    private static readonly byte[] PortableBytes = TestProgram.Bytes(TestProgram.X64, "neue EXE");

    private static UpdateAsset Asset(string name, byte[]? bytes = null)
        => new(name, "https://github.com/Celestial0579/lagerfeuer/releases/download/v0.3.0/" + name, (bytes ?? Bytes).Length,
            Convert.ToHexString(SHA256.HashData(bytes ?? Bytes)));

    // ---------- Version im Dateinamen ----------

    [Theory]
    [InlineData("Lagerfeuer-0.3.0-win-x64-setup.exe", "0.3.0", true)]
    [InlineData("Lagerfeuer-0.3-win-x64-setup.exe", "0.3.0", true)]
    [InlineData("Lagerfeuer-0.3.0.0-win-x64-setup.exe", "0.3.0", true)]
    [InlineData("Lagerfeuer-0.2.1-win-x64-setup.exe", "0.3.0", false)]
    [InlineData("Lagerfeuer-0.3.1-win-x64-setup.exe", "0.3.0", false)]
    [InlineData("Lagerfeuer-1.3.0-win-x64-setup.exe", "0.3.0", false)]
    public void VersionImNamen_MussZurReleaseVersionPassen(string name, string version, bool expected)
        => Assert.Equal(expected, UpdateService.IsExpectedAssetName(name, false, "win-x64", Version.Parse(version)));

    [Fact]
    public async Task AlteDateiImNeuenRelease_WirdNichtAngeboten()
    {
        var service = Create(_ => FakeHandler.Json(ReleaseJson("v0.3.0", "Lagerfeuer-0.2.1-win-x64-setup.exe")));

        var update = await service.CheckAsync(Ct);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update.Version);
        Assert.Null(update.Asset); // nur der Weg über die Release-Seite
    }

    [Fact]
    public async Task PassendeDateiNebenAlter_WirdAngeboten()
    {
        var service = Create(_ => FakeHandler.Json(ReleaseJson("v0.3.0",
            "Lagerfeuer-0.2.1-win-x64-setup.exe", "Lagerfeuer-0.3.0-win-x64-setup.exe")));

        var update = await service.CheckAsync(Ct);

        Assert.Equal("Lagerfeuer-0.3.0-win-x64-setup.exe", update?.Asset?.Name);
    }

    [Theory]
    [InlineData("Lagerfeuer-0.3.0-win-x64-setup.exe", "lagerfeuer-0.3.0-win-x64-setup.exe")]
    [InlineData("Lagerfeuer-0.3.0-win-x64-setup.exe", "Lagerfeuer-0.3-win-x64-setup.exe")]
    public async Task MehrerePassendeDateien_KeineWirdAngeboten(string a, string b)
    {
        var service = Create(_ => FakeHandler.Json(ReleaseJson("v0.3.0", a, b)));

        var update = await service.CheckAsync(Ct);

        Assert.NotNull(update);
        Assert.Null(update.Asset);
    }

    // ---------- gestartet wird nur, was geprüft ist ----------

    [Fact]
    public void UngepruefteDatei_WirdNichtGestartet()
    {
        var service = Create(_ => Datei());
        var setup = Path.Combine(_dir.Path, "Lagerfeuer-0.3.0-win-x64-setup.exe");
        File.WriteAllBytes(setup, Bytes);
        var started = false;

        var ex = Assert.Throws<InvalidOperationException>(() => service.LaunchInstaller(setup, _ => { started = true; return null; }));

        Assert.Contains("nicht geprüft", ex.Message);
        Assert.False(started);
    }

    [Fact]
    public async Task AndereDateiAlsDieGepruefte_WirdNichtGestartet()
    {
        var service = Create(_ => Datei());
        await service.DownloadAsync(Asset("Lagerfeuer-0.3.0-win-x64-setup.exe"), null, Ct);
        var andere = Path.Combine(_dir.Path, "andere.exe");
        File.WriteAllBytes(andere, Bytes);
        var started = false;

        Assert.Throws<InvalidOperationException>(() => service.LaunchInstaller(andere, _ => { started = true; return null; }));
        Assert.False(started);
    }

    [Fact]
    public async Task GepruefteDatei_WirdGestartet_UndDanachFreigegeben()
    {
        var service = Create(_ => Datei());
        var file = await service.DownloadAsync(Asset("Lagerfeuer-0.3.0-win-x64-setup.exe"), null, Ct);
        ProcessStartInfo? seen = null;

        service.LaunchInstaller(file, info => { seen = info; return null; });

        Assert.Equal(file, seen?.FileName);
        File.WriteAllBytes(file, [1]); // nach dem Start gesperrt bleibt nichts
    }

    [Fact]
    public async Task Setup_IstBisZumStartGegenSchreibenUndUmbenennenGesperrt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Freigabemodi von Dateien gibt es nur unter Windows.");
        var service = Create(_ => Datei());
        var file = await service.DownloadAsync(Asset("Lagerfeuer-0.3.0-win-x64-setup.exe"), null, Ct);

        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(file, [1]));
        Assert.ThrowsAny<IOException>(() => File.Move(file, file + ".weg"));
        Assert.Equal(Bytes, File.ReadAllBytes(file)); // lesen dürfen andere, etwa der Virenschutz
    }

    [Fact]
    public async Task PortableDatei_NachDerPruefungErsetzt_WirdNichtEingespielt()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PortableBytes) }, portable: true);
        var file = await service.DownloadAsync(Asset("Lagerfeuer-0.3.0-win-x64.exe", PortableBytes), null, Ct);
        // Umbenennen darf bei der portablen Fassung jeder (Lagerfeuer schiebt sie selbst an ihren Platz) …
        File.Move(file, file + ".weg");
        File.WriteAllBytes(file, TestProgram.Bytes(TestProgram.X64, "untergeschoben"));
        var started = false;

        // … aber dann stimmt die Prüfsumme vor dem Tausch nicht mehr. Getauscht wird nichts.
        var ex = Assert.Throws<InvalidOperationException>(() => service.LaunchInstaller(file, _ => { started = true; return null; }));

        Assert.Contains("verändert", ex.Message);
        Assert.False(started);
    }

    [Fact]
    public async Task Setup_LandetInEigenemNeuemOrdner()
    {
        var service = Create(_ => Datei(), dir: null);
        var file = await service.DownloadAsync(Asset("Lagerfeuer-0.3.0-win-x64-setup.exe"), null, Ct);
        var folder = Path.GetDirectoryName(file)!;
        try
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetDirectoryName(folder));
            Assert.StartsWith(UpdateService.UpdateTempPrefix, Path.GetFileName(folder));
            Assert.Equal("Lagerfeuer-0.3.0-win-x64-setup.exe", Path.GetFileName(file));

            var second = Create(_ => Datei(), dir: null);
            var file2 = await second.DownloadAsync(Asset("Lagerfeuer-0.3.0-win-x64-setup.exe"), null, Ct);
            try { Assert.NotEqual(folder, Path.GetDirectoryName(file2)); }
            finally { second.ReleaseHeld(); Directory.Delete(Path.GetDirectoryName(file2)!, true); }
        }
        finally
        {
            service.ReleaseHeld();
            Directory.Delete(folder, true);
        }
    }

    // ---------- Aufräumen ----------

    [Fact]
    public void Aufraeumen_LoeschtNurDieEigenenOrdnerUndFolgtKeinerVerknuepfung()
    {
        var temp = Directory.CreateDirectory(Path.Combine(_dir.Path, "temp")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir.Path, "aussen")).FullName;
        File.WriteAllText(Path.Combine(outside, "wichtig.txt"), "x");
        var alt = Directory.CreateDirectory(Path.Combine(temp, UpdateService.UpdateTempPrefix + "abc")).FullName;
        File.WriteAllBytes(Path.Combine(alt, "Lagerfeuer-0.2.0-win-x64-setup.exe"), Bytes);
        var fremd = Directory.CreateDirectory(Path.Combine(temp, "Anderes-Update-abc")).FullName;
        var link = Path.Combine(temp, UpdateService.UpdateTempPrefix + "link");
        var mitLink = Directory.CreateDirectory(Path.Combine(temp, UpdateService.UpdateTempPrefix + "mitlink")).FullName;
        var linksMade = true;
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            Directory.CreateSymbolicLink(Path.Combine(mitLink, "innen"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            linksMade = false; // Windows ohne Entwicklermodus: Verknüpfungen lassen sich nicht anlegen
        }

        UpdateService.RemoveUpdateFolders(temp);

        Assert.False(Directory.Exists(alt));
        Assert.True(Directory.Exists(fremd));
        Assert.True(File.Exists(Path.Combine(outside, "wichtig.txt")));
        if (linksMade)
        {
            Assert.True(Directory.Exists(link));
            Assert.True(Directory.Exists(mitLink));
        }
    }
}
