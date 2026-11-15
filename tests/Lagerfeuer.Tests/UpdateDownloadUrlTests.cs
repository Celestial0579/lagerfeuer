using System.Net;
using System.Security.Cryptography;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// #49: Der Updater lädt nur von den Releases dieses Repos über https. Eine andere Adresse aus der Antwort der API
/// wird nicht angefragt, und es entsteht keine Datei.
/// </summary>
public sealed class UpdateDownloadUrlTests : IDisposable
{
    private readonly TempDir _dir = new();

    private static readonly byte[] Bytes = TestProgram.Bytes(TestProgram.X86, "neues Setup");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Bytes));
    private const string Setup = "Lagerfeuer-0.2.0-win-x64-setup.exe";

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("https://github.com/Celestial0579/lagerfeuer/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://GitHub.com/celestial0579/Lagerfeuer/releases/download/v0.2.0/" + Setup)]
    public void ReleaseAdresse_WirdAngenommen(string url) => Assert.True(UpdateService.IsReleaseDownloadUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("releases/download/v0.2.0/" + Setup)]
    [InlineData("http://github.com/Celestial0579/lagerfeuer/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://github.com.example.org/Celestial0579/lagerfeuer/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://example.org/Celestial0579/lagerfeuer/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://github.com:8443/Celestial0579/lagerfeuer/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://nutzer@github.com/Celestial0579/lagerfeuer/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://github.com/Fremder/lagerfeuer/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://github.com/Celestial0579/anderes/releases/download/v0.2.0/" + Setup)]
    [InlineData("https://github.com/Celestial0579/lagerfeuer/releases/download/../../../Fremder/x/releases/download/v1/" + Setup)]
    [InlineData("https://github.com/Celestial0579/lagerfeuer/archive/v0.2.0.zip")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    public void FremdeAdresse_WirdAbgelehnt(string? url) => Assert.False(UpdateService.IsReleaseDownloadUrl(url));

    [Fact]
    public async Task FremdeAdresse_KeineAnfrageUndKeineDatei()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        var service = new UpdateService(new HttpClient(handler), new Version(0, 1, 0), false, "win-x64", _dir.Path);
        var asset = new UpdateAsset(Setup, "https://example.org/" + Setup, Bytes.Length, Digest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Contains("gehört nicht zu den Releases", ex.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }
}
