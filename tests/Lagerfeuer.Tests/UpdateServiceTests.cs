using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class UpdateServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    private static readonly byte[] SetupBytes = "neues Setup"u8.ToArray();
    private static readonly string SetupDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(SetupBytes)).ToLowerInvariant();

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, bool withAssets = true)
        => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = $"Lagerfeuer {tag}",
            html_url = $"https://github.com/Celestial0579/lagerfeuer/releases/tag/{tag}",
            draft,
            prerelease,
            assets = withAssets
                ? new object[]
                {
                    Asset($"Lagerfeuer-{tag.TrimStart('v')}-win-x64-setup.exe", SetupDigest),
                    Asset($"Lagerfeuer-{tag.TrimStart('v')}-win-x64.exe", null),
                    Asset($"Lagerfeuer-{tag.TrimStart('v')}-win-arm64-setup.exe", null),
                    Asset($"Lagerfeuer-{tag.TrimStart('v')}-win-arm64.exe", null),
                }
                : [],
        });

    private static object Asset(string name, string? digest) => new
    {
        name,
        browser_download_url = $"https://github.com/Celestial0579/lagerfeuer/releases/download/v/{name}",
        size = SetupBytes.Length,
        digest,
    };

    private UpdateService Create(HttpResponseMessage response, string current = "0.1.0", bool portable = false, string rid = "win-x64")
    {
        var http = new HttpClient(new FakeHandler(_ => response));
        return new UpdateService(http, Version.Parse(current), portable, rid, _dir.Path);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V0.10.0", "0.10.0")]
    [InlineData("v2", "2.0.0")]
    [InlineData("1.4", "1.4.0")]
    public void TryParseVersion_ErkenntVersionsTags(string tag, string expected)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("v1.2.0-beta.1")]
    [InlineData("1.0.0+build5")]
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseVersion_LehntVorabversionenUndUnsinnAb(string? tag)
        => Assert.False(UpdateService.TryParseVersion(tag, out _));

    [Fact]
    public async Task NeuereVersion_InstallierteApp_BekommtDasSetup()
    {
        var update = await Create(FakeHandler.Json(ReleaseJson("v0.2.0"))).CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0), update.Version);
        Assert.Equal("Lagerfeuer-0.2.0-win-x64-setup.exe", update.Asset!.Name);
        Assert.Equal(SetupDigest[7..], update.Asset.Sha256);
        Assert.EndsWith("/releases/tag/v0.2.0", update.ReleaseUrl);
    }

    [Theory]
    [InlineData(false, "win-arm64", "Lagerfeuer-0.2.0-win-arm64-setup.exe")]
    [InlineData(true, "win-x64", "Lagerfeuer-0.2.0-win-x64.exe")]
    [InlineData(true, "win-arm64", "Lagerfeuer-0.2.0-win-arm64.exe")]
    public async Task WaehltDieDateiPassendZuVarianteUndArchitektur(bool portable, string rid, string expected)
    {
        var update = await Create(FakeHandler.Json(ReleaseJson("v0.2.0")), portable: portable, rid: rid)
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, update!.Asset!.Name);
    }

    [Theory]
    [InlineData("v0.1.0")] // gleiche Version
    [InlineData("v0.0.9")] // ältere Version
    public async Task GleicheOderAeltereVersion_IstKeinUpdate(string tag)
        => Assert.Null(await Create(FakeHandler.Json(ReleaseJson(tag))).CheckAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task EntwurfOderVorabversion_WerdenIgnoriert()
    {
        Assert.Null(await Create(FakeHandler.Json(ReleaseJson("v9.0.0", draft: true))).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Null(await Create(FakeHandler.Json(ReleaseJson("v9.0.0", prerelease: true))).CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NochKeinRelease_IstKeinUpdate()
        => Assert.Null(await Create(FakeHandler.Status(HttpStatusCode.NotFound)).CheckAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task ReleaseOhnePassendeDatei_LiefertNurDenLink()
    {
        var update = await Create(FakeHandler.Json(ReleaseJson("v0.2.0", withAssets: false))).CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Null(update.Asset);
    }

    [Fact]
    public async Task GitHubFehler_WirdGemeldet()
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(FakeHandler.Status(HttpStatusCode.Forbidden)).CheckAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Download_MitKorrekterPruefsumme()
    {
        var service = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SetupBytes) });
        var progress = new List<double>();
        var asset = new UpdateAsset("Lagerfeuer-0.2.0-win-x64-setup.exe", "https://example.test/setup.exe", SetupBytes.Length, SetupDigest[7..]);

        var file = await service.DownloadAsync(asset, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(SetupBytes, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(1.0, progress.Last());
    }

    [Fact]
    public async Task Download_MitFalscherPruefsumme_WirdVerworfen()
    {
        var service = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("manipuliert"u8.ToArray()) });
        var asset = new UpdateAsset("Lagerfeuer-0.2.0-win-x64-setup.exe", "https://example.test/setup.exe", 11, SetupDigest[7..]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_dir.Path, asset.Name)));
    }

    /// <summary>Progress&lt;T&gt; meldet asynchron; für Tests synchron.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose() => _dir.Dispose();
}
