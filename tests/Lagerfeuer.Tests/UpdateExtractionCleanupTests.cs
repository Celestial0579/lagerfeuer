using System.Diagnostics;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Die portable EXE räumt die Entpackordner früherer Fassungen weg. Weil die App dafür Ordner löscht, prüfen die
/// meisten Tests hier, was alles liegen bleibt: der eigene Ordner, Fremdes, Verknüpfungen, Benutztes, und bei
/// der installierten Fassung alles.
/// </summary>
public sealed class UpdateExtractionCleanupTests : IDisposable
{
    private const string Own = "IT2layXbRNhk";
    private const string Old = "I32pbR3xKl8-";
    private const string AppName = "Lagerfeuer-0.2.0-win-x64";

    private readonly TempDir _dir = new();

    private string Temp => Path.Combine(_dir.Path, "Temp");
    private string AppDir => Path.Combine(Temp, ".net", AppName);
    private string Exe => Path.Combine(_dir.Path, "Downloads", AppName + ".exe");
    private string OwnLibrary => Path.Combine(AppDir, Own, "Lagerfeuer.dll");

    public UpdateExtractionCleanupTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
        File.WriteAllText(Exe, "exe");
        Extraction(Path.Combine(AppDir, Own));
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>Legt einen Ordner an, der aussieht wie ein Entpackordner der portablen EXE.</summary>
    private static string Extraction(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        File.WriteAllText(Path.Combine(directory, "Lagerfeuer.dll"), "dll");
        File.WriteAllText(Path.Combine(directory, "Lagerfeuer.Core.dll"), "dll");
        File.WriteAllText(Path.Combine(directory, "Assets", "AppIcon.ico"), "ico");
        return directory;
    }

    private static int Files(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;

    /// <summary>Unter Windows eine Junction (die braucht keine besonderen Rechte), sonst ein Symlink.</summary>
    private static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private int Run(Action<string, string>? move = null) => UpdateService.RemoveStaleExtractions(OwnLibrary, Exe, Temp, move);

    // ---------- Was gelöscht wird ----------

    [Fact]
    public void FruehererEntpackordner_WirdGeloescht_DerEigeneBleibt()
    {
        var old = Extraction(Path.Combine(AppDir, Old));

        Assert.Equal(1, Run());

        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(old + ".entfernen"));
        Assert.Equal(3, Files(Path.Combine(AppDir, Own)));
    }

    [Fact]
    public void MehrereFruehere_WerdenAlleGeloescht()
    {
        Extraction(Path.Combine(AppDir, Old));
        Extraction(Path.Combine(AppDir, "KtT-gCFU8cmf"));

        Assert.Equal(2, Run());

        Assert.Equal([Path.Combine(AppDir, Own)], Directory.GetDirectories(AppDir));
    }

    [Fact]
    public void RestEinesAbgebrochenenLoeschens_WirdBeimNaechstenMalEntfernt()
    {
        // Geprüft und umbenannt war er schon; die Bibliothek fehlt, weil das Löschen mittendrin abbrach.
        var rest = Path.Combine(AppDir, Old + ".entfernen");
        Directory.CreateDirectory(Path.Combine(rest, "Assets"));
        File.WriteAllText(Path.Combine(rest, "Assets", "AppIcon.ico"), "ico");

        Assert.Equal(1, Run());

        Assert.False(Directory.Exists(rest));
    }

    // ---------- Was liegen bleibt ----------

    [Fact]
    public void InstallierteFassung_LoeschtNichts()
    {
        // Die Nachbarn des Programmordners sind andere Programme.
        var programs = Path.Combine(_dir.Path, "Programs");
        var lagerfeuer = Path.Combine(programs, "Lagerfeuer");
        Extraction(lagerfeuer);
        File.WriteAllText(Path.Combine(lagerfeuer, "Lagerfeuer.exe"), "exe");
        var other = Extraction(Path.Combine(programs, "I32pbR3xKl8-"));

        var removed = UpdateService.RemoveStaleExtractions(
            Path.Combine(lagerfeuer, "Lagerfeuer.dll"), Path.Combine(lagerfeuer, "Lagerfeuer.exe"), Temp);

        Assert.Equal(0, removed);
        Assert.Equal(3, Files(other));
    }

    [Fact]
    public void EntpackordnerAusserhalbVonTemp_WirdNichtAngefasst()
    {
        // z.B. über DOTNET_BUNDLE_EXTRACT_BASE_DIR verlegt: nicht der Ort, den wir kennen.
        var elsewhere = Path.Combine(_dir.Path, "Woanders", ".net", AppName);
        Extraction(Path.Combine(elsewhere, Own));
        var old = Extraction(Path.Combine(elsewhere, Old));

        var removed = UpdateService.RemoveStaleExtractions(Path.Combine(elsewhere, Own, "Lagerfeuer.dll"), Exe, Temp);

        Assert.Equal(0, removed);
        Assert.Equal(3, Files(old));
    }

    [Fact]
    public void OrdnerEinerAnderenExe_WirdNichtAngefasst()
    {
        var old = Extraction(Path.Combine(AppDir, Old));

        var removed = UpdateService.RemoveStaleExtractions(OwnLibrary, Path.Combine(_dir.Path, "Downloads", "Anderes.exe"), Temp);

        Assert.Equal(0, removed);
        Assert.Equal(3, Files(old));
    }

    [Fact]
    public void EigenerOrdnerSiehtNichtWieEinEntpackordnerAus_NichtsWirdGeloescht()
    {
        // Die Lage ist nicht die erwartete: lieber nichts tun.
        var own = Extraction(Path.Combine(AppDir, "eigener-ordner"));
        var old = Extraction(Path.Combine(AppDir, Old));

        var removed = UpdateService.RemoveStaleExtractions(Path.Combine(own, "Lagerfeuer.dll"), Exe, Temp);

        Assert.Equal(0, removed);
        Assert.Equal(3, Files(old));
    }

    [Theory]
    [InlineData("Sicherung")]                 // heißt nicht wie eine Kennung
    [InlineData("I32pbR3xKl8")]               // elf Zeichen
    [InlineData("I32pbR3xKl8-x")]             // dreizehn Zeichen
    [InlineData("I32pbR3x Kl8")]              // Leerzeichen
    [InlineData("I32pbR3xKl8-.alt")]          // andere Endung
    [InlineData("Sicherung.entfernen")]       // Endung allein genügt nicht
    public void NachbarMitAnderemNamen_BleibtLiegen(string name)
    {
        var other = Extraction(Path.Combine(AppDir, name));

        Assert.Equal(0, Run());

        Assert.Equal(3, Files(other));
    }

    [Fact]
    public void NachbarOhneProgrammbibliothek_BleibtLiegen()
    {
        // Heißt zufällig wie eine Kennung, ist aber kein Entpackordner von Lagerfeuer.
        var other = Path.Combine(AppDir, Old);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "notizen.txt"), "wichtig");

        Assert.Equal(0, Run());

        Assert.Equal("wichtig", File.ReadAllText(Path.Combine(other, "notizen.txt")));
    }

    [Fact]
    public void NachbarMitStarter_BleibtLiegen()
    {
        // Bibliothek und Starter nebeneinander: Das ist ein ausgepackter Programmordner, kein Entpackordner.
        var other = Extraction(Path.Combine(AppDir, Old));
        File.WriteAllText(Path.Combine(other, "Lagerfeuer.exe"), "exe");

        Assert.Equal(0, Run());

        Assert.Equal(4, Files(other));
    }

    [Fact]
    public void AllesAusserhalbDesEigenenStammordners_BleibtLiegen()
    {
        Extraction(Path.Combine(AppDir, Old));
        var otherApp = Extraction(Path.Combine(Temp, ".net", "AnderesProgramm", Old));
        var inTemp = Extraction(Path.Combine(Temp, Old));

        Assert.Equal(1, Run());

        Assert.Equal(3, Files(otherApp));
        Assert.Equal(3, Files(inTemp));
    }

    // ---------- Verknüpfungen ----------

    [Fact]
    public void VerknuepfungAlsNachbar_WirdNichtVerfolgtUndNichtGeloescht()
    {
        // Das Ziel sieht aus wie ein Entpackordner und liegt außerhalb.
        var target = Extraction(Path.Combine(_dir.Path, "Dokumente"));
        File.WriteAllText(Path.Combine(target, "wichtig.txt"), "wichtig");
        var link = Path.Combine(AppDir, Old);
        Link(link, target);

        Assert.Equal(0, Run());

        Assert.True(Directory.Exists(link));
        Assert.Equal(4, Files(target));
    }

    [Fact]
    public void VerknuepfungImInneren_OrdnerBleibtGanzLiegen()
    {
        var target = Path.Combine(_dir.Path, "Dokumente");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "wichtig.txt"), "wichtig");
        var old = Extraction(Path.Combine(AppDir, Old));
        Link(Path.Combine(old, "Assets", "dokumente"), target);

        Assert.Equal(0, Run());

        Assert.True(File.Exists(Path.Combine(old, "Lagerfeuer.dll")));
        Assert.False(Directory.Exists(old + ".entfernen"));
        Assert.Equal("wichtig", File.ReadAllText(Path.Combine(target, "wichtig.txt")));
    }

    [Fact]
    public void StammordnerIstEineVerknuepfung_NichtsWirdGeloescht()
    {
        // %TEMP%\.net\<Name der EXE> zeigt woandershin: Die „Nachbarn“ lägen dann dort.
        var real = Path.Combine(_dir.Path, "Woanders");
        Extraction(Path.Combine(real, Own));
        var old = Extraction(Path.Combine(real, Old));
        Directory.Delete(AppDir, recursive: true);
        Link(AppDir, real);

        Assert.Equal(0, Run());

        Assert.Equal(3, Files(old));
    }

    // ---------- In Benutzung ----------

    [Fact]
    public void LaesstSichNichtUmbenennen_BleibtUnberuehrtUndOhneFehler()
    {
        // So meldet sich ein Ordner, aus dem noch eine Instanz läuft.
        var old = Extraction(Path.Combine(AppDir, Old));

        var removed = Run((_, _) => throw new IOException("The process cannot access the file because it is being used by another process."));

        Assert.Equal(0, removed);
        Assert.Equal(3, Files(old));
    }

    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public void DateiDarinIstGeoeffnet_OrdnerBleibtUnberuehrt(FileShare share)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Nur Windows verweigert das Umbenennen eines Ordners, in dem eine Datei geöffnet ist.");
        var old = Extraction(Path.Combine(AppDir, Old));

        int removed;
        using (new FileStream(Path.Combine(old, "Lagerfeuer.Core.dll"), FileMode.Open, FileAccess.Read, share))
            removed = Run();

        Assert.Equal(0, removed);
        Assert.Equal(3, Files(old));
        Assert.False(Directory.Exists(old + ".entfernen"));
    }

    // ---------- Fehlende Angaben ----------

    [Theory]
    [InlineData(null, "exe")]
    [InlineData("", "exe")]
    [InlineData("dll", null)]
    [InlineData("dll", "")]
    public void OhneAngaben_PassiertNichts(string? library, string? exe)
    {
        var old = Extraction(Path.Combine(AppDir, Old));

        var removed = UpdateService.RemoveStaleExtractions(library == "dll" ? OwnLibrary : library, exe == "exe" ? Exe : exe, Temp);

        Assert.Equal(0, removed);
        Assert.Equal(3, Files(old));
    }
}
