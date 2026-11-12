using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Programme von Windows startet Lagerfeuer nur mit vollem Pfad und ohne die Shell. Beim bloßen Namen sucht
/// Windows zuerst im aktuellen Ordner; bei der portablen EXE ist das der Ordner, aus dem sie gestartet wurde.
/// </summary>
public class SystemProgramsTests
{
    // Steht für den Windows-Ordner: ein voller Pfad, den es auf jedem Betriebssystem gibt.
    private static readonly string Windows = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "Lagerfeuer", "cache");

    [Fact]
    public void OrdnerOeffnen_NenntDenExplorerMitVollemPfad()
    {
        var info = SystemPrograms.OpenFolder(Folder, Windows);

        Assert.Equal(Path.Combine(Windows, "explorer.exe"), info.FileName);
        Assert.True(Path.IsPathFullyQualified(info.FileName));
    }

    [Fact]
    public void OrdnerOeffnen_GehtNichtUeberDieShell()
    {
        // Über die Shell (UseShellExecute) sucht Windows das Programm; ohne sie startet es genau diese Datei.
        var info = SystemPrograms.OpenFolder(Folder, Windows);

        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void OrdnerOeffnen_UebergibtDenOrdnerAlsEinArgument()
    {
        var mitLeerzeichen = Path.Combine(Path.GetTempPath(), "Mein Ordner", "mit Leerzeichen");

        var info = SystemPrograms.OpenFolder(mitLeerzeichen, Windows);

        Assert.Equal([mitLeerzeichen], info.ArgumentList);
        Assert.Equal("", info.Arguments); // nichts, was noch einmal zerlegt würde
    }

    [Fact]
    public void OrdnerOeffnen_OhneAbschliessendenTrenner()
    {
        // Ein Trenner vor dem schließenden Anführungszeichen verwirrt manche Programme beim Zerlegen der Zeile.
        var info = SystemPrograms.OpenFolder(Folder + Path.DirectorySeparatorChar, Windows);

        Assert.Equal([Folder], info.ArgumentList);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Windows")]
    [InlineData("explorer.exe")]
    public void WindowsOrdnerUnbekannt_StartetNichtsStattAufDenNamenZurueckzufallen(string windowsDirectory)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SystemPrograms.OpenFolder(Folder, windowsDirectory));

        Assert.Contains("Windows-Ordner", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cache")]
    [InlineData("..")]
    public void OrdnerOhneVollenPfad_WirdAbgelehnt(string folder)
        => Assert.Throws<ArgumentException>(() => SystemPrograms.OpenFolder(folder, Windows));

    [Fact]
    public void ImQuelltext_NenntKeinAufrufEinProgrammAlsFestenText()
    {
        // Ein Zeichenketten-Literal als Programm in ProcessStartInfo oder Process.Start wäre ein bloßer Name
        // wie "explorer.exe" – oder ein fest verdrahteter Pfad, den es hier auch nicht geben soll.
        var src = FindSourceFolder();
        Assert.SkipWhen(src is null, "Der Quelltext liegt nicht neben den Tests.");

        var call = new System.Text.RegularExpressions.Regex("(new\\s+ProcessStartInfo|Process\\.Start)\\s*\\(\\s*\\$?@?\"");
        var skip = new[] { $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}" };
        var hits = Directory.EnumerateFiles(src!, "*.cs", SearchOption.AllDirectories)
            .Where(file => !skip.Any(file.Contains))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, index, line)))
            .Where(x => call.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(hits);
    }

    /// <summary>Der Ordner „src“ des Repositorys, von den gebauten Tests aus gesucht.</summary>
    private static string? FindSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var src = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(src, "Lagerfeuer.Core")) && Directory.Exists(Path.Combine(src, "Lagerfeuer"))) return src;
        }
        return null;
    }

    [Fact]
    public void UnterWindows_LiegtDerExplorerDort()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Den Explorer gibt es nur unter Windows.");

        var info = SystemPrograms.OpenFolder(Folder);

        Assert.True(File.Exists(info.FileName), info.FileName);
        Assert.EndsWith(@"\explorer.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
