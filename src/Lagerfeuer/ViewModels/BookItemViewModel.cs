using System.Collections.ObjectModel;
using Lagerfeuer.Models;
using Lagerfeuer.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace Lagerfeuer.ViewModels;

/// <summary>Kachel für ein Buch oder einen Podcast (in "Weiterhören" ggf. mit angefangener Folge).</summary>
public sealed partial class BookItemViewModel : ObservableObject
{
    public BookItemViewModel(LibraryItem item, string? subtitle = null)
    {
        Item = item;
        _subtitle = subtitle;
        RefreshProgress();
    }

    private readonly string? _subtitle;

    public LibraryItem Item { get; }
    public string Id => Item.Id;
    public bool IsPodcast => Item.IsPodcast;
    public string? EpisodeId => Item.RecentEpisode?.Id;

    public string Title => Item.RecentEpisode?.Title ?? Item.Media.Metadata.Title ?? "(ohne Titel)";

    /// <summary>Autor – bei einer Podcast-Folge der Podcast-Name, in Serien ggf. "Band 3".</summary>
    public string Author => _subtitle
        ?? (Item.RecentEpisode is not null ? Item.Media.Metadata.Title ?? "" : Item.Media.Metadata.DisplayAuthor);

    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool HasProgress { get; set; }
    [ObservableProperty] public partial bool IsFinished { get; set; }
    [ObservableProperty] public partial bool IsDownloaded { get; set; }

    public void RefreshProgress()
    {
        var p = App.Progress.Get(Item.Id, EpisodeId) ?? (EpisodeId is null ? Item.UserMediaProgress : null);
        IsFinished = p?.IsFinished ?? false;
        ProgressPercent = IsFinished ? 100 : (p?.Progress ?? 0) * 100;
        HasProgress = ProgressPercent > 0;
        IsDownloaded = !IsPodcast && App.Cache.IsPinned(Item.Id);
    }
}

/// <summary>Bibliotheksansicht mit seitenweisem Nachladen (Incremental Loading) für große Bibliotheken.</summary>
public sealed partial class IncrementalBookCollection(string libraryId, AbsApiClient api, LibraryQuery? query = null)
    : ObservableCollection<BookItemViewModel>, ISupportIncrementalLoading
{
    private const int PageSize = 60;
    private int _page;
    private int _total = -1;
    private bool _loading;

    public bool HasError { get; private set; }

    public event EventHandler<Exception>? LoadFailed;

    public bool HasMoreItems => !HasError && (_total < 0 || Count < _total);

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        => LoadAsync().AsAsyncOperation();

    private async Task<LoadMoreItemsResult> LoadAsync()
    {
        if (_loading) return new LoadMoreItemsResult { Count = 0 };
        _loading = true;
        try
        {
            var resp = await api.GetLibraryItemsAsync(libraryId, _page, PageSize, query);
            _total = resp.Total;
            _page++;
            foreach (var item in resp.Results) Add(new BookItemViewModel(item));
            if (resp.Results.Count == 0) _total = Count;
            return new LoadMoreItemsResult { Count = (uint)resp.Results.Count };
        }
        catch (Exception ex)
        {
            HasError = true;
            Log.Error("Bibliothek konnte nicht geladen werden", ex);
            LoadFailed?.Invoke(this, ex);
            return new LoadMoreItemsResult { Count = 0 };
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>Serie als Kachel (Cover des ersten Bandes).</summary>
public sealed class SeriesViewModel(SeriesItem series)
{
    public SeriesItem Series { get; } = series;
    public string Id => Series.Id;
    public string Name => Series.Name;
    public string CoverItemId => Series.Books.FirstOrDefault()?.Id ?? "";
    public string Info => Series.Books.Count == 1 ? "1 Buch" : $"{Series.Books.Count} Bücher";
}

/// <summary>Autor als Kachel.</summary>
public sealed class AuthorViewModel(Author author)
{
    public Author Author { get; } = author;
    public string Id => Author.Id;
    public string Name => Author.Name;
    public string Info => Author.NumBooks == 1 ? "1 Buch" : $"{Author.NumBooks} Bücher";
}
