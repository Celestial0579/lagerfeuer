using System.Collections.ObjectModel;
using Lagerfeuer.Models;
using Lagerfeuer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed partial class LibraryPage : Page
{
    private Library? _library;
    private IncrementalBookCollection? _all;
    private CancellationTokenSource? _searchCts;

    public LibraryPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Library lib) return;

        if (_library?.Id != lib.Id)
        {
            _library = lib;
            HeaderText.Text = lib.Name;
            SearchBox.Text = "";
            ShowAll(reload: true);
        }
        else
        {
            // Zurück zur Seite: Fortschritt kann sich geändert haben.
            if (Grid.ItemsSource is IEnumerable<BookItemViewModel> items)
                foreach (var vm in items) vm.RefreshProgress();
        }
    }

    private void ShowAll(bool reload)
    {
        if (reload || _all is null || _all.HasError)
        {
            _all = new IncrementalBookCollection(_library!.Id, App.Api);
            _all.LoadFailed += (_, ex) => App.MainWindow.ShowError($"Bibliothek konnte nicht geladen werden: {ex.Message}");
        }
        NoResults.Visibility = Visibility.Collapsed;
        Grid.ItemsSource = _all;
    }

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
            Grid.ItemsSource = new ObservableCollection<BookItemViewModel>(results.Select(r => new BookItemViewModel(r)));
            NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Suche fehlgeschlagen: {ex.Message}");
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookItemViewModel vm) App.MainWindow.OpenItem(vm.Id);
    }
}
