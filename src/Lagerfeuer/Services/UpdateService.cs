using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Lagerfeuer.Services;

/// <summary>
/// Prüft die GitHub-Releases des Projekts auf eine neuere Version und spielt sie ein
/// (Prinzip wie in Lodestone: releases/latest abfragen, Tag als Versionsnummer vergleichen,
/// Entwürfe und Vorabversionen ignorieren).
///
/// Erwartete Release-Assets (erzeugt von build/publish.ps1):
///   Lagerfeuer-&lt;ver&gt;-win-x64-setup.exe   → installierte Version: Setup still mit /S /RESTART ausführen
///   Lagerfeuer-&lt;ver&gt;-win-x64.exe         → portable Version: laufende EXE austauschen und neu starten
/// Die SHA-256-Prüfsumme, die GitHub für jedes Asset liefert, wird vor dem Ausführen geprüft.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryOwner = "Celestial0579";
    public const string RepositoryName = "lagerfeuer";
    public static readonly string RepositoryUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}";

    /// <summary>Argument für den Neustart nach einem Update: die neue Instanz wartet auf das Ende der alten.</summary>
    public const string AfterUpdateArg = "--after-update";

    // Reihenfolge wichtig: CreateClient() verwendet CurrentVersion.
    public static Version CurrentVersion { get; } = Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Single-File-EXE (portabel) statt Installation?</summary>
    public static bool IsPortable => string.IsNullOrEmpty(typeof(App).Assembly.Location);

    private static string Rid => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(
            $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // noch kein Release
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub antwortet mit {(int)resp.StatusCode}.");

        var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(ct);
        if (release is null || release.Draft || release.Prerelease) return null;
        if (!TryParseVersion(release.TagName, out var version) || version <= CurrentVersion) return null;

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(
            IsPortable ? $"-{Rid}.exe" : $"-{Rid}-setup.exe", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo(version, release.Name is { Length: > 0 } n ? n : release.TagName, release.HtmlUrl,
            asset is null ? null : new UpdateAsset(asset.Name, asset.BrowserDownloadUrl, asset.Size, ParseDigest(asset.Digest)));
    }

    /// <summary>Lädt das Update herunter und prüft die Prüfsumme. Gibt den lokalen Pfad zurück.</summary>
    public async Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress, CancellationToken ct = default)
    {
        // Portabel: neben die laufende EXE (gleiches Laufwerk → atomares Umbenennen möglich).
        var target = IsPortable
            ? Environment.ProcessPath + ".new"
            : Path.Combine(Path.GetTempPath(), "Lagerfeuer-Update", asset.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using var resp = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? asset.Size;

        using var sha = SHA256.Create();
        await using (var net = await resp.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await net.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                sha.TransformBlock(buffer, 0, n, null, 0);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
            sha.TransformFinalBlock([], 0, 0);
        }

        if (asset.Sha256 is { } expected && !Convert.ToHexString(sha.Hash!).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException("Die Prüfsumme des Updates stimmt nicht – Download verworfen.");
        }
        return target;
    }

    /// <summary>Startet die Installation des heruntergeladenen Updates. Danach muss sich die App beenden.</summary>
    public void LaunchInstaller(string downloadedFile)
    {
        if (IsPortable)
        {
            // Eine laufende EXE darf umbenannt, aber nicht überschrieben werden.
            var exe = Environment.ProcessPath!;
            var old = exe + ".old";
            File.Delete(old);
            File.Move(exe, old);
            File.Move(downloadedFile, exe);
            Process.Start(new ProcessStartInfo(exe, $"{AfterUpdateArg} {Environment.ProcessId}") { UseShellExecute = true });
        }
        else
        {
            // Das Setup wartet, bis sich die App beendet hat, und startet sie danach neu.
            Process.Start(new ProcessStartInfo(downloadedFile, "/S /RESTART") { UseShellExecute = true });
        }
        Log.Info($"Update gestartet: {downloadedFile}");
    }

    /// <summary>Räumt Reste eines vorherigen Updates auf (beim Start aufrufen).</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                File.Delete(exe + ".old");
                File.Delete(exe + ".new");
            }
            var temp = Path.Combine(Path.GetTempPath(), "Lagerfeuer-Update");
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
        catch
        {
            // z.B. alte EXE noch kurz gesperrt – beim nächsten Start erneut
        }
    }

    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) return false; // Vorabversion (z.B. 1.2.0-beta) nie automatisch anbieten
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return false;
        version = Normalize(v);
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static string? ParseDigest(string? digest)
        => digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lagerfeuer", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}

public sealed record UpdateInfo(Version Version, string Name, string ReleaseUrl, UpdateAsset? Asset);

public sealed record UpdateAsset(string Name, string DownloadUrl, long Size, string? Sha256);
