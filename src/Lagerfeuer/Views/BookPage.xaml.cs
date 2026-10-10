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
        Unloaded += (_, _) => App.Progress.Changed -= OnProgressChanged;
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

            UpdateProgress();
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
