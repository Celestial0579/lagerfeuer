using System.ComponentModel;
using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Lagerfeuer.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Windows.System;

namespace Lagerfeuer;

public sealed partial class MainWindow : Window
{
    private readonly List<NavigationViewItem> _libraryItems = [];
    private bool _shutdownDone;

    public PlayerService Player => App.Player;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        PlaceWindow();
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Closing += OnAppWindowClosing;

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        App.Api.SessionExpired += (_, _) => DispatcherQueue.TryEnqueue(() =>
            ShowLogin("Die Anmeldung ist abgelaufen. Bitte erneut anmelden."));
        Player.PropertyChanged += OnPlayerPropertyChanged;

        _ = StartAsync();
        StartUpdateChecks();
        _taskbar = new TaskbarIntegration(WinRT.Interop.WindowNative.GetWindowHandle(this), Player, DispatcherQueue);
        Closed += (_, _) => { _taskbar.Dispose(); _miniPlayer?.Close(); };
    }

    private readonly TaskbarIntegration _taskbar;
    private MiniPlayerWindow? _miniPlayer;

    // ---------- Größe und Lage des Fensters ----------

    /// <summary>Skalierung des Bildschirms, auf dem das Fenster liegt (1.0 bei 100 %).</summary>
    private double WindowScale()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>
    /// Beim Start: die zuletzt gewählte Größe (sonst 1280 × 860 bei 100 %), nie größer als der Arbeitsbereich
    /// des Bildschirms, mittig darin. War das Fenster zuletzt maximiert, ist es das wieder.
    /// </summary>
    private void PlaceWindow()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var rect = WindowPlacement.Initial(new WindowRect(area.X, area.Y, area.Width, area.Height), WindowScale(),
                App.Settings.WindowWidth, App.Settings.WindowHeight);
            AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
            if (App.Settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        catch (Exception ex)
        {
            // An der Fenstergröße soll der Start nicht scheitern.
            Log.Error("Fenstergröße konnte nicht gesetzt werden", ex);
            AppWindow.Resize(new SizeInt32(WindowPlacement.DefaultWidth, WindowPlacement.DefaultHeight));
        }
    }

    /// <summary>
    /// Merkt sich Größe und „maximiert“ in den Einstellungen. Gespeichert werden sie beim Beenden mit allen
    /// übrigen. Die Größe eines maximierten oder minimierten Fensters wird nicht übernommen.
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPresenterChange) return;
        if (sender.Presenter is not OverlappedPresenter presenter) return;
        switch (presenter.State)
        {
            case OverlappedPresenterState.Maximized:
                App.Settings.WindowMaximized = true;
                break;
            case OverlappedPresenterState.Restored:
                App.Settings.WindowMaximized = false;
                (App.Settings.WindowWidth, App.Settings.WindowHeight) =
                    WindowPlacement.ToSaved(sender.Size.Width, sender.Size.Height, WindowScale());
                break;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>Öffnet den kompakten Mini-Player (immer im Vordergrund).</summary>
    public void ShowMiniPlayer()
    {
        if (_miniPlayer is null)
        {
            _miniPlayer = new MiniPlayerWindow();
            _miniPlayer.Closed += (_, _) => _miniPlayer = null;
        }
        _miniPlayer.Activate();
    }

    // ---------- Updates (GitHub-Releases) ----------

    private readonly UpdateService _updates = new();
    private UpdateInfo? _availableUpdate;
    private bool _installingUpdate;

    private void StartUpdateChecks()
    {
        // Kurz nach dem Start und danach alle 12 Stunden. Zeiten und Ablauf stehen in UpdateCheckSchedule.
        _ = UpdateCheckSchedule.RunAsync(
            () => App.Settings.AutoCheckUpdates ? CheckForUpdatesAsync(manual: false) : Task.CompletedTask);
    }

    /// <summary>Prüft auf Updates; liefert einen Statustext für die Einstellungsseite.</summary>
    public async Task<string> CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var update = await _updates.CheckAsync();
            if (update is null)
                return $"Lagerfeuer {UpdateService.CurrentVersion.ToString(3)} ist aktuell.";
            if (!manual && App.Settings.SkippedUpdateVersion == update.Version.ToString(3))
                return $"Version {update.Version.ToString(3)} ist verfügbar (übersprungen).";

            ShowUpdate(update);
            return $"Version {update.Version.ToString(3)} ist verfügbar.";
        }
        catch (Exception ex)
        {
            Log.Error("Update-Prüfung fehlgeschlagen", ex);
            return $"Update-Prüfung fehlgeschlagen: {ex.Message}";
        }
    }

    private void ShowUpdate(UpdateInfo update)
    {
        // Läuft gerade ein Update, bleibt dessen Anzeige (Meldung, Fortschritt) stehen.
        if (_installingUpdate) return;
        _availableUpdate = update;
        // Ein früherer Fehlschlag bleibt an einem neuen Hinweis nicht hängen.
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateButton.Content = "Jetzt aktualisieren";
        UpdateBar.Title = $"Lagerfeuer {update.Version.ToString(3)} ist verfügbar";
        UpdateBar.Message = $"Du verwendest Version {UpdateService.CurrentVersion.ToString(3)}.";
        ReleaseNotesLink.NavigateUri = new Uri(update.ReleaseUrl);
        // Ohne passende Datei im Release bleibt nur der Link zur Release-Seite (wie bei Lodestone).
        UpdateButton.Visibility = update.Asset is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateBar.IsOpen = true;
    }

    private void OnSkipUpdate(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        App.Settings.SkippedUpdateVersion = _availableUpdate.Version.ToString(3);
        App.Settings.Save();
        UpdateBar.IsOpen = false;
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate?.Asset is not { } asset || _installingUpdate) return;
        _installingUpdate = true;
        UpdateButton.IsEnabled = SkipUpdateLink.IsEnabled = false;
        // Ein neuer Versuch beginnt ohne den Fehler vom letzten.
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateButton.Content = "Jetzt aktualisieren";
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateBar.Message = "Update wird heruntergeladen …";
        try
        {
            var progress = new Progress<double>(p => UpdateProgress.Value = p);
            var file = await _updates.DownloadAsync(asset, progress);

            UpdateBar.Message = "Update wird installiert – Lagerfeuer startet gleich neu …";
            // Erst starten, dann herunterfahren: Scheitert der Start, läuft die App samt Wiedergabe weiter.
            // Der Start läuft im Hintergrund (bei einer frisch geladenen EXE dauert er viele Sekunden);
            // das Fenster bleibt so lange bedienbar, die Knöpfe der Leiste sind gesperrt, und schließen lässt
            // es sich in dieser Zeit nicht (_closeGate).
            await UpdateInstallation.RunAsync(
                launch: () => _updates.LaunchInstaller(file),
                shutdownAsync: Player.ShutdownAsync,
                exit: () =>
                {
                    _shutdownDone = true;
                    Application.Current.Exit();
                },
                gate: _closeGate);
        }
        catch (Exception ex)
        {
            Log.Error("Update fehlgeschlagen", ex);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.Message = $"Update fehlgeschlagen: {ex.Message}";
            UpdateProgress.Visibility = Visibility.Collapsed;
            // Nichts ist heruntergefahren: Der Versuch lässt sich ohne Neustart wiederholen.
            UpdateButton.Content = "Erneut versuchen";
            UpdateButton.IsEnabled = SkipUpdateLink.IsEnabled = true;
            _installingUpdate = false;
        }
    }

    // ---------- Anmeldung / Start ----------

    private async Task StartAsync()
    {
        var tokens = CredentialStore.Load();
        if (App.Settings.ServerUrl is null || tokens is null || tokens.Server != App.Settings.ServerUrl)
        {
            ShowLogin(null);
            return;
        }

        App.Api.Configure(new Uri(tokens.Server), tokens.AccessToken, tokens.RefreshToken);
        try
        {
            var login = await App.Api.AuthorizeAsync();
            await OnLoggedInAsync(login);
        }
        catch (AbsApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            ShowLogin("Die Anmeldung ist abgelaufen. Bitte erneut anmelden.");
        }
        catch (Exception ex)
        {
            Log.Error("Automatische Anmeldung fehlgeschlagen – starte offline", ex);
            EnterOfflineMode();
        }
    }

    // ---------- Offline-Modus ----------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reconnectTimer;

    /// <summary>Server nicht erreichbar: Downloads stehen weiter zur Verfügung, Verbindung wird regelmäßig geprüft.</summary>
    private void EnterOfflineMode()
    {
        App.IsOffline = true;
        Player.PreferOffline = true;
        OfflineBar.IsOpen = true;

        LoginFrame.Content = null;
        LoginFrame.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;
        foreach (var item in _libraryItems) item.IsEnabled = false;
        ((NavigationViewItem)Nav.MenuItems[0]).IsEnabled = false;

        Nav.SelectedItem = DownloadsItem;
        ContentFrame.BackStack.Clear();
        ContentFrame.Navigate(typeof(DownloadsPage), null, new SuppressNavigationTransitionInfo());

        _reconnectTimer ??= CreateReconnectTimer();
        _reconnectTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateReconnectTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(30);
        timer.Tick += (_, _) => _ = TryReconnectAsync(manual: false);
        return timer;
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => _ = TryReconnectAsync(manual: true);

    private bool _reconnecting;

    private async Task TryReconnectAsync(bool manual)
    {
        if (!App.IsOffline || _reconnecting) return;
        _reconnecting = true;
        ReconnectButton.IsEnabled = false;
        try
        {
            var login = await App.Api.AuthorizeAsync();
            Log.Info("Server wieder erreichbar.");
            await OnLoggedInAsync(login);
        }
        catch (AbsApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            LeaveOfflineMode();
            ShowLogin("Die Anmeldung ist abgelaufen. Bitte erneut anmelden.");
        }
        catch (Exception ex)
        {
            if (manual) OfflineBar.Message = $"Weiterhin offline: {ex.Message}";
        }
        finally
        {
            _reconnecting = false;
            ReconnectButton.IsEnabled = true;
        }
    }

    private void LeaveOfflineMode()
    {
        App.IsOffline = false;
        Player.PreferOffline = false;
        OfflineBar.IsOpen = false;
        _reconnectTimer?.Stop();
        ((NavigationViewItem)Nav.MenuItems[0]).IsEnabled = true;
    }

    public void RetryStart() => _ = StartAsync();

    public void ShowLogin(string? message, bool canRetry = false)
    {
        Nav.Visibility = Visibility.Collapsed;
        LoginFrame.Visibility = Visibility.Visible;
        LoginFrame.Navigate(typeof(LoginPage), new LoginPageArgs(message, canRetry));
    }

    public async Task OnLoggedInAsync(LoginResponse login)
    {
        if (App.IsOffline) LeaveOfflineMode();
        App.User = login.User;
        App.LiveSync.Start();
        App.Progress.Reset(login.User.MediaProgress);
        _ = App.UploadOfflineSessionsAsync();
        App.Settings.ServerUrl = App.Api.ServerUri!.ToString();
        App.Settings.Username = login.User.Username;
        App.Settings.Save();

        List<Library> libraries;
        try
        {
            libraries = await App.Api.GetLibrariesAsync();
        }
        catch (Exception ex)
        {
            ShowLogin($"Bibliotheken konnten nicht geladen werden: {ex.Message}", canRetry: true);
            return;
        }

        foreach (var old in _libraryItems) Nav.MenuItems.Remove(old);
        _libraryItems.Clear();
        foreach (var lib in libraries.Where(l => l.MediaType is "book" or "podcast"))
        {
            var item = new NavigationViewItem
            {
                Content = lib.Name,
                Tag = lib,
                Icon = new FontIcon { Glyph = lib.MediaType == "podcast" ? "\uE720" : "\uE8F1" },
            };
            _libraryItems.Add(item);
            Nav.MenuItems.Add(item);
        }

        LogoutItem.Content = $"Abmelden ({login.User.Username})";
        LoginFrame.Content = null;
        LoginFrame.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;

        ContentFrame.BackStack.Clear();
        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
        FocusNavigation();
    }

    /// <summary>
    /// Setzt den Tastaturfokus auf den gewählten Eintrag der Navigation.
    ///
    /// Nach dem Anmelden lag der Fokus auf der Anmeldeseite. Verschwindet sie, sucht sich das Fenster das erste
    /// fokussierbare Element. Das war der Knopf „Jetzt aktualisieren“, wenn gerade ein Update angeboten wird:
    /// Leertaste oder Eingabetaste hätten dann das Update gestartet statt die Wiedergabe.
    /// </summary>
    private void FocusNavigation()
        => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (Nav.SelectedItem is NavigationViewItem item) item.Focus(FocusState.Programmatic);
        });

    public async Task LogoutAsync(bool allDevices = false)
    {
        await Player.StopAsync();
        try
        {
            await App.Api.LogoutAsync(allDevices);
        }
        catch (Exception ex)
        {
            ShowError($"Abmelden auf allen Geräten fehlgeschlagen: {ex.Message}");
        }
        CredentialStore.Clear();
        App.User = null;
        ShowLogin(allDevices ? "Du wurdest auf allen Geräten abgemeldet." : null);
    }

    // ---------- Navigation ----------

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item) return;
        switch (item.Tag)
        {
            case "home":
                ContentFrame.Navigate(typeof(HomePage), null, args.RecommendedNavigationTransitionInfo);
                break;
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage), null, args.RecommendedNavigationTransitionInfo);
                break;
            case "downloads":
                ContentFrame.Navigate(typeof(DownloadsPage), null, args.RecommendedNavigationTransitionInfo);
                break;
            case "logout":
                _ = LogoutAsync();
                break;
            case Library lib:
                App.Settings.LastLibraryId = lib.Id;
                ContentFrame.Navigate(typeof(LibraryPage), lib, args.RecommendedNavigationTransitionInfo);
                break;
        }
    }

    public void OpenItem(string itemId) => ContentFrame.Navigate(typeof(BookPage), itemId);

    /// <summary>Öffnet ein Buch bzw. einen Podcast auf der passenden Seite.</summary>
    public void OpenItem(Models.LibraryItem item)
    {
        if (item.IsPodcast) ContentFrame.Navigate(typeof(PodcastPage), item.Id);
        else OpenItem(item.Id);
    }

    public void OpenAuthor(string authorId) => ContentFrame.Navigate(typeof(AuthorPage), authorId);

    public void OpenSeries(string libraryId, string seriesId, string name)
        => ContentFrame.Navigate(typeof(SeriesPage), new SeriesPageArgs(libraryId, seriesId, name));

    private void OnOpenItemRequested(object? sender, string itemId) => OpenItem(itemId);

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnContentNavigated(object sender, NavigationEventArgs e)
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
        Nav.SelectedItem = e.SourcePageType switch
        {
            var t when t == typeof(HomePage) => Nav.MenuItems[0],
            var t when t == typeof(SettingsPage) => SettingsItem,
            var t when t == typeof(DownloadsPage) => DownloadsItem,
            var t when t == typeof(LibraryPage) => _libraryItems.FirstOrDefault(i => ReferenceEquals(i.Tag, e.Parameter)),
            _ => Nav.SelectedItem,
        };
    }

    // ---------- Fehler, Tastatur, Beenden ----------

    /// <summary>Holt das Fenster nach vorn (z.B. nach der SSO-Anmeldung im Browser).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    /// <summary>Kurzer Hinweis, der sich nach einigen Sekunden selbst schließt.</summary>
    public void ShowInfo(string message)
    {
        ErrorBar.Severity = InfoBarSeverity.Informational;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (ErrorBar.Message == message) ErrorBar.IsOpen = false; };
        timer.Start();
    }

    public void ShowError(string message)
    {
        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private string? _playerErrorShown;

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerService.ErrorMessage)) return;
        if (Player.ErrorMessage is { } msg)
        {
            _playerErrorShown = msg;
            ShowError(msg);
        }
        else if (_playerErrorShown is { } shown)
        {
            // Der Player hat seinen Fehler zurückgenommen (neuer Versuch): Die Leiste zeigt ihn nicht weiter.
            // Eine andere Meldung, die inzwischen dort steht, bleibt.
            _playerErrorShown = null;
            if (ErrorBar.IsOpen && ErrorBar.Message == shown) ErrorBar.IsOpen = false;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!Player.HasSession || e.Handled) return;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or PasswordBox or AutoSuggestBox or ButtonBase or Slider) return;

        switch (e.Key)
        {
            case VirtualKey.Space:
                Player.TogglePlayPause();
                e.Handled = true;
                break;
            case VirtualKey.Left:
                _ = Player.SkipAsync(-10);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                _ = Player.SkipAsync(30);
                e.Handled = true;
                break;
        }
    }

    private readonly UpdateCloseGate _closeGate = new();

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownDone) return;
        args.Cancel = true;
        // Startet gerade ein Update, bleibt das Fenster offen: Der Prozess darf nicht mitten im Tausch der EXE
        // enden, und die App beendet sich danach selbst. Ein zweiter Klick, während das Herunterfahren läuft,
        // stößt nichts Neues an: Der erste schließt.
        if (_closeGate.RequestClose() != CloseRequest.Proceed) return;
        try { await Player.ShutdownAsync(); }
        catch (Exception ex) { Log.Error("Fehler beim Beenden", ex); }
        _shutdownDone = true;
        Close();
    }
}
