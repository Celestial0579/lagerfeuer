using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lagerfeuer.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lagerfeuer");

    static AppPaths()
    {
        // Einmalige Übernahme der Daten aus der Vorversion (Arbeitsname "AbsClient").
        // Gespeicherte Tokens sind an den alten Namen gebunden → einmal neu anmelden.
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(Root)!, "AbsClient");
            if (Directory.Exists(legacy) && !Directory.Exists(Root))
            {
                Directory.Move(legacy, Root);
                File.Delete(Path.Combine(Root, "credentials.bin"));
            }
        }
        catch
        {
            // nicht kritisch
        }
    }

    public static string Cache => Path.Combine(Root, "cache");
    public static string Covers => Path.Combine(Root, "covers");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string CredentialsFile => Path.Combine(Root, "credentials.bin");
    public static string LogFile => Path.Combine(Root, "log.txt");
}

public sealed class AppSettings
{
    public string? ServerUrl { get; set; }
    public string? Username { get; set; }
    public string? LastLibraryId { get; set; }
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public double PlaybackRate { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public int CacheLimitGb { get; set; } = 10;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool PauseOnCalls { get; set; } = true;
    public bool ResumeAfterCalls { get; set; } = true;
    public string LibrarySort { get; set; } = "media.metadata.title";
    public bool LibrarySortDescending { get; set; }
    public string LibraryFilter { get; set; } = "";
    public bool HideFinishedEpisodes { get; set; }
    public string? SkippedUpdateVersion { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Einstellungen konnten nicht gelesen werden", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.SettingsFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Einstellungen konnten nicht gespeichert werden", ex);
        }
    }
}

/// <summary>Speichert Tokens DPAPI-verschlüsselt (nur für den aktuellen Windows-Benutzer lesbar).</summary>
public static class CredentialStore
{
    public sealed record StoredTokens(string Server, string? AccessToken, string? RefreshToken);

    public static void Save(StoredTokens tokens)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
            File.WriteAllBytes(AppPaths.CredentialsFile,
                ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Log.Error("Zugangsdaten konnten nicht gespeichert werden", ex);
        }
    }

    public static StoredTokens? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.CredentialsFile)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.CredentialsFile), Entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredTokens>(plain);
        }
        catch (Exception ex)
        {
            Log.Error("Zugangsdaten konnten nicht gelesen werden", ex);
            return null;
        }
    }

    public static void Clear()
    {
        try { File.Delete(AppPaths.CredentialsFile); } catch { /* egal */ }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lagerfeuer.Tokens.v1");
}

public static class Log
{
    private static readonly object Sync = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{(ex is null ? "" : " :: " + ex)}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch { /* Logging darf nie crashen */ }
    }
}
