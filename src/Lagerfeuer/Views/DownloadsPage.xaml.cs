using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed partial class DownloadItemViewModel : ObservableObject
{
    public required string ItemId { get; init; }
    /// <summary>Bei einer Podcast-Folge (#76) die Folge; ItemId ist dann der Podcast.</summary>
    public string? EpisodeId { get; init; }
    public string Key => DownloadManager.Key(ItemId, EpisodeId);
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Author { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial bool CanPlay { get; set; }
    [ObservableProperty] public partial string RemoveGlyph { get; set; } = "";
    [ObservableProperty] public partial string RemoveTooltip { get; set; } = "Download entfernen";
}

/// <summary>Heruntergeladene und vollständig zwischengespeicherte Bücher und Podcast-Folgen – auch offline nutzbar.</summary>
public sealed partial class DownloadsPage : Page
{
    private readonly ObservableCollection<DownloadItemViewModel> _items = [];

    public DownloadsPage()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        App.Downloads.Changed += OnDownloadChanged;
        Unloaded += (_, _) => App.Downloads.Changed -= OnDownloadChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = RefreshAsync();

    private void OnDownloadChanged(object? sender, DownloadState state)
        => DispatcherQueue.TryEnqueue(() =>
        {
            var vm = _items.FirstOrDefault(i => i.Key == state.Key);
            if (vm is null || state.Status is DownloadStatus.Completed or DownloadStatus.Canceled) _ = RefreshAsync();
            else Apply(vm, state);
        });

    private async Task RefreshAsync()
    {
        var (locals, sizes) = await Task.Run(() =>
        {
            // Bücher (item.json) und heruntergeladene Podcast-Folgen (#76) aus dem Cache.
            var items = App.Cache.ListLocalItems()
                .Select(i => (Item: i, Pinned: App.Cache.IsPinned(i.LibraryItemId), Offline: App.Cache.IsAvailableOffline(i)))
                .Concat(App.Cache.ListLocalEpisodes()
                    .Select(e => (Item: e, Pinned: App.Cache.IsEpisodePinned(e.LibraryItemId, e.EpisodeId!), Offline: App.Cache.IsAvailableOffline(e))))
                .Where(x => x.Pinned || x.Offline)
                .ToList();
            var s = items.ToDictionary(x => DownloadManager.Key(x.Item.LibraryItemId, x.Item.EpisodeId),
                x => x.Item.EpisodeId is null ? App.Cache.GetItemSize(x.Item.LibraryItemId) : App.Cache.GetEpisodeSize(x.Item));
            return (items, s);
        });

        _items.Clear();
        foreach (var (item, pinned, offline) in locals)
        {
            var vm = new DownloadItemViewModel
            {
                ItemId = item.LibraryItemId,
                EpisodeId = item.EpisodeId,
                Title = item.Title,
                Author = item.Author ?? "",
                CanPlay = offline || !App.IsOffline,
                StatusText = (offline ? (pinned ? "Heruntergeladen" : "Vollständig zwischengespeichert") : "Unvollständig")
                             + $" · {Format.Size(sizes[DownloadManager.Key(item.LibraryItemId, item.EpisodeId)])} · {Format.Duration(item.Duration)}",
            };
            if (!pinned)
            {
                vm.RemoveGlyph = "\uE718"; // Anheften
                vm.RemoveTooltip = "Dauerhaft behalten (vor Bereinigung schützen)";
            }
            if (App.Downloads.Get(item.LibraryItemId, item.EpisodeId) is { IsActive: true } state) Apply(vm, state);
            _items.Add(vm);
        }

        // Laufende Downloads, für die noch keine lokalen Daten existieren
        foreach (var state in App.Downloads.All.Where(s => s.IsActive && _items.All(i => i.Key != s.Key)))
        {
            var vm = new DownloadItemViewModel { ItemId = state.ItemId, EpisodeId = state.EpisodeId, Title = state.Title };
            Apply(vm, state);
            _items.Add(vm);
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var total = sizes.Values.Sum();
        var episodes = _items.Count(i => i.EpisodeId is not null);
        var books = _items.Count - episodes;
        var what = episodes == 0 ? Format.Count(books, "Buch", "Bücher")
            : books == 0 ? Format.Count(episodes, "Folge", "Folgen")
            : $"{Format.Count(books, "Buch", "Bücher")}, {Format.Count(episodes, "Folge", "Folgen")}";
        SummaryText.Text = _items.Count == 0 ? ""
            : $"{what} · {Format.Size(total)} auf diesem Gerät" +
              (App.IsOffline ? " · Offline – nur diese sind verfügbar." : "");
    }

    private static void Apply(DownloadItemViewModel vm, DownloadState state)
    {
        if (!string.IsNullOrEmpty(state.Title)) vm.Title = state.Title;
        vm.IsDownloading = state.IsActive;
        vm.Fraction = state.Fraction;
        vm.StatusText = state.Status switch
        {
            DownloadStatus.Queued => "Wartet auf Download …",
            DownloadStatus.Downloading => $"Wird heruntergeladen … {state.Fraction:P0}",
            DownloadStatus.Failed => $"Fehlgeschlagen: {state.Error}",
            _ => vm.StatusText,
        };
        if (state.IsActive)
        {
            vm.CanPlay = !App.IsOffline;
            vm.RemoveGlyph = "";
            vm.RemoveTooltip = "Download abbrechen";
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (App.IsOffline || e.ClickedItem is not DownloadItemViewModel vm) return;
        if (vm.EpisodeId is null) App.MainWindow.OpenItem(vm.ItemId);
        else App.MainWindow.OpenPodcast(vm.ItemId);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string key) return;
        var (itemId, episodeId) = DownloadManager.ParseKey(key);
        _ = App.Player.PlayItemAsync(itemId, episodeId: episodeId);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string key) return;
        var (id, episodeId) = DownloadManager.ParseKey(key);
        var vm = _items.FirstOrDefault(i => i.Key == key);

        if (App.Downloads.Get(id, episodeId) is { IsActive: true })
        {
            App.Downloads.Cancel(id, episodeId);
            return;
        }
        var pinned = episodeId is null ? App.Cache.IsPinned(id) : App.Cache.IsEpisodePinned(id, episodeId);
        if (!pinned)
        {
            // zwischengespeichertes Buch bzw. Folge dauerhaft behalten
            if (episodeId is null) App.Cache.SetPinned(id, true);
            else App.Cache.SetEpisodePinned(id, episodeId, true);
            await RefreshAsync();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Download entfernen?",
            Content = $"„{vm?.Title}“ wird von diesem Gerät gelöscht. Dein Hörfortschritt bleibt erhalten.",
            PrimaryButtonText = "Entfernen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (App.Player.ItemId == id && (episodeId is null || App.Player.EpisodeId == episodeId)) await App.Player.StopAsync();
        if (await Task.Run(() => App.Downloads.Remove(id, episodeId)) is { } reason)
            App.MainWindow.ShowError(DownloadManager.RemoveFailedMessage(vm?.Title, reason));
        await RefreshAsync();
    }
}
