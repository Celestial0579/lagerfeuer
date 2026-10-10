using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Lagerfeuer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed partial class BookPage : Page
{
    private LibraryItem? _item;

    public BookPage()
    {
        InitializeComponent();
        App.Progress.Changed += OnProgressChanged;
        App.Downloads.Changed += OnDownloadChanged;
        App.BookmarksChanged += OnBookmarksChanged;
        Unloaded += (_, _) =>
        {
            App.Progress.Changed -= OnProgressChanged;
            App.Downloads.Changed -= OnDownloadChanged;
            App.BookmarksChanged -= OnBookmarksChanged;
        };
    }

    public static string FormatTime(double seconds) => Format.Time(seconds);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string itemId) _ = LoadAsync(itemId);
    }

    private async Task LoadAsync(string itemId)
    {
        try
        {
            _item = await App.Api.GetItemAsync(itemId);
            if (_item.UserMediaProgress is { } p) App.Progress.Set(p);

            var meta = _item.Media.Metadata;
            Cover.ItemId = _item.Id;
            TitleText.Text = meta.Title ?? "";
            SetText(SubtitleText, meta.Subtitle);
            SetText(AuthorText, string.IsNullOrWhiteSpace(meta.DisplayAuthor) ? null : $"von {meta.DisplayAuthor}");
            SetText(NarratorText, string.IsNullOrWhiteSpace(meta.DisplayNarrator) ? null : $"Gelesen von {meta.DisplayNarrator}");
            SetText(SeriesText, meta.SeriesName);
            DurationText.Text = string.Join(" · ", new[]
            {
                _item.Media.Duration > 0 ? Format.Duration(_item.Media.Duration) : null,
                meta.PublishedYear,
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
            SetText(DescriptionText, Format.StripHtml(meta.Description));

            ChapterList.ItemsSource = _item.Media.Chapters;
            ChaptersPanel.Visibility = _item.Media.Chapters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            BuildLinks();
            UpdateBookmarks();
            UpdateProgress();
            UpdateDownloadState();
            ContentRoot.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Buch konnte nicht geladen werden: {ex.Message}");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private static void SetText(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnProgressChanged(object? sender, string itemId)
    {
        if (itemId == _item?.Id) DispatcherQueue.TryEnqueue(UpdateProgress);
    }

    private void UpdateProgress()
    {
        if (_item is null) return;
        var p = App.Progress.Get(_item.Id);
        var finished = p?.IsFinished ?? false;
        var current = p?.CurrentTime ?? 0;
        var duration = _item.Media.Duration;

        ProgressPanel.Visibility = p is not null && (finished || current > 0) ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Value = finished ? 100 : (p?.Progress ?? 0) * 100;
        ProgressText.Text = finished
            ? "Beendet"
            : $"{Format.Time(current)} von {Format.Time(duration)} · noch {Format.Duration(duration - current)}";
        var hasAudio = _item.Media.AudioFiles is not { Count: 0 };
        PlayButton.IsEnabled = hasAudio;
        PlayButtonText.Text = !hasAudio ? "Keine Audiodateien (E-Book)"
            : !finished && current > 0 ? "Weiterhören" : "Abspielen";
        FinishedButtonText.Text = finished ? "Als nicht beendet markieren" : "Als beendet markieren";
    }

    // ---------- Autor- und Serien-Links ----------

    private void BuildLinks()
    {
        var meta = _item!.Media.Metadata;
        AuthorLinks.Children.Clear();
        SeriesLinks.Children.Clear();

        if (meta.Authors.Count > 0)
        {
            AuthorText.Visibility = Visibility.Collapsed;
            foreach (var author in meta.Authors)
            {
                var link = new HyperlinkButton { Content = author.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                link.Click += (_, _) => App.MainWindow.OpenAuthor(author.Id);
                AuthorLinks.Children.Add(link);
            }
        }

        if (meta.Series.Count > 0)
        {
            SeriesText.Visibility = Visibility.Collapsed;
            foreach (var series in meta.Series)
            {
                var link = new HyperlinkButton
                {
                    Content = string.IsNullOrEmpty(series.Sequence) ? series.Name : $"{series.Name} · Band {series.Sequence}",
                };
                link.Click += (_, _) => App.MainWindow.OpenSeries(_item.LibraryId, series.Id, series.Name);
                SeriesLinks.Children.Add(link);
            }
        }
    }

    // ---------- Lesezeichen ----------

    private void OnBookmarksChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateBookmarks);

    private void UpdateBookmarks()
    {
        if (_item is null) return;
        var bookmarks = App.User?.Bookmarks.Where(b => b.LibraryItemId == _item.Id).OrderBy(b => b.Time).ToList() ?? [];
        BookmarkList.ItemsSource = bookmarks;
        BookmarksPanel.Visibility = bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBookmarkClick(object sender, ItemClickEventArgs e)
    {
        if (_item is not null && e.ClickedItem is Models.Bookmark bm) _ = App.Player.PlayItemAsync(_item.Id, bm.Time);
    }

    private async void OnDeleteBookmark(object sender, RoutedEventArgs e)
    {
        if (_item is null || ((FrameworkElement)sender).Tag is not Models.Bookmark bm) return;
        try
        {
            await App.Api.DeleteBookmarkAsync(_item.Id, bm.Time);
            App.User?.Bookmarks.RemoveAll(b => b.LibraryItemId == bm.LibraryItemId && b.Time == bm.Time);
            App.RaiseBookmarksChanged();
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Lesezeichen konnte nicht gelöscht werden: {ex.Message}");
        }
    }

    // ---------- Download ----------

    private void OnDownloadChanged(object? sender, DownloadState state)
    {
        if (state.ItemId == _item?.Id) DispatcherQueue.TryEnqueue(UpdateDownloadState);
    }

    private void UpdateDownloadState()
    {
        if (_item is null) return;
        var hasAudio = _item.Media.AudioFiles is not { Count: 0 };
        var state = App.Downloads.Get(_item.Id);
        DownloadButton.IsEnabled = hasAudio && (App.CanDownload || App.Cache.IsPinned(_item.Id));
        if (!App.CanDownload && !App.Cache.IsPinned(_item.Id))
        {
            DownloadButtonText.Text = "Download nicht erlaubt";
            ToolTipService.SetToolTip(DownloadButton, "Dein Audiobookshelf-Admin hat das Herunterladen für dein Konto deaktiviert.");
            return;
        }

        if (state is { IsActive: true })
        {
            DownloadIcon.Glyph = "\uE711";
            DownloadButtonText.Text = state.Status == DownloadStatus.Queued ? "Wartet … (abbrechen)" : $"{state.Fraction:P0} (abbrechen)";
            DownloadProgress.Value = state.Fraction;
            DownloadProgress.Visibility = Visibility.Visible;
            return;
        }

        DownloadProgress.Visibility = Visibility.Collapsed;
        if (App.Cache.IsPinned(_item.Id))
        {
            DownloadIcon.Glyph = "\uE73E";
            DownloadButtonText.Text = "Heruntergeladen";
            ToolTipService.SetToolTip(DownloadButton, "Klicken, um den Download zu entfernen");
        }
        else
        {
            DownloadIcon.Glyph = "\uE896";
            DownloadButtonText.Text = state?.Status == DownloadStatus.Failed ? "Erneut herunterladen" : "Herunterladen";
            ToolTipService.SetToolTip(DownloadButton, state?.Status == DownloadStatus.Failed ? state.Error : "Für die Offline-Wiedergabe herunterladen");
        }
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        var id = _item.Id;
        if (App.Downloads.Get(id) is { IsActive: true })
        {
            App.Downloads.Cancel(id);
            return;
        }
        if (App.Cache.IsPinned(id))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Download entfernen?",
                Content = $"„{_item.Media.Metadata.Title}“ wird von diesem Gerät gelöscht. Dein Hörfortschritt bleibt erhalten.",
                PrimaryButtonText = "Entfernen",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (App.Player.ItemId == id) await App.Player.StopAsync();
            await Task.Run(() => App.Downloads.Remove(id));
            UpdateDownloadState();
            return;
        }
        App.Downloads.Enqueue(id, _item.Media.Metadata.Title);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_item is not null) _ = App.Player.PlayItemAsync(_item.Id);
    }

    private void OnChapterClick(object sender, ItemClickEventArgs e)
    {
        if (_item is not null && e.ClickedItem is Chapter ch) _ = App.Player.PlayItemAsync(_item.Id, ch.Start);
    }

    private async void OnToggleFinished(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        var p = App.Progress.Get(_item.Id);
        var finish = !(p?.IsFinished ?? false);
        try
        {
            var duration = _item.Media.Duration;
            var current = finish ? duration : 0;
            await App.Api.UpdateProgressAsync(_item.Id, current, duration, finish);
            App.Progress.Set(new MediaProgress
            {
                LibraryItemId = _item.Id,
                CurrentTime = current,
                Duration = duration,
                Progress = finish ? 1 : 0,
                IsFinished = finish,
            });
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Fortschritt konnte nicht gespeichert werden: {ex.Message}");
        }
    }
}
