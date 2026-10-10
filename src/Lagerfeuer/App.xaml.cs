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
    public static ProgressStore Progress { get; } = new();
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

        Player = new PlayerService(Api, Cache, Covers, Settings, dispatcher);
        Player.ProgressSynced += (_, p) => Progress.Update(p.ItemId, p.CurrentTime, p.Duration);
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static void OnActivationMessage(string message)
    {
        if (ProtocolRegistration.IsCallback(message)) OpenIdLogin.OnCallback(message);
        MainWindow?.BringToFront();
    }
}

/// <summary>Hält den Hörfortschritt des Benutzers für Fortschrittsbalken in der Bibliothek.</summary>
public sealed class ProgressStore
{
    private readonly Dictionary<string, MediaProgress> _byItem = new();

    public event EventHandler<string>? Changed;

    public void Reset(IEnumerable<MediaProgress> progress)
    {
        _byItem.Clear();
        foreach (var p in progress.Where(p => p.EpisodeId is null)) _byItem[p.LibraryItemId] = p;
    }

    public MediaProgress? Get(string itemId) => _byItem.GetValueOrDefault(itemId);

    public void Set(MediaProgress progress)
    {
        _byItem[progress.LibraryItemId] = progress;
        Changed?.Invoke(this, progress.LibraryItemId);
    }

    public void Update(string itemId, double currentTime, double duration)
    {
        var p = _byItem.GetValueOrDefault(itemId) ?? new MediaProgress { LibraryItemId = itemId };
        p.CurrentTime = currentTime;
        p.Duration = duration;
        p.Progress = duration > 0 ? Math.Clamp(currentTime / duration, 0, 1) : 0;
        if (duration > 0 && currentTime >= duration - 1) p.IsFinished = true;
        _byItem[itemId] = p;
        Changed?.Invoke(this, itemId);
    }
}
