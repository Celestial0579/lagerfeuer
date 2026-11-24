using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed partial class EpisodeViewModel(string itemId, PodcastEpisode episode) : ObservableObject
{
    public PodcastEpisode Episode { get; } = episode;
    public string ItemId { get; } = itemId;
    public string Id => Episode.Id;
    public string Title => Episode.Title ?? "(ohne Titel)";

    [ObservableProperty] public partial string Info { get; set; } = "";
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool HasProgress { get; set; }
    public bool IsFinished { get; private set; }

    // ---------- Download der Folge (#76) ----------

    /// <summary>Vollständig heruntergeladen und angeheftet (aus dem Cache ermittelt, nicht vom Server).</summary>
    public bool IsDownloaded { get; private set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double DownloadPercent { get; set; }
    [ObservableProperty] public partial string DownloadGlyph { get; set; } = "\uE896";
    [ObservableProperty] public partial string DownloadTooltip { get; set; } = "Herunterladen";
    [ObservableProperty] public partial bool CanUseDownload { get; set; } = true;

    /// <summary>Knopf und Fortschritt nach dem Stand des Downloads bzw. des Caches.</summary>
    public void ApplyDownload(DownloadState? state, bool downloaded)
    {
        IsDownloaded = downloaded;
        IsDownloading = state is { IsActive: true };
        DownloadPercent = IsDownloading ? state!.Fraction * 100 : 0;
        if (IsDownloading)
        {
            DownloadGlyph = "\uE711"; // abbrechen
            DownloadTooltip = state!.Status == DownloadStatus.Queued ? "Wartet auf Download – abbrechen" : $"Wird heruntergeladen ({state.Fraction:P0}) – abbrechen";
        }
        else if (downloaded)
        {
            DownloadGlyph = "\uE73E"; // Haken
            DownloadTooltip = "Heruntergeladen – vom Gerät entfernen";
        }
        else
        {
            DownloadGlyph = "\uE896";
            DownloadTooltip = state is { Status: DownloadStatus.Failed } ? $"Fehlgeschlagen: {state.Error} – erneut versuchen"
                : App.CanDownload ? "Herunterladen" : "Download nicht erlaubt";
        }
        CanUseDownload = IsDownloading || downloaded || App.CanDownload;
        Refresh();
    }

    public void Refresh()
    {
        var p = App.Progress.Get(ItemId, Id);
        IsFinished = p?.IsFinished ?? false;
        ProgressPercent = IsFinished ? 100 : (p?.Progress ?? 0) * 100;
        HasProgress = ProgressPercent > 0 && !IsFinished;
        var parts = new List<string>();
        if (Episode.Published is { } date) parts.Add(date.ToLocalTime().ToString("d. MMMM yyyy", CultureInfo.GetCultureInfo("de-DE")));
        if (Episode.DisplayDuration > 0) parts.Add(Format.Duration(Episode.DisplayDuration));
        if (IsFinished) parts.Add("gehört");
        else if (p is { CurrentTime: > 0 } && Episode.DisplayDuration > 0) parts.Add($"noch {Format.Duration(Episode.DisplayDuration - p.CurrentTime)}");
        if (IsDownloaded) parts.Add("heruntergeladen");
        Info = string.Join(" · ", parts);
    }
}

/// <summary>Podcast mit seinen Folgen (neueste zuerst).</summary>
public sealed partial class PodcastPage : Page
{
    private string? _itemId;
    private string? _libraryId;
    private List<EpisodeViewModel> _episodes = [];

    public PodcastPage()
    {
        InitializeComponent();
        App.Progress.Changed += OnProgressChanged;
        App.Downloads.Changed += OnDownloadChanged;
        Unloaded += (_, _) =>
        {
            App.Progress.Changed -= OnProgressChanged;
            App.Downloads.Changed -= OnDownloadChanged;
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not string itemId) return;
        _itemId = itemId;
        try
        {
            var item = await App.Api.GetItemAsync(itemId);
            _libraryId = item.LibraryId;
            var meta = item.Media.Metadata;
            Cover.ItemId = item.Id;
            TitleText.Text = meta.Title ?? "";
            AuthorText.Text = meta.DisplayAuthor;
            DescriptionText.Text = Format.StripHtml(meta.Description);

            // Der Server liefert die Folgen unsortiert → neueste zuerst.
            _episodes = (item.Media.Episodes ?? [])
                .OrderByDescending(ep => ep.PublishedAt ?? 0)
                .Select(ep => new EpisodeViewModel(item.Id, ep))
                .ToList();
            foreach (var ep in _episodes) ep.Refresh();
            await RefreshDownloadsAsync();
            HideFinishedBox.IsChecked = App.Settings.HideFinishedEpisodes;
            ApplyFilter();
            ContentRoot.Visibility = Visibility.Visible;
            App.MainWindow.ClearError(MessageOrigins.Podcast);
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Podcast konnte nicht geladen werden: {ex.Message}", MessageOrigins.Podcast);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void ApplyFilter()
    {
        var hide = HideFinishedBox.IsChecked == true;
        var visible = hide ? _episodes.Where(e => !e.IsFinished).ToList() : _episodes;
        EpisodeList.ItemsSource = visible;
        EpisodesHeader.Text = $"{_episodes.Count} Folgen" + (hide && visible.Count != _episodes.Count ? $" ({visible.Count} ungehört)" : "");
    }

    private void OnHideFinishedChanged(object sender, RoutedEventArgs e)
    {
        App.Settings.HideFinishedEpisodes = HideFinishedBox.IsChecked == true;
        App.Settings.Save();
        ApplyFilter();
    }

    private void OnProgressChanged(object? sender, string key)
    {
        if (_itemId is null || !key.StartsWith(_itemId + "/")) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var ep in _episodes.Where(ep => key == ProgressStore.Key(ep.ItemId, ep.Id))) ep.Refresh();
        });
    }

    /// <summary>Welche Folgen dieses Podcasts heruntergeladen sind, aus dem Cache (ohne den Server zu fragen).</summary>
    private async Task RefreshDownloadsAsync()
    {
        if (_itemId is not { } itemId) return;
        var downloaded = await Task.Run(() => App.Cache.ListLocalEpisodes()
            .Where(e => e.LibraryItemId == itemId && App.Cache.IsEpisodePinned(itemId, e.EpisodeId!) && App.Cache.IsAvailableOffline(e))
            .Select(e => e.EpisodeId!)
            .ToHashSet(StringComparer.Ordinal));
        foreach (var ep in _episodes) ep.ApplyDownload(App.Downloads.Get(itemId, ep.Id), downloaded.Contains(ep.Id));
    }

    private void OnDownloadChanged(object? sender, DownloadState state)
    {
        if (state.ItemId != _itemId || state.EpisodeId is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (state.IsActive) _episodes.FirstOrDefault(e => e.Id == state.EpisodeId)?.ApplyDownload(state, false);
            else _ = RefreshDownloadsAsync(); // fertig, abgebrochen oder fehlgeschlagen: Stand aus dem Cache
        });
    }

    /// <summary>Herunterladen, abbrechen oder (nach Rückfrage) vom Gerät entfernen – je nach Stand der Folge (#76).</summary>
    private async void OnDownloadEpisode(object sender, RoutedEventArgs e)
    {
        if (_itemId is not { } itemId || ((FrameworkElement)sender).Tag is not string episodeId) return;
        if (_episodes.FirstOrDefault(x => x.Id == episodeId) is not { } ep) return;
        if (App.Downloads.Get(itemId, episodeId) is { IsActive: true })
        {
            App.Downloads.Cancel(itemId, episodeId);
            return;
        }
        if (ep.IsDownloaded)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Download entfernen?",
                Content = new TextBlock { Text = $"„{ep.Title}“ wird von diesem Gerät gelöscht. Dein Hörfortschritt bleibt erhalten.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Entfernen",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (App.Player.ItemId == itemId && App.Player.EpisodeId == episodeId) await App.Player.StopAsync();
            await Task.Run(() => App.Downloads.Remove(itemId, episodeId));
            await RefreshDownloadsAsync();
            return;
        }
        if (!App.CanDownload) return;
        ep.ApplyDownload(App.Downloads.Enqueue(itemId, ep.Title, episodeId), false);
    }

    private void OnEpisodeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodeViewModel ep) _ = App.Player.PlayItemAsync(ep.ItemId, episodeId: ep.Id);
    }

    private void OnPlayEpisode(object sender, RoutedEventArgs e)
    {
        if (_itemId is not null && ((FrameworkElement)sender).Tag is string episodeId)
            _ = App.Player.PlayItemAsync(_itemId, episodeId: episodeId);
    }

    private void OnAddToPlaylist(object sender, RoutedEventArgs e)
    {
        if (_itemId is not { } itemId || _libraryId is not { } libraryId || ((FrameworkElement)sender).Tag is not string episodeId) return;
        PlaylistUi.CreateAddFlyout(() => (libraryId, itemId, episodeId), XamlRoot).ShowAt((FrameworkElement)sender);
    }
}
