using System.Collections.ObjectModel;
using Lagerfeuer.Models;
using Lagerfeuer.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace Lagerfeuer.ViewModels;

public sealed partial class BookItemViewModel : ObservableObject
{
    public BookItemViewModel(LibraryItem item)
    {
        Item = item;
        RefreshProgress();
    }

    public LibraryItem Item { get; }
    public string Id => Item.Id;
    public string Title => Item.Media.Metadata.Title ?? "(ohne Titel)";
    public string Author => Item.Media.Metadata.DisplayAuthor;

    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool HasProgress { get; set; }
    [ObservableProperty] public partial bool IsFinished { get; set; }
    [ObservableProperty] public partial bool IsDownloaded { get; set; }

    public void RefreshProgress()
    {
        var p = App.Progress.Get(Item.Id) ?? Item.UserMediaProgress;
        IsFinished = p?.IsFinished ?? false;
        ProgressPercent = IsFinished ? 100 : (p?.Progress ?? 0) * 100;
        HasProgress = ProgressPercent > 0;
        IsDownloaded = App.Cache.IsPinned(Item.Id);
    }
}

/// <summary>Bibliotheksansicht mit seitenweisem Nachladen (Incremental Loading) für große Bibliotheken.</summary>
public sealed partial class IncrementalBookCollection(string libraryId, AbsApiClient api)
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
            var resp = await api.GetLibraryItemsAsync(libraryId, _page, PageSize);
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
