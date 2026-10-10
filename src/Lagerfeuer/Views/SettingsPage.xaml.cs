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
