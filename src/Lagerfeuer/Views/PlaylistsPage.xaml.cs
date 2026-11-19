using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

/// <summary>Eine Zeile der Übersicht. Alle Texte sind bereinigt (<see cref="PlaylistRules"/>).</summary>
public sealed class PlaylistSummary(Playlist playlist, string libraryName)
{
    public Playlist Playlist { get; } = playlist;
    public string Id => Playlist.Id;
    public string Name { get; } = PlaylistRules.DisplayName(playlist);
    public string Info { get; } = $"{(playlist.Items.Count == 1 ? "1 Titel" : $"{playlist.Items.Count} Titel")} · {libraryName}";
    public bool CanPlay { get; } = playlist.Items.Any(PlaylistRules.IsAvailable);
}

/// <summary>Eigene Wiedergabelisten aller Bibliotheken (#36).</summary>
public sealed partial class PlaylistsPage : Page
{
    private List<PlaylistSummary> _lists = [];
    private int _loadGeneration;

    public PlaylistsPage()
    {
        InitializeComponent();
        App.PlaylistChanged += OnPlaylistChanged;
        Unloaded += (_, _) => App.PlaylistChanged -= OnPlaylistChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = RefreshAsync();

    private void OnPlaylistChanged(object? sender, PlaylistChange change) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var generation = ++_loadGeneration;
        LoadingRing.IsActive = true;
        var result = new List<PlaylistSummary>();
        string? error = null;
        foreach (var lib in App.Libraries)
        {
            try
            {
                foreach (var p in await App.Api.GetPlaylistsAsync(lib.Id))
                    if (PlaylistRules.IsPlainId(p.Id)) result.Add(new PlaylistSummary(p, lib.Name));
            }
            catch (Exception ex)
            {
                Log.Error($"Wiedergabelisten von {lib.Id} nicht geladen", ex);
                error = ex.Message;
            }
        }
        if (generation != _loadGeneration) return; // inzwischen neu geladen
        _lists = result.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        List.ItemsSource = _lists;
        EmptyState.Visibility = _lists.Count == 0 && error is null ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = false;
        if (error is not null) App.MainWindow.ShowError($"Wiedergabelisten konnten nicht geladen werden: {error}", MessageOrigins.Playlists);
        else App.MainWindow.ClearError(MessageOrigins.Playlists);
    }

    private PlaylistSummary? Find(object sender)
        => ((FrameworkElement)sender).Tag is string id ? _lists.FirstOrDefault(l => l.Id == id) : null;

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistSummary s) App.MainWindow.OpenPlaylist(s.Id);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is { } s) _ = App.Player.PlayPlaylistAsync(s.Playlist);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } s || !await PlaylistUi.ConfirmDeleteAsync(XamlRoot, s.Playlist)) return;
        try
        {
            await App.Api.DeletePlaylistAsync(s.Id);
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Wiedergabeliste konnte nicht gelöscht werden: {ex.Message}");
        }
        await RefreshAsync();
    }
}
