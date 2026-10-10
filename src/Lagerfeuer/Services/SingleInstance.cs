using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lagerfeuer.Services;

/// <summary>
/// Sorgt dafür, dass nur eine Lagerfeuer-Instanz läuft. Weitere Starts (z.B. durch den Browser über
/// audiobookshelf://oauth?… nach der SSO-Anmeldung) reichen ihre Argumente per Named Pipe an die
/// laufende Instanz weiter und beenden sich.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    public const string ActivateMessage = "activate";

    private static readonly string PipeName = $"Lagerfeuer.Activation.{Environment.UserName}";
    private const string MutexName = @"Local\Lagerfeuer.Primary";

    private readonly CancellationTokenSource _cts = new();
    private Mutex? _mutex;

    /// <summary>Nachricht einer weiteren Instanz (Hintergrund-Thread).</summary>
    public event Action<string>? MessageReceived;

    public bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (created) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public void StartServer() => _ = Task.Run(ServerLoopAsync);

    private async Task ServerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server);
                var message = (await reader.ReadToEndAsync(_cts.Token)).Trim();
                if (message.Length > 0) MessageReceived?.Invoke(message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Aktivierungs-Pipe", ex);
                await Task.Delay(500);
            }
        }
    }

    /// <summary>Sendet eine Nachricht an die laufende Instanz und erlaubt ihr, sich in den Vordergrund zu holen.</summary>
    public static bool SendToPrimary(string message)
    {
        AllowSetForegroundWindow(AsfwAny);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.Write(message);
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Weiterleitung an laufende Instanz fehlgeschlagen", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex?.Dispose();
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}

/// <summary>Registriert Lagerfeuer (pro Benutzer, ohne Adminrechte) als Handler für audiobookshelf://.</summary>
public static class ProtocolRegistration
{
    public const string Scheme = "audiobookshelf";
    private const string KeyPath = @"Software\Classes\" + Scheme;

    public static void EnsureRegistered()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Pfad der Anwendung unbekannt.");
        var command = $"\"{exe}\" \"%1\"";

        using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command"))
        {
            if (existing?.GetValue("") as string == command) return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("", "URL:Lagerfeuer (Audiobookshelf)");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
        using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
        Log.Info($"Protokoll {Scheme}:// registriert für {exe}");
    }

    public static bool IsCallback(string arg)
        => arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);
}
