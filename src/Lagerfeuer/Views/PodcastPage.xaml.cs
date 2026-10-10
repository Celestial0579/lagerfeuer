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
        Info = string.Join(" · ", parts);
    }
}

/// <summary>Podcast mit seinen Folgen (neueste zuerst).</summary>
public sealed partial class PodcastPage : Page
{
    private string? _itemId;
    private List<EpisodeViewModel> _episodes = [];

    public PodcastPage()
    {
        InitializeComponent();
        App.Progress.Changed += OnProgressChanged;
        Unloaded += (_, _) => App.Progress.Changed -= OnProgressChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not string itemId) return;
        _itemId = itemId;
        try
        {
            var item = await App.Api.GetItemAsync(itemId);
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
            HideFinishedBox.IsChecked = App.Settings.HideFinishedEpisodes;
            ApplyFilter();
            ContentRoot.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Podcast konnte nicht geladen werden: {ex.Message}");
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

    private void OnEpisodeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodeViewModel ep) _ = App.Player.PlayItemAsync(ep.ItemId, episodeId: ep.Id);
    }

    private void OnPlayEpisode(object sender, RoutedEventArgs e)
    {
        if (_itemId is not null && ((FrameworkElement)sender).Tag is string episodeId)
            _ = App.Player.PlayItemAsync(_itemId, episodeId: episodeId);
    }
}
