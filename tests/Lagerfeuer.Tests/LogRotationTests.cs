using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>#49: Das Log liegt unter %LOCALAPPDATA%\Lagerfeuer\logs und wechselt ab einer Größe in eine Vorgängerdatei.</summary>
public sealed class LogRotationTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Log => Path.Combine(_dir.Path, "lagerfeuer.log");
    private string Previous => Path.Combine(_dir.Path, "lagerfeuer.1.log");

    [Fact]
    public void LogLiegtImOrdnerLogs()
    {
        Assert.Equal(Path.Combine(AppPaths.Root, "logs"), AppPaths.LogDirectory);
        Assert.Equal(AppPaths.LogDirectory, Path.GetDirectoryName(AppPaths.LogFile));
    }

    [Fact]
    public void UnterDerGrenze_BleibtAlles()
    {
        File.WriteAllText(Log, new string('a', 100));
        AppPaths.RotateLog(Log, 100);
        Assert.True(File.Exists(Log));
        Assert.False(File.Exists(Previous));
    }

    [Fact]
    public void UeberDerGrenze_WirdZurVorgaengerdatei()
    {
        File.WriteAllText(Previous, "ganz alt");
        File.WriteAllText(Log, new string('b', 101));

        AppPaths.RotateLog(Log, 100);

        Assert.False(File.Exists(Log));
        Assert.Equal(new string('b', 101), File.ReadAllText(Previous));
    }

    [Fact]
    public void OhneLog_GeschiehtNichts()
    {
        AppPaths.RotateLog(Log, 100);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }
}
