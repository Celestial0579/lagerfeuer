using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Größenangaben: unter 1 GB in MB, darüber in GB.</summary>
public class FormatSizeTests
{
    private const long Mb = 1024 * 1024, Gb = 1024 * Mb;

    private static string InKultur(string name, Func<string> format)
    {
        var vorher = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return format(); }
        finally { CultureInfo.CurrentCulture = vorher; }
    }

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(-5, "0 MB")]
    [InlineData(1, "unter 1 MB")]
    [InlineData(400 * 1024, "unter 1 MB")]
    [InlineData(600 * 1024, "1 MB")]
    [InlineData(14 * Mb, "14 MB")]
    [InlineData(28 * Mb + 300 * 1024, "28 MB")]   // zwei Testbücher: früher „0,0 GB“
    [InlineData(1023 * Mb, "1023 MB")]
    public void UnterEinemGigabyte_InMegabyte(long bytes, string expected)
        => Assert.Equal(expected, Format.Size(bytes));

    [Theory]
    [InlineData(Gb, "1,0 GB")]
    [InlineData(Gb - 100 * 1024, "1,0 GB")]       // gerundet 1024 MB: schon in GB
    [InlineData(Gb + 400 * Mb, "1,4 GB")]
    [InlineData(12 * Gb + 512 * Mb, "12,5 GB")]
    public void AbEinemGigabyte_InGigabyteMitEinerStelle(long bytes, string expected)
        => Assert.Equal(expected, InKultur("de-DE", () => Format.Size(bytes)));

    [Fact]
    public void Dezimalzeichen_FolgtDerSpracheDesSystems()
        => Assert.Equal("1.4 GB", InKultur("en-US", () => Format.Size(Gb + 400 * Mb)));

    [Theory]
    [InlineData(0, "0 Bücher")]
    [InlineData(1, "1 Buch")]
    [InlineData(2, "2 Bücher")]
    [InlineData(17, "17 Bücher")]
    public void Anzahl_MitPassendemWort(int count, string expected)
        => Assert.Equal(expected, Format.Count(count, "Buch", "Bücher"));
}

/// <summary>Name eines angemeldeten Geräts in den Einstellungen und der User-Agent, aus dem er entsteht.</summary>
public class DeviceNameTests
{
    private static AuthSession Session(string json) => JsonSerializer.Deserialize<AuthSession>(json, AbsApiClient.Json)!;

    [Theory]
    // Lagerfeuer ab dieser Fassung: Name und Version aus dem User-Agent, das Betriebssystem vom Server.
    [InlineData("Lagerfeuer/0.2.2 (Windows NT 10.0; Win64; x64)", "Windows", "10", "Lagerfeuer 0.2.2 · Windows 10")]
    [InlineData("Lagerfeuer/1.0.0 (Windows NT 10.0; ARM64)", "Windows", "10", "Lagerfeuer 1.0.0 · Windows 10")]
    // Fassungen bis 0.2.1: Der Server las aus „Lagerfeuer-Windows/0.1“ das Betriebssystem „Windows 0.1“.
    [InlineData("Lagerfeuer-Windows/0.1", "Windows", "0.1", "Lagerfeuer · Windows")]
    public void Lagerfeuer_ErscheintMitNamen(string userAgent, string osName, string osVersion, string expected)
    {
        var session = Session($$"""
            {"id":"1","userAgent":"{{userAgent}}","deviceInfo":{"browserName":null,"osName":"{{osName}}","osVersion":"{{osVersion}}" } }
            """);

        Assert.Equal(expected, session.DisplayName);
    }

    [Theory]
    [InlineData("Lagerfeuer/0.2.2 (Windows NT 10.0; Win64; x64)", "Lagerfeuer 0.2.2")]
    [InlineData("Lagerfeuer/", "Lagerfeuer")]
    [InlineData("Lagerfeuer-Windows/0.1", "Lagerfeuer · Windows")]
    public void Lagerfeuer_OhneAngabenDesServers(string userAgent, string expected)
        => Assert.Equal(expected, Session($$"""{"id":"1","userAgent":"{{userAgent}}","deviceInfo":null}""").DisplayName);

    [Fact]
    public void AndereGeraete_BleibenWieBisher()
    {
        Assert.Equal("Firefox · Linux", Session("""
            {"id":"1","userAgent":"Mozilla/5.0","deviceInfo":{"browserName":"Firefox","osName":"Linux","osVersion":""}}
            """).DisplayName);
        Assert.Equal("Chrome · Android 14 · Pixel 8", Session("""
            {"id":"1","userAgent":"Mozilla/5.0","deviceInfo":{"browserName":"Chrome","osName":"Android","osVersion":"14","model":"Pixel 8"}}
            """).DisplayName);
        Assert.Equal("curl/8.5.0", Session("""{"id":"1","userAgent":"curl/8.5.0","deviceInfo":null}""").DisplayName);
        Assert.Equal("Unbekanntes Gerät", Session("""{"id":"1","userAgent":null,"deviceInfo":null}""").DisplayName);
    }

    [Theory]
    [InlineData("0.2.2.0", Architecture.X64, "Lagerfeuer/0.2.2 (Windows NT 10.0; Win64; x64)")]
    [InlineData("1.10.3.0", Architecture.Arm64, "Lagerfeuer/1.10.3 (Windows NT 10.0; ARM64)")]
    [InlineData("0.3", Architecture.X64, "Lagerfeuer/0.3.0 (Windows NT 10.0; Win64; x64)")]
    public void UserAgent_NenntAppVersionUndWindows(string version, Architecture architecture, string expected)
        => Assert.Equal(expected, AbsApiClient.BuildUserAgent(Version.Parse(version), architecture));

    [Fact]
    public async Task UserAgent_GehtMitJederAnfrageHinaus()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"isInit":true,"authMethods":["local"]}"""));
        using var api = new AbsApiClient(handler);

        await api.GetStatusAsync(new Uri("https://abs.test/"), TestContext.Current.CancellationToken);

        var agent = handler.Requests.Single().Headers.UserAgent.ToString();
        Assert.Equal(AbsApiClient.UserAgent, agent);
        Assert.StartsWith("Lagerfeuer/", agent);
        Assert.Contains("Windows NT 10.0", agent);   // daran erkennt der Server das Betriebssystem
        Assert.DoesNotContain("Lagerfeuer-Windows/0.1", agent);
    }

    [Fact]
    public void EigenerUserAgent_ErgibtEinenLesbarenNamen()
    {
        // Der Kreis schließt sich: Was die App schickt, erkennt sie in der Liste wieder.
        var session = Session($$"""
            {"id":"1","userAgent":"{{AbsApiClient.UserAgent}}","deviceInfo":{"osName":"Windows","osVersion":"10" } }
            """);

        Assert.StartsWith("Lagerfeuer ", session.DisplayName);
        Assert.EndsWith(" · Windows 10", session.DisplayName);
    }
}
