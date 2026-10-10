using System.Collections.ObjectModel;
using Lagerfeuer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed partial class HomePage : Page
{
    private readonly ObservableCollection<BookItemViewModel> _items = [];

    public HomePage()
    {
        InitializeComponent();
        Grid.ItemsSource = _items;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = LoadAsync();

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            App.Progress.Reset(await App.Api.GetAllProgressAsync());
            var items = await App.Api.GetItemsInProgressAsync();
            _items.Clear();
            foreach (var item in items) _items.Add(new BookItemViewModel(item));
            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"„Weiterhören“ konnte nicht geladen werden: {ex.Message}");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookItemViewModel vm) App.MainWindow.OpenItem(vm.Item);
    }
}
