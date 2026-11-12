using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Der Dateiname eines Release-Assets stammt aus der Antwort der GitHub-API und geht in den Zielpfad des
/// Downloads ein. Angenommen wird nur, was build/publish.ps1 für diese Variante und Architektur erzeugt.
/// </summary>
public class UpdateAssetNameTests : IDisposable
{
    private const string Setup = "Lagerfeuer-0.2.0-win-x64-setup.exe";

    private readonly TempDir _dir = new();

    private static readonly byte[] Bytes = TestProgram.Bytes(TestProgram.X86, "neues Setup");
    private static readonly string Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    private static string ReleaseJson(params string[] assetNames)
        => JsonSerializer.Serialize(new
        {
            tag_name = "v0.2.0",
            name = "Lagerfeuer v0.2.0",
            html_url = "https://github.com/Celestial0579/lagerfeuer/releases/tag/v0.2.0",
            draft = false,
            prerelease = false,
            assets = assetNames.Select(name => new
            {
                name,
                browser_download_url = "https://example.test/download",
                size = Bytes.Length,
                digest = "sha256:" + Sha256,
            }),
        });

    private (UpdateService Service, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string? downloadDirectory = null)
    {
        var handler = new FakeHandler(respond);
        return (new UpdateService(new HttpClient(handler), new Version(0, 1, 0), false, "win-x64", downloadDirectory ?? _dir.Path), handler);
    }

    private static HttpResponseMessage Datei() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };

    // ---------- das Muster ----------

    [Theory]
    [InlineData("Lagerfeuer-0.2.0-win-x64-setup.exe", false, "win-x64")]
    [InlineData("Lagerfeuer-0.2.0-win-arm64-setup.exe", false, "win-arm64")]
    [InlineData("Lagerfeuer-0.2.0-win-x64.exe", true, "win-x64")]
    [InlineData("Lagerfeuer-0.2.0-win-arm64.exe", true, "win-arm64")]
    [InlineData("Lagerfeuer-12.34.56-win-x64-setup.exe", false, "win-x64")]
    [InlineData("Lagerfeuer-1.2-win-x64.exe", true, "win-x64")]
    [InlineData("Lagerfeuer-1.2.3.4-win-x64.exe", true, "win-x64")]
    [InlineData("lagerfeuer-0.2.0-WIN-X64-SETUP.EXE", false, "win-x64")]
    public void NamenNachDemMusterDerReleaseDateien_WerdenAngenommen(string name, bool portable, string rid)
        => Assert.True(UpdateService.IsExpectedAssetName(name, portable, rid));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("setup.exe")]
    [InlineData("Lagerfeuer-0.2.0-win-x64.exe")]                    // portable Datei, erwartet wird das Setup
    [InlineData("Lagerfeuer-0.2.0-win-arm64-setup.exe")]            // andere Architektur
    [InlineData("Andere-0.2.0-win-x64-setup.exe")]
    [InlineData("Lagerfeuer--win-x64-setup.exe")]
    [InlineData("Lagerfeuer-2-win-x64-setup.exe")]                  // publish.ps1 schreibt immer mindestens zwei Stellen
    [InlineData("Lagerfeuer-0.2.0-beta-win-x64-setup.exe")]
    [InlineData("Lagerfeuer-0.2.0-win-x64-setup.exe.bat")]
    [InlineData("Lagerfeuer-0.2.0-win-x64-setup.exe ")]
    [InlineData("Lagerfeuer-0.2.0-win-x64-setup.exe\n")]
    [InlineData("Lagerfeuer-0.2.0-win-x64-setup.exe:strom")]
    [InlineData("../Lagerfeuer-0.2.0-win-x64-setup.exe")]
    [InlineData("..\\..\\Lagerfeuer-0.2.0-win-x64-setup.exe")]
    [InlineData("/tmp/Lagerfeuer-0.2.0-win-x64-setup.exe")]
    [InlineData("C:\\Users\\Public\\Lagerfeuer-0.2.0-win-x64-setup.exe")]
    [InlineData("Lagerfeuer-0.2.0/../x-win-x64-setup.exe")]
    public void AllesAndere_WirdAbgelehnt(string? name)
        => Assert.False(UpdateService.IsExpectedAssetName(name, false, "win-x64"));

    // ---------- Auswahl im Release ----------

    [Fact]
    public async Task Check_WaehltNurDieDateiMitErwartetemNamen()
    {
        // Vor der richtig benannten Datei stehen zwei, die auf dieselbe Endung hören.
        var (service, _) = Create(_ => FakeHandler.Json(ReleaseJson(
            "..\\..\\Lagerfeuer-0.2.0-win-x64-setup.exe", "Andere-0.2.0-win-x64-setup.exe", Setup)));

        var update = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Setup, update?.Asset?.Name);
    }

    [Fact]
    public async Task Check_OhneDateiMitErwartetemNamen_BleibtNurDerLink()
    {
        var (service, _) = Create(_ => FakeHandler.Json(ReleaseJson(
            "..\\..\\Lagerfeuer-0.2.0-win-x64-setup.exe", "Andere-0.2.0-win-x64-setup.exe")));

        var update = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Null(update.Asset);
    }

    // ---------- Download ----------

    [Fact]
    public async Task Download_MitErwartetemNamen_LandetImZielverzeichnis()
    {
        var (service, _) = Create(_ => Datei());
        var asset = new UpdateAsset(Setup, "https://example.test/download", Bytes.Length, Sha256);

        var file = await service.DownloadAsync(asset, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir.Path, Setup), file);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("Andere-0.2.0-win-x64-setup.exe")]
    [InlineData("Lagerfeuer-0.2.0-win-x64.exe")]
    [InlineData("Lagerfeuer-0.2.0-win-x64-setup.exe.bat")]
    public async Task Download_MitUnerwartetemNamen_LaedtNichtsUndSchreibtNichts(string name)
    {
        var (service, handler) = Create(_ => Datei());
        var asset = new UpdateAsset(name, "https://example.test/download", Bytes.Length, Sha256);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Contains("Dateiname", ex.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path));
    }

    [Fact]
    public async Task Download_MitPfadNachOben_SchreibtNichtAusserhalbDesZielverzeichnisses()
    {
        // Das Zielverzeichnis liegt eine Ebene unter dem Testverzeichnis: Ein „..“ landete in diesem,
        // nicht im gemeinsamen Temp-Ordner aller Testläufe.
        var ziel = Directory.CreateDirectory(Path.Combine(_dir.Path, "ziel")).FullName;
        var (service, handler) = Create(_ => Datei(), ziel);
        var asset = new UpdateAsset(Path.Combine("..", Setup), "https://example.test/download", Bytes.Length, Sha256);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
        Assert.Equal([ziel], Directory.GetFileSystemEntries(_dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(ziel));
    }

    [Fact]
    public async Task Download_MitAbsolutemPfad_SchreibtNichtDorthin()
    {
        using var fremd = new TempDir();
        var (service, handler) = Create(_ => Datei());
        var asset = new UpdateAsset(Path.Combine(fremd.Path, Setup), "https://example.test/download", Bytes.Length, Sha256);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(fremd.Path));
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path));
    }

    public void Dispose() => _dir.Dispose();
}
