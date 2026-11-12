using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class UpdateServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    private static readonly byte[] SetupBytes = TestProgram.Bytes(TestProgram.X86, "neues Setup");
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

    // ---------- Prüfsumme: ohne sie wird nichts geladen ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_PruefsummeGiltInGrossUndKleinschreibung(bool upper)
    {
        var lower = SetupDigest[7..];
        Assert.NotEqual(lower, lower.ToUpperInvariant()); // sonst prüft der Test nichts
        var service = Create(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SetupBytes) });
        var asset = new UpdateAsset("Lagerfeuer-0.2.0-win-x64-setup.exe", "https://example.test/setup.exe", SetupBytes.Length,
            upper ? lower.ToUpperInvariant() : lower);

        var file = await service.DownloadAsync(asset, null, TestContext.Current.CancellationToken);

        Assert.Equal(SetupBytes, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
    }

    public static TheoryData<string?> UnbrauchbarePruefsummen => new()
    {
        (string?)null,         // GitHub liefert kein digest
        "",
        "abc123",              // zu kurz
        new string('g', 64),   // richtige Länge, aber keine Hex-Zeichen
    };

    [Theory]
    [MemberData(nameof(UnbrauchbarePruefsummen))]
    public async Task Download_OhnePruefsumme_LaedtNichtsUndMeldetEsKlar(string? sha256)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SetupBytes) });
        var service = new UpdateService(new HttpClient(handler), new Version(0, 1, 0), false, "win-x64", _dir.Path);
        var asset = new UpdateAsset("Lagerfeuer-0.2.0-win-x64-setup.exe", "https://example.test/setup.exe", SetupBytes.Length, sha256);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Contains("keine Prüfsumme", ex.Message);
        Assert.Empty(handler.Requests);                          // gar nicht erst geladen
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path)); // nichts abgelegt
    }

    [Fact]
    public async Task ReleaseOhneDigest_WirdGezeigtAberNichtInstalliert()
    {
        // Im Test-Release hat nur das x64-Setup ein digest; die portable Datei hat keins.
        var handler = new FakeHandler(_ => FakeHandler.Json(ReleaseJson("v0.2.0")));
        var service = new UpdateService(new HttpClient(handler), new Version(0, 1, 0), true, "win-x64", _dir.Path);

        var update = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update?.Asset);
        Assert.Null(update.Asset.Sha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(update.Asset, null, TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests); // nur die Abfrage der Releases, kein Download
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:abc123")]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha512:cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void ParseDigest_AllesAusserGueltigemSha256_IstKeinePruefsumme(string? digest)
        => Assert.Null(UpdateService.ParseDigest(digest));

    [Fact]
    public void ParseDigest_LiestGitHubsFormat()
    {
        Assert.Equal(SetupDigest[7..], UpdateService.ParseDigest(SetupDigest));
        Assert.Equal(SetupDigest[7..], UpdateService.ParseDigest("SHA256:" + SetupDigest[7..]));
    }

    // ---------- Tausch der portablen EXE: mit Rückweg ----------

    private string Exe => Path.Combine(_dir.Path, "Lagerfeuer.exe");
    private string Old => Exe + ".old";
    private string New => Exe + ".new";

    private void Lege(string? exe = "alt", string? neu = "neu", string? old = null)
    {
        if (exe is not null) File.WriteAllText(Exe, exe);
        if (neu is not null) File.WriteAllText(New, neu);
        if (old is not null) File.WriteAllText(Old, old);
    }

    /// <summary>Wie File.Move, scheitert aber bei den genannten Aufrufen (1 = erster Aufruf).</summary>
    private static Action<string, string, bool> MoveDasScheitertBei(params int[] aufrufe)
    {
        var n = 0;
        return (von, nach, ueberschreiben) =>
        {
            if (aufrufe.Contains(++n)) throw new IOException($"simulierter Fehler bei Aufruf {n}");
            File.Move(von, nach, ueberschreiben);
        };
    }

    [Fact]
    public void Tausch_Gelingt_NeueExeAmPlatz_BisherigeAlsOld()
    {
        Lege();

        UpdateService.ReplaceExecutable(Exe, New);

        Assert.Equal("neu", File.ReadAllText(Exe));
        Assert.Equal("alt", File.ReadAllText(Old));
        Assert.False(File.Exists(New));
    }

    [Fact]
    public void Tausch_UeberschreibtDenRestVomVorigenUpdate()
    {
        Lege(old: "uralt");

        UpdateService.ReplaceExecutable(Exe, New);

        Assert.Equal("neu", File.ReadAllText(Exe));
        Assert.Equal("alt", File.ReadAllText(Old));
    }

    [Fact]
    public void Tausch_ZweitesUmbenennenScheitert_BisherigeExeLiegtWiederAmPlatz()
    {
        Lege();

        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.ReplaceExecutable(Exe, New, MoveDasScheitertBei(2)));

        Assert.Contains("bleibt bei der bisherigen Version", ex.Message);
        Assert.Equal("alt", File.ReadAllText(Exe));
        Assert.False(File.Exists(Old));
        Assert.Equal("neu", File.ReadAllText(New)); // bleibt liegen; beim nächsten Start räumt CleanupAfterUpdate auf
    }

    [Fact]
    public void Tausch_NeueDateiIstGesperrt_BisherigeExeLiegtWiederAmPlatz()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Nur unter Windows verhindert eine Dateisperre das Umbenennen.");
        Lege();

        using (new FileStream(New, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<InvalidOperationException>(() => UpdateService.ReplaceExecutable(Exe, New));

        Assert.Equal("alt", File.ReadAllText(Exe));
        Assert.False(File.Exists(Old));
        Assert.Equal("neu", File.ReadAllText(New));
    }

    [Fact]
    public void Tausch_AuchDerRueckwegScheitert_MeldungNenntDenOrtDerBisherigenExe()
    {
        Lege();

        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.ReplaceExecutable(Exe, New, MoveDasScheitertBei(2, 3)));

        Assert.Contains(Old, ex.Message);
        Assert.Contains("„Lagerfeuer.exe“", ex.Message);
        Assert.IsType<AggregateException>(ex.InnerException);
        Assert.False(File.Exists(Exe));
        Assert.Equal("alt", File.ReadAllText(Old)); // nichts ist verloren
    }

    [Fact]
    public void Tausch_ErstesUmbenennenScheitert_BisherigeExeBleibtUnberuehrt()
    {
        Lege();

        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.ReplaceExecutable(Exe, New, MoveDasScheitertBei(1)));

        Assert.Contains("nicht umbenennen", ex.Message);
        Assert.Equal("alt", File.ReadAllText(Exe));
        Assert.Equal("neu", File.ReadAllText(New));
        Assert.False(File.Exists(Old));
    }

    [Fact]
    public void Tausch_DownloadFehlt_NichtsWirdAngefasst()
    {
        Lege(neu: null);
        var aufrufe = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.ReplaceExecutable(Exe, New, (_, _, _) => aufrufe++));

        Assert.Contains("nicht mehr da", ex.Message);
        Assert.Equal(0, aufrufe);
        Assert.Equal("alt", File.ReadAllText(Exe));
        Assert.False(File.Exists(Old));
    }

    [Fact]
    public void Rueckweg_NachGelungenemTausch_StelltDieBisherigeExeWiederHer()
    {
        Lege();
        UpdateService.ReplaceExecutable(Exe, New);

        UpdateService.RestoreExecutable(Exe);

        Assert.Equal("alt", File.ReadAllText(Exe));
        Assert.False(File.Exists(Old));
    }

    [Fact]
    public void Rueckweg_Scheitert_MeldungNenntDenOrtDerBisherigenExe()
    {
        Lege();
        UpdateService.ReplaceExecutable(Exe, New);

        var ex = Assert.Throws<InvalidOperationException>(() => UpdateService.RestoreExecutable(Exe, MoveDasScheitertBei(1)));

        Assert.Contains(Old, ex.Message);
        Assert.Equal("alt", File.ReadAllText(Old));
    }

    /// <summary>Progress&lt;T&gt; meldet asynchron; für Tests synchron.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose() => _dir.Dispose();
}
