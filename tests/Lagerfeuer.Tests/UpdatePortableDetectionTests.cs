using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Portabel oder installiert? Davon hängt ab, welche Datei der Updater lädt und ob er die laufende EXE tauscht.
/// Die portable EXE entpackt sich beim Start nach %TEMP%\.net: Ihre Bibliothek liegt dann auf der Platte,
/// aber in einem anderen Ordner als die EXE und ohne Starter daneben.
/// </summary>
public sealed class UpdatePortableDetectionTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string Touch(string directory, string name)
    {
        var dir = Path.Combine(_tmp.Path, directory);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void EntpacktePortableExe_GiltAlsPortabel()
    {
        // So liegt die portable Fassung auf der Platte: die EXE dort, wo der Nutzer sie abgelegt hat,
        // die Bibliothek entpackt unter %TEMP%\.net. Die Prüfung auf eine leere Location sah darin eine Installation.
        var exe = Touch("Downloads", "Lagerfeuer-0.2.0-win-x64.exe");
        var library = Touch(Path.Combine("Temp", ".net", "Lagerfeuer-0.2.0-win-x64", "KtT-gCFU8cmf"), "Lagerfeuer.dll");

        Assert.True(UpdateService.IsSingleFileExe(library, exe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SingleFileExeOhneEntpacken_GiltAlsPortabel(string? location)
    {
        // Ohne IncludeAllContentForSelfExtract liegt die Bibliothek gar nicht auf der Platte.
        var exe = Touch("Downloads", "Lagerfeuer-0.2.0-win-x64.exe");

        Assert.True(UpdateService.IsSingleFileExe(location, exe));
    }

    [Fact]
    public void Installation_GiltNichtAlsPortabel()
    {
        var exe = Touch(Path.Combine("Programs", "Lagerfeuer"), "Lagerfeuer.exe");
        var library = Touch(Path.Combine("Programs", "Lagerfeuer"), "Lagerfeuer.dll");

        Assert.False(UpdateService.IsSingleFileExe(library, exe));
    }

    [Fact]
    public void StarterNebenDerBibliothek_GiltNichtAlsPortabel()
    {
        // Die EXE des Prozesses liegt woanders (fremder Host, oder derselbe Ordner über einen zweiten Pfad),
        // aber neben der Bibliothek liegt ihr Starter: Hier läuft keine Single-File-EXE.
        var host = Touch("dotnet", "dotnet.exe");
        var library = Touch(Path.Combine("Programs", "Lagerfeuer"), "Lagerfeuer.dll");
        Touch(Path.Combine("Programs", "Lagerfeuer"), "Lagerfeuer.exe");

        Assert.False(UpdateService.IsSingleFileExe(library, host));
    }

    [Fact]
    public void UmbenannterStarterImSelbenOrdner_GiltNichtAlsPortabel()
    {
        var exe = Touch(Path.Combine("Programs", "Lagerfeuer"), "Hoerbuch.exe");
        var library = Touch(Path.Combine("Programs", "Lagerfeuer"), "Lagerfeuer.dll");

        Assert.False(UpdateService.IsSingleFileExe(library, exe));
    }

    [Fact]
    public void GleicherOrdnerInAndererSchreibweise_GiltNichtAlsPortabel()
    {
        // Windows unterscheidet bei Pfaden keine Groß- und Kleinschreibung.
        var library = Touch("App", "Lagerfeuer.dll");
        var exe = Path.Combine(Path.GetDirectoryName(library)!.ToUpperInvariant(), "Hoerbuch.exe");

        Assert.False(UpdateService.IsSingleFileExe(library, exe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OhnePfadDerExe_GiltNichtAlsPortabel(string? processPath)
    {
        var library = Touch(Path.Combine("Temp", ".net", "Lagerfeuer", "abc"), "Lagerfeuer.dll");

        Assert.False(UpdateService.IsSingleFileExe(library, processPath));
    }
}
