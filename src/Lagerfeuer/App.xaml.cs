using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Lagerfeuer;

public partial class App : Application
{
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static AbsApiClient Api { get; } = new();
    public static CacheManager Cache { get; } = new(Api, AppPaths.Cache);
    public static CoverCache Covers { get; } = new(Api, AppPaths.Covers);
    public static ProgressStore Progress { get; } = new(Path.Combine(AppPaths.Root, "progress.json"));
    public static LocalSessionStore LocalSessions { get; } = new(Path.Combine(AppPaths.Root, "offline-sessions.json"));
    public static DownloadManager Downloads { get; } = new(Api, Cache, Covers);
    public static CallDetector Calls { get; } = new();
    public static LiveSyncClient LiveSync { get; } = new(Api);

    /// <summary>Lesezeichen wurden (auch von einem anderen Gerät) geändert.</summary>
    public static event EventHandler? BookmarksChanged;

    public static void RaiseBookmarksChanged() => BookmarksChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>Server nicht erreichbar – nur heruntergeladene Bücher verfügbar.</summary>
    public static bool IsOffline { get; set; }

    /// <summary>Der angemeldete Benutzer (nach Login/Autorisierung).</summary>
    public static AbsUser? User { get; set; }

    public static bool CanDownload => User?.Permissions?.Download ?? true;
    public static PlayerService Player { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unbehandelte Ausnahme", e.Exception);
            e.Handled = true;
            MainWindow?.ShowError(e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unbeobachtete Task-Ausnahme", e.Exception);
            e.SetObserved();
        };

        Api.TokensChanged += (_, _) =>
        {
            if (Api.ServerUri is { } server)
                CredentialStore.Save(new CredentialStore.StoredTokens(server.ToString(), Api.AccessToken, Api.RefreshToken));
        };
    }

    private static readonly SingleInstance Instance = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Start durch den Browser (audiobookshelf://oauth?…) oder zweiter Start → an laufende Instanz übergeben.
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
        var callbackUrl = cmdArgs.FirstOrDefault(ProtocolRegistration.IsCallback);

        // Neustart nach Update der portablen EXE: warten, bis die alte Instanz beendet ist.
        var afterUpdate = cmdArgs.IndexOf(UpdateService.AfterUpdateArg);
        if (afterUpdate >= 0 && afterUpdate + 1 < cmdArgs.Count && int.TryParse(cmdArgs[afterUpdate + 1], out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); }
            catch (ArgumentException) { /* bereits beendet */ }
        }

        if (!Instance.TryBecomePrimary())
        {
            SingleInstance.SendToPrimary(callbackUrl ?? SingleInstance.ActivateMessage);
            Environment.Exit(0);
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        Instance.MessageReceived += message => dispatcher.TryEnqueue(() => OnActivationMessage(message));
        Instance.StartServer();
        if (callbackUrl is not null) Log.Info("Mit SSO-Callback gestartet, aber keine Anmeldung aktiv – ignoriert.");
        UpdateService.CleanupAfterUpdate();

        Player = new PlayerService(Api, Cache, Covers, Settings, LocalSessions, Progress, dispatcher);
        Player.ProgressSynced += (_, p) => Progress.Update(p.ItemId, p.CurrentTime, p.Duration, p.EpisodeId);
        Player.LocalSessionEnded += (_, _) => { if (!IsOffline) _ = UploadOfflineSessionsAsync(); };
        Player.AttachCallDetector(Calls);
        if (Settings.PauseOnCalls) _ = Calls.StartAsync();
        LiveSync.ProgressUpdated += (_, p) => dispatcher.TryEnqueue(() => OnRemoteProgress(p));
        LiveSync.UserUpdated += (_, user) => dispatcher.TryEnqueue(() =>
        {
            Progress.Reset(user.MediaProgress);
            if (User is not null)
            {
                User.Bookmarks = user.Bookmarks;
                RaiseBookmarksChanged();
            }
        });
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>
    /// Fortschritt von einem anderen Gerät: Anzeigen aktualisieren und – wenn dasselbe Buch hier
    /// geladen, aber pausiert ist – an die neue Stelle springen.
    /// </summary>
    private static void OnRemoteProgress(RemoteProgress remote)
    {
        if (remote.SessionId is not null && remote.SessionId == Player.SessionId) return; // eigene Meldung
        var p = remote.Progress;
        Progress.Set(p);

        if (Player.ItemId == p.LibraryItemId && p.EpisodeId == Player.EpisodeId && !Player.IsPlaying && !p.IsFinished
            && Math.Abs(Player.Position - p.CurrentTime) > 5)
        {
            _ = Player.SeekAsync(p.CurrentTime);
            MainWindow?.ShowInfo($"Fortschritt übernommen{(string.IsNullOrEmpty(remote.DeviceDescription) ? "" : " von " + remote.DeviceDescription)}: {Format.Time(p.CurrentTime)}");
        }
    }

    /// <summary>Lädt offline gehörte Sessions zum Server hoch (still; bei Fehlern beim nächsten Mal).</summary>
    public static async Task UploadOfflineSessionsAsync()
    {
        try
        {
            if (LocalSessions.Pending.Count == 0) return;
            var count = await LocalSessions.SyncAsync(Api, Settings.DeviceId);
            if (count > 0)
            {
                Log.Info($"{count} Offline-Session(s) hochgeladen.");
                Progress.Reset(await Api.GetAllProgressAsync());
            }
        }
        catch (Exception ex)
        {
            Log.Error("Offline-Sessions konnten nicht hochgeladen werden", ex);
        }
    }

    private static void OnActivationMessage(string message)
    {
        if (ProtocolRegistration.IsCallback(message)) OpenIdLogin.OnCallback(message);
        MainWindow?.BringToFront();
    }
}
