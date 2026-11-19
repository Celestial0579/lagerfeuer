using System.Collections.ObjectModel;
using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Lagerfeuer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed partial class LibraryPage : Page
{
    private sealed record Option(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    private static readonly Option[] BookSorts =
    [
        new("Titel", LibraryQuery.SortTitle),
        new("Autor", LibraryQuery.SortAuthor),
        new("Zuletzt hinzugefügt", LibraryQuery.SortAdded),
        new("Erscheinungsjahr", LibraryQuery.SortPublished),
        new("Dauer", LibraryQuery.SortDuration),
        new("Zuletzt gehört", LibraryQuery.SortProgress),
    ];

    private static readonly Option[] PodcastSorts =
    [
        new("Titel", LibraryQuery.SortTitle),
        new("Autor", LibraryQuery.SortPodcastAuthor),
        new("Zuletzt hinzugefügt", LibraryQuery.SortAdded),
    ];

    private static readonly Option[] Filters =
    [
        new("Alle", ""),
        new("Nicht begonnen", "not-started"),
        new("Angefangen", "in-progress"),
        new("Nicht beendet", "not-finished"),
        new("Beendet", "finished"),
    ];

    private Library? _library;
    private IncrementalBookCollection? _all;
    private CancellationTokenSource? _searchCts;
    private bool _updatingControls;
    private bool _seriesLoaded;
    private bool _authorsLoaded;

    public LibraryPage()
    {
        InitializeComponent();
        // Die Seite bleibt im Speicher (NavigationCacheMode): bei jedem Zeigen an- und beim Verlassen abmelden.
        Loaded += (_, _) => App.CollectionChanged += OnCollectionChanged;
        Unloaded += (_, _) => App.CollectionChanged -= OnCollectionChanged;
    }

    private bool IsPodcastLibrary => _library?.MediaType == "podcast";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Library lib) return;

        if (_library?.Id != lib.Id)
        {
            _library = lib;
            _seriesLoaded = _authorsLoaded = false;
            HeaderText.Text = lib.Name;
            SearchBox.Text = "";
            SetupControls();
            ViewSelector.SelectedItem = BooksTab;
            ShowView("books");
            ShowAll(reload: true);
        }
        else if (BooksGrid.ItemsSource is IEnumerable<BookItemViewModel> items)
        {
            // Zurück zur Seite: Fortschritt kann sich geändert haben.
            foreach (var vm in items) vm.RefreshProgress();
        }
        if (_library?.Id == lib.Id && CollectionsGrid.Visibility == Visibility.Visible) _ = LoadCollectionsAsync();
    }

    private void SetupControls()
    {
        _updatingControls = true;
        var podcast = IsPodcastLibrary;
        SeriesTab.Visibility = AuthorsTab.Visibility = CollectionsTab.Visibility = podcast ? Visibility.Collapsed : Visibility.Visible;
        // Podcast-Bibliotheken haben nur eine Ansicht: keine Reiterleiste, auch kein einzelnes „Bücher“ (#43).
        ViewSelector.Visibility = LibraryViews.ShowSelector(_library?.MediaType) ? Visibility.Visible : Visibility.Collapsed;
        FilterBox.Visibility = podcast ? Visibility.Collapsed : Visibility.Visible; // Podcasts haben keinen Fortschrittsfilter

        var sorts = podcast ? PodcastSorts : BookSorts;
        SortBox.ItemsSource = sorts;
        SortBox.SelectedItem = sorts.FirstOrDefault(s => s.Value == App.Settings.LibrarySort) ?? sorts[0];
        DescToggle.IsChecked = App.Settings.LibrarySortDescending;
        FilterBox.ItemsSource = Filters;
        FilterBox.SelectedItem = Filters.FirstOrDefault(f => f.Value == App.Settings.LibraryFilter) ?? Filters[0];
        SearchBox.PlaceholderText = podcast ? "Podcasts suchen" : "Titel, Autor, Serie suchen";
        _updatingControls = false;
    }

    private LibraryQuery CurrentQuery()
    {
        var sort = (SortBox.SelectedItem as Option)?.Value ?? LibraryQuery.SortTitle;
        var filter = IsPodcastLibrary ? "" : (FilterBox.SelectedItem as Option)?.Value ?? "";
        return new LibraryQuery(sort, DescToggle.IsChecked == true,
            filter.Length > 0 ? "progress" : null, filter.Length > 0 ? filter : null);
    }

    private void OnQueryChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || _library is null) return;
        var q = CurrentQuery();
        App.Settings.LibrarySort = q.Sort;
        App.Settings.LibrarySortDescending = q.Descending;
        App.Settings.LibraryFilter = q.FilterValue ?? "";
        App.Settings.Save();
        SearchBox.Text = "";
        ShowAll(reload: true);
    }

    private void ShowAll(bool reload)
    {
        if (reload || _all is null || _all.HasError)
        {
            _all = new IncrementalBookCollection(_library!.Id, App.Api, CurrentQuery());
            _all.LoadFailed += (_, ex) => App.MainWindow.ShowError($"Bibliothek konnte nicht geladen werden: {ex.Message}", MessageOrigins.Library);
            _all.LoadSucceeded += (_, _) => App.MainWindow.ClearError(MessageOrigins.Library);
        }
        NoResults.Visibility = Visibility.Collapsed;
        BooksGrid.ItemsSource = _all;
    }

    // ---------- Ansicht: Bücher / Serien / Autoren ----------

    private void OnViewChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_library is null || sender.SelectedItem?.Tag is not string view) return;
        ShowView(view);
        if (view == "series" && !_seriesLoaded) _ = LoadSeriesAsync();
        if (view == "authors" && !_authorsLoaded) _ = LoadAuthorsAsync();
        if (view == LibraryViews.Collections) _ = LoadCollectionsAsync(); // immer frisch: andere Nutzer ändern sie mit
    }

    private void ShowView(string view)
    {
        BooksGrid.Visibility = view == "books" ? Visibility.Visible : Visibility.Collapsed;
        SeriesGrid.Visibility = view == "series" ? Visibility.Visible : Visibility.Collapsed;
        AuthorsGrid.Visibility = view == "authors" ? Visibility.Visible : Visibility.Collapsed;
        CollectionsGrid.Visibility = view == LibraryViews.Collections ? Visibility.Visible : Visibility.Collapsed;
        SortPanel.Visibility = view == "books" ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = view == "books" ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = Visibility.Collapsed;
    }

    private async Task LoadSeriesAsync()
    {
        LoadingRing.IsActive = true;
        try
        {
            var list = new ObservableCollection<SeriesViewModel>();
            SeriesGrid.ItemsSource = list;
            for (var page = 0; ; page++)
            {
                var resp = await App.Api.GetSeriesAsync(_library!.Id, page, 100);
                foreach (var s in resp.Results) list.Add(new SeriesViewModel(s));
                if (resp.Results.Count == 0 || list.Count >= resp.Total) break;
            }
            _seriesLoaded = true;
            App.MainWindow.ClearError(MessageOrigins.Series);
            NoResults.Visibility = list.Count == 0 && SeriesGrid.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Serien konnten nicht geladen werden: {ex.Message}", MessageOrigins.Series);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async Task LoadAuthorsAsync()
    {
        LoadingRing.IsActive = true;
        try
        {
            var authors = await App.Api.GetAuthorsAsync(_library!.Id);
            AuthorsGrid.ItemsSource = authors.Where(a => a.NumBooks > 0).Select(a => new AuthorViewModel(a)).ToList();
            _authorsLoaded = true;
            App.MainWindow.ClearError(MessageOrigins.Authors);
            NoResults.Visibility = authors.Count == 0 && AuthorsGrid.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Autoren konnten nicht geladen werden: {ex.Message}", MessageOrigins.Authors);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private int _collectionsGeneration;

    private async Task LoadCollectionsAsync()
    {
        if (_library is null) return;
        var generation = ++_collectionsGeneration;
        LoadingRing.IsActive = true;
        try
        {
            var list = await App.Api.GetCollectionsAsync(_library.Id);
            if (generation != _collectionsGeneration) return;
            var items = list.Where(c => PlaylistRules.IsPlainId(c.Id))
                .Select(c => new CollectionViewModel(c))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            CollectionsGrid.ItemsSource = items;
            App.MainWindow.ClearError(MessageOrigins.Collections);
            NoResults.Visibility = items.Count == 0 && CollectionsGrid.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Sammlungen konnten nicht geladen werden: {ex.Message}", MessageOrigins.Collections);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void OnCollectionChanged(object? sender, CollectionChange change)
    {
        if (change.LibraryId == _library?.Id && CollectionsGrid.Visibility == Visibility.Visible) _ = LoadCollectionsAsync();
    }

    private void OnCollectionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CollectionViewModel c) App.MainWindow.OpenCollection(c.Id);
    }

    private void OnSeriesClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SeriesViewModel s)
            Frame.Navigate(typeof(SeriesPage), new SeriesPageArgs(_library!.Id, s.Id, s.Name));
    }

    private void OnAuthorClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AuthorViewModel a) Frame.Navigate(typeof(AuthorPage), a.Id);
    }

    // ---------- Suche ----------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _ = SearchDebouncedAsync(sender.Text);
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => _ = SearchDebouncedAsync(args.QueryText, delayMs: 0);

    private async Task SearchDebouncedAsync(string query, int delayMs = 350)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(query))
        {
            ShowAll(reload: false);
            return;
        }

        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            var results = await App.Api.SearchAsync(_library!.Id, query.Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;
            BooksGrid.ItemsSource = new ObservableCollection<BookItemViewModel>(results.Select(r => new BookItemViewModel(r)));
            NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            App.MainWindow.ClearError(MessageOrigins.Search);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Suche fehlgeschlagen: {ex.Message}", MessageOrigins.Search);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookItemViewModel vm) App.MainWindow.OpenItem(vm.Item);
    }
}
