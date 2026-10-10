using System.Diagnostics;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        CacheLimitBox.Value = App.Settings.CacheLimitGb;
        AutoUpdateSwitch.IsOn = App.Settings.AutoCheckUpdates;
        PauseOnCallsSwitch.IsOn = App.Settings.PauseOnCalls;
        ResumeAfterCallsBox.IsChecked = App.Settings.ResumeAfterCalls;
        ResumeAfterCallsBox.IsEnabled = App.Settings.PauseOnCalls;
        _loading = false;

        AccountText.Text = $"{App.Settings.Username} auf {App.Settings.ServerUrl}";
        InfoText.Text = $"Lagerfeuer {UpdateService.CurrentVersion.ToString(3)} ({(UpdateService.IsPortable ? "portabel" : "installiert")}) · " +
                        $"{UpdateService.RepositoryUrl} · Lizenz: GPL-3.0 · Daten: {AppPaths.Root}";
        _ = UpdateCacheSizeAsync();
        if (!App.IsOffline) _ = LoadSessionsAsync();
    }

    // ---------- Angemeldete Geräte ----------

    private async Task LoadSessionsAsync()
    {
        try
        {
            var sessions = await App.Api.GetAuthSessionsAsync();
            SessionList.Items.Clear();
            foreach (var s in sessions.OrderByDescending(s => s.Current).ThenByDescending(s => s.UpdatedAt))
                SessionList.Items.Add(CreateSessionRow(s));
            if (sessions.Count == 0) SessionList.Items.Add(new TextBlock { Text = "Keine Sitzungen gefunden." });
        }
        catch (Exception ex)
        {
            SessionList.Items.Clear();
            SessionList.Items.Add(new TextBlock { Text = $"Geräte konnten nicht geladen werden: {ex.Message}", TextWrapping = TextWrapping.Wrap });
        }
    }

    private FrameworkElement CreateSessionRow(Models.AuthSession session)
    {
        var lastActive = DateTimeOffset.FromUnixTimeMilliseconds(session.UpdatedAt).ToLocalTime();
        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = session.DisplayName + (session.Current ? " (dieses Gerät)" : ""),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"Zuletzt aktiv {lastActive:dd.MM.yyyy HH:mm}" + (string.IsNullOrEmpty(session.IpAddress) ? "" : $" · {session.IpAddress}"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var row = new Grid { Padding = new Thickness(0, 6, 0, 6), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(info);
        if (!session.Current)
        {
            var button = new Button { Content = "Abmelden", VerticalAlignment = VerticalAlignment.Center };
            button.Click += async (_, _) =>
            {
                try
                {
                    await App.Api.DeleteAuthSessionAsync(session.Id);
                    await LoadSessionsAsync();
                }
                catch (Exception ex)
                {
                    App.MainWindow.ShowError($"Gerät konnte nicht abgemeldet werden: {ex.Message}");
                }
            };
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
        }
        return row;
    }

    private void OnRefreshSessions(object sender, RoutedEventArgs e) => _ = LoadSessionsAsync();

    private async void OnLogoutAllDevices(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Auf allen Geräten abmelden?",
            Content = "Alle Geräte und Browser – auch dieses – werden abgemeldet und müssen sich neu anmelden.",
            PrimaryButtonText = "Überall abmelden",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await App.MainWindow.LogoutAsync(allDevices: true);
    }

    private async Task UpdateCacheSizeAsync()
    {
        CacheSizeText.Text = "Belegt: …";
        var bytes = await Task.Run(App.Cache.GetTotalSize);
        CacheSizeText.Text = $"Belegt: {bytes / 1024d / 1024 / 1024:0.00} GB";
    }

    private void OnCacheLimitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        App.Settings.CacheLimitGb = (int)Math.Round(args.NewValue);
        App.Settings.Save();
    }

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        var protectedIds = App.Player.ItemId is { } id ? new[] { id } : [];
        await Task.Run(() => App.Cache.Clear(protectedIds));
        await UpdateCacheSizeAsync();
    }

    private async void OnPauseOnCallsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.PauseOnCalls = PauseOnCallsSwitch.IsOn;
        App.Settings.Save();
        ResumeAfterCallsBox.IsEnabled = PauseOnCallsSwitch.IsOn;
        if (PauseOnCallsSwitch.IsOn)
        {
            if (!await App.Calls.StartAsync())
                App.MainWindow.ShowError("Die Anruferkennung konnte nicht gestartet werden (Details im Log).");
        }
        else App.Calls.Stop();
    }

    private void OnResumeAfterCallsClicked(object sender, RoutedEventArgs e)
    {
        App.Settings.ResumeAfterCalls = ResumeAfterCallsBox.IsChecked == true;
        App.Settings.Save();
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoCheckUpdates = AutoUpdateSwitch.IsOn;
        App.Settings.Save();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Suche nach Updates …";
        UpdateStatusText.Text = await App.MainWindow.CheckForUpdatesAsync(manual: true);
        CheckUpdateButton.IsEnabled = true;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Cache);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Cache) { UseShellExecute = true });
    }
}
