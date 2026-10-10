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
        AppWindow.Resize(new SizeInt32(1280, 860));
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
        // Kurz nach dem Start und danach alle 12 Stunden.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(8);
        timer.Tick += (t, _) =>
        {
            t.Interval = TimeSpan.FromHours(12);
            if (App.Settings.AutoCheckUpdates) _ = CheckForUpdatesAsync(manual: false);
        };
        timer.Start();
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
        _availableUpdate = update;
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
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateBar.Message = "Update wird heruntergeladen …";
        try
        {
            var progress = new Progress<double>(p => UpdateProgress.Value = p);
            var file = await _updates.DownloadAsync(asset, progress);

            UpdateBar.Message = "Update wird installiert – Lagerfeuer startet gleich neu …";
            await Player.ShutdownAsync();
            _updates.LaunchInstaller(file);
            _shutdownDone = true;
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Log.Error("Update fehlgeschlagen", ex);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.Message = $"Update fehlgeschlagen: {ex.Message}";
            UpdateProgress.Visibility = Visibility.Collapsed;
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
        foreach (var lib in libraries.Where(l => l.MediaType == "book"))
        {
            var item = new NavigationViewItem
            {
                Content = lib.Name,
                Tag = lib,
                Icon = new FontIcon { Glyph = "" },
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
    }

    private async Task LogoutAsync()
    {
        await Player.StopAsync();
        await App.Api.LogoutAsync();
        CredentialStore.Clear();
        ShowLogin(null);
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

    public void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerService.ErrorMessage) && Player.ErrorMessage is { } msg)
            ShowError(msg);
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

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownDone) return;
        args.Cancel = true;
        try { await Player.ShutdownAsync(); }
        catch (Exception ex) { Log.Error("Fehler beim Beenden", ex); }
        _shutdownDone = true;
        Close();
    }
}
