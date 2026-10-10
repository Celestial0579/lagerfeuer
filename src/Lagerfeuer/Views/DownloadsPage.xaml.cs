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
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Author { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial bool CanPlay { get; set; }
    [ObservableProperty] public partial string RemoveGlyph { get; set; } = "";
    [ObservableProperty] public partial string RemoveTooltip { get; set; } = "Download entfernen";
}

/// <summary>Heruntergeladene und vollständig zwischengespeicherte Bücher – auch offline nutzbar.</summary>
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
            var vm = _items.FirstOrDefault(i => i.ItemId == state.ItemId);
            if (vm is null || state.Status is DownloadStatus.Completed or DownloadStatus.Canceled) _ = RefreshAsync();
            else Apply(vm, state);
        });

    private async Task RefreshAsync()
    {
        var (locals, sizes) = await Task.Run(() =>
        {
            var items = App.Cache.ListLocalItems()
                .Select(i => (Item: i, Pinned: App.Cache.IsPinned(i.LibraryItemId), Offline: App.Cache.IsAvailableOffline(i)))
                .Where(x => x.Pinned || x.Offline)
                .ToList();
            var s = items.ToDictionary(x => x.Item.LibraryItemId, x => App.Cache.GetItemSize(x.Item.LibraryItemId));
            return (items, s);
        });

        _items.Clear();
        foreach (var (item, pinned, offline) in locals)
        {
            var vm = new DownloadItemViewModel
            {
                ItemId = item.LibraryItemId,
                Title = item.Title,
                Author = item.Author ?? "",
                CanPlay = offline || !App.IsOffline,
                StatusText = (offline ? (pinned ? "Heruntergeladen" : "Vollständig zwischengespeichert") : "Unvollständig")
                             + $" · {sizes[item.LibraryItemId] / 1024d / 1024:0} MB · {Format.Duration(item.Duration)}",
            };
            if (!pinned)
            {
                vm.RemoveGlyph = ""; // Anheften
                vm.RemoveTooltip = "Dauerhaft behalten (vor Bereinigung schützen)";
            }
            if (App.Downloads.Get(item.LibraryItemId) is { IsActive: true } state) Apply(vm, state);
            _items.Add(vm);
        }

        // Laufende Downloads, für die noch keine lokalen Daten existieren
        foreach (var state in App.Downloads.All.Where(s => s.IsActive && _items.All(i => i.ItemId != s.ItemId)))
        {
            var vm = new DownloadItemViewModel { ItemId = state.ItemId, Title = state.Title };
            Apply(vm, state);
            _items.Add(vm);
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var total = sizes.Values.Sum();
        SummaryText.Text = _items.Count == 0 ? ""
            : $"{_items.Count} Bücher · {total / 1024d / 1024 / 1024:0.0} GB auf diesem Gerät" +
              (App.IsOffline ? " · Offline – nur diese Bücher sind verfügbar." : "");
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
        if (!App.IsOffline && e.ClickedItem is DownloadItemViewModel vm) App.MainWindow.OpenItem(vm.ItemId);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id) _ = App.Player.PlayItemAsync(id);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id) return;
        var vm = _items.FirstOrDefault(i => i.ItemId == id);

        if (App.Downloads.Get(id) is { IsActive: true })
        {
            App.Downloads.Cancel(id);
            return;
        }
        if (!App.Cache.IsPinned(id))
        {
            App.Cache.SetPinned(id, true); // zwischengespeichertes Buch dauerhaft behalten
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
        if (App.Player.ItemId == id) await App.Player.StopAsync();
        await Task.Run(() => App.Downloads.Remove(id));
        await RefreshAsync();
    }
}
