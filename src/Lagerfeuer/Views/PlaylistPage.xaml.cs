using System.Collections.ObjectModel;
using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

/// <summary>Ein Eintrag der Detailseite. Alle Texte sind bereinigt (<see cref="PlaylistRules"/>).</summary>
public sealed class PlaylistEntry(PlaylistItem item)
{
    public PlaylistItem Item { get; } = item;
    public string Title { get; } = PlaylistRules.DisplayTitle(item);
    public string Subtitle { get; } = PlaylistRules.DisplaySubtitle(item);
    public bool IsAvailable { get; } = PlaylistRules.IsAvailable(item);
    public double TextOpacity => IsAvailable ? 1 : 0.5;
    /// <summary>Cover nur für schlichte IDs; das Cover einer Folge ist das ihres Podcasts.</summary>
    public string? CoverId => PlaylistRules.IsPlainId(Item.LibraryItemId) ? Item.LibraryItemId : null;
}

/// <summary>Eine Wiedergabeliste: abspielen, umbenennen, ordnen, Einträge entfernen, löschen (#36).</summary>
public sealed partial class PlaylistPage : Page
{
    private readonly ObservableCollection<PlaylistEntry> _entries = [];
    private Playlist? _playlist;
    private string? _playlistId;
    private bool _saving;

    public PlaylistPage()
    {
        InitializeComponent();
        List.ItemsSource = _entries;
        App.PlaylistChanged += OnPlaylistChanged;
        Unloaded += (_, _) => App.PlaylistChanged -= OnPlaylistChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not string id || !PlaylistRules.IsPlainId(id)) return;
        _playlistId = id;
        _ = LoadAsync();
    }

    private void OnPlaylistChanged(object? sender, PlaylistChange change)
    {
        if (change.PlaylistId != _playlistId || _saving) return;
        if (change.Kind == PlaylistChangeKind.Removed)
        {
            App.MainWindow.ShowInfo("Diese Wiedergabeliste wurde gelöscht.");
            if (Frame.CanGoBack) Frame.GoBack();
            return;
        }
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_playlistId is null) return;
        try
        {
            Show(await App.Api.GetPlaylistAsync(_playlistId));
            ContentRoot.Visibility = Visibility.Visible;
            App.MainWindow.ClearError(MessageOrigins.Playlist);
        }
        catch (AbsApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            App.MainWindow.ShowError("Diese Wiedergabeliste gibt es nicht mehr.", MessageOrigins.Playlist);
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Wiedergabeliste konnte nicht geladen werden: {ex.Message}", MessageOrigins.Playlist);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void Show(Playlist playlist)
    {
        // Audiobookshelf löscht eine Liste, deren letzter Eintrag entfernt wird, und antwortet mit der leeren Liste.
        _playlist = playlist;
        TitleText.Text = PlaylistRules.DisplayName(playlist);
        DescriptionText.Text = PlaylistRules.DisplayDescription(playlist);
        DescriptionText.Visibility = DescriptionText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _entries.Clear();
        foreach (var item in playlist.Items) _entries.Add(new PlaylistEntry(item));
        CountText.Text = _entries.Count == 1 ? "1 Titel" : $"{_entries.Count} Titel";
        PlayButton.IsEnabled = _entries.Any(e => e.IsAvailable);
        DownloadButton.IsEnabled = PlaylistRules.DownloadPlan(playlist).Entries.Count > 0;
    }

    /// <summary>
    /// Legt jedes Buch und jede Podcast-Folge (#76) der Liste in die bestehenden Downloads (#62). Fortschritt und
    /// Abbrechen wie bei einzelnen Downloads auf der Seite „Downloads“. Abgelegt wird unter Item- bzw. Folgen-ID,
    /// nie unter dem Namen der Liste.
    /// </summary>
    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_playlist is null) return;
        var plan = PlaylistRules.DownloadPlan(_playlist);
        foreach (var entry in plan.Entries) App.Downloads.Enqueue(entry.ItemId, entry.Title, entry.EpisodeId);
        if (plan.Entries.Count > 0) App.MainWindow.ShowInfo(plan.Describe(), "Downloads anzeigen", App.MainWindow.OpenDownloads);
        else App.MainWindow.ShowInfo(plan.Describe());
    }

    private static PlaylistEntry? EntryOf(object sender) => ((FrameworkElement)sender).Tag as PlaylistEntry;

    private void OnPlayAll(object sender, RoutedEventArgs e)
    {
        if (_playlist is not null) _ = App.Player.PlayPlaylistAsync(_playlist);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistEntry entry) PlayFrom(entry);
    }

    private void OnPlayFrom(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) PlayFrom(entry);
    }

    private void PlayFrom(PlaylistEntry entry)
    {
        if (_playlist is null || !entry.IsAvailable) return;
        _ = App.Player.PlayPlaylistAsync(_playlist, _entries.IndexOf(entry));
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => _ = MoveAsync(EntryOf(sender), -1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => _ = MoveAsync(EntryOf(sender), 1);

    private async Task MoveAsync(PlaylistEntry? entry, int delta)
    {
        if (entry is null) return;
        var index = _entries.IndexOf(entry);
        if (PlaylistRules.Move(_entries.Select(x => x.Item).ToList(), index, delta) is not { } order) return;
        _entries.Move(index, index + delta);
        await SaveOrderAsync(order);
        // Fokus bleibt beim verschobenen Eintrag, damit man mit der Tastatur weiterschieben kann.
        if (List.ContainerFromItem(entry) is ListViewItem container) container.Focus(FocusState.Keyboard);
    }

    private void OnDragItemsCompleted(ListViewBase sender, Microsoft.UI.Xaml.Controls.DragItemsCompletedEventArgs args)
        => _ = SaveOrderAsync(_entries.Select(x => x.Item).ToList());

    private async Task SaveOrderAsync(List<PlaylistItem> order)
    {
        if (_playlistId is null) return;
        _saving = true;
        try
        {
            Show(await App.Api.ReorderPlaylistAsync(_playlistId, order));
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Reihenfolge konnte nicht gespeichert werden: {ex.Message}");
            await LoadAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry || _playlistId is null) return;
        var last = _entries.Count == 1;
        if (last && !await ConfirmRemoveLastAsync()) return;
        _saving = true;
        try
        {
            var result = await App.Api.RemoveFromPlaylistAsync(_playlistId, entry.Item.LibraryItemId, entry.Item.EpisodeId);
            if (last)
            {
                App.MainWindow.ShowInfo("Die Wiedergabeliste war leer und wurde gelöscht.");
                if (Frame.CanGoBack) Frame.GoBack();
                return;
            }
            Show(result);
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Eintrag konnte nicht entfernt werden: {ex.Message}");
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task<bool> ConfirmRemoveLastAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Letzten Eintrag entfernen?",
            Content = "Audiobookshelf löscht eine Wiedergabeliste, sobald sie leer ist.",
            PrimaryButtonText = "Entfernen und Liste löschen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_playlist is null) return;
        var current = PlaylistRules.DisplayName(_playlist);
        if (await PlaylistUi.AskNameAsync(XamlRoot, "Wiedergabeliste umbenennen", "Umbenennen", current) is not { } name) return;
        _saving = true;
        try
        {
            Show(await App.Api.RenamePlaylistAsync(_playlist.Id, name));
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Wiedergabeliste konnte nicht umbenannt werden: {ex.Message}");
        }
        finally
        {
            _saving = false;
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_playlist is null || !await PlaylistUi.ConfirmDeleteAsync(XamlRoot, _playlist)) return;
        _saving = true;
        try
        {
            await App.Api.DeletePlaylistAsync(_playlist.Id);
            if (Frame.CanGoBack) Frame.GoBack();
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Wiedergabeliste konnte nicht gelöscht werden: {ex.Message}");
        }
        finally
        {
            _saving = false;
        }
    }
}
