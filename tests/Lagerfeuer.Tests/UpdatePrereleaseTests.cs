using System.Net;
using System.Text.Json;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Vorabversionen (Tag vX.Y.Z-beta.N): Ihre Assembly-Version ist X.Y.Z wie die der fertigen Fassung. Wer die Beta
/// installiert hat, soll die fertige Fassung derselben Nummer trotzdem angeboten bekommen.
/// </summary>
public sealed class UpdatePrereleaseTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static HttpResponseMessage Release(string tag) => FakeHandler.Json(JsonSerializer.Serialize(new
    {
        tag_name = tag,
        name = tag,
        html_url = "https://github.com/Celestial0579/lagerfeuer/releases/tag/" + tag,
        draft = false,
        prerelease = false,
        assets = Array.Empty<object>(),
    }));

    private Task<UpdateInfo?> CheckAsync(string tag, string current, bool currentIsPrerelease)
        => new UpdateService(new HttpClient(new FakeHandler(_ => Release(tag))), Version.Parse(current), false, "win-x64",
            _dir.Path, currentIsPrerelease).CheckAsync(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("0.2.5-beta.1", true)]
    [InlineData("0.2.5-beta.1+e5fa8a4", true)]
    [InlineData("0.2.5", false)]
    [InlineData("0.2.5+e5fa8a4092935ae721f9e81047c96c5876a4554e", false)]
    [InlineData("0.2.5+build-7", false)]
    [InlineData(null, false)]
    public void IsPrereleaseVersion(string? informational, bool expected)
        => Assert.Equal(expected, UpdateService.IsPrereleaseVersion(informational));

    [Fact]
    public async Task Beta_BekommtFertigeFassungDerselbenNummer()
    {
        var update = await CheckAsync("v0.2.5", "0.2.5", currentIsPrerelease: true);
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 5), update.Version);
    }

    [Fact]
    public async Task FertigeFassung_BekommtSichNichtSelbst()
        => Assert.Null(await CheckAsync("v0.2.5", "0.2.5", currentIsPrerelease: false));

    [Fact]
    public async Task Beta_BekommtKeineAeltereFassung()
        => Assert.Null(await CheckAsync("v0.2.4", "0.2.5", currentIsPrerelease: true));
}
