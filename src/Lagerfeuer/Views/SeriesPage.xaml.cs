using Lagerfeuer.Services;
using Lagerfeuer.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

public sealed record SeriesPageArgs(string LibraryId, string SeriesId, string Name);

/// <summary>Alle Bücher einer Serie in Lesereihenfolge.</summary>
public sealed partial class SeriesPage : Page
{
    public SeriesPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not SeriesPageArgs args) return;
        HeaderText.Text = args.Name;
        try
        {
            var books = await App.Api.GetSeriesBooksAsync(args.LibraryId, args.SeriesId);
            Grid.ItemsSource = books.Select(b =>
            {
                var sequence = b.Media.Metadata.Series.FirstOrDefault(s => s.Id == args.SeriesId)?.Sequence
                               ?? b.Media.Metadata.Series.FirstOrDefault()?.Sequence;
                return new BookItemViewModel(b, string.IsNullOrEmpty(sequence) ? null : $"Band {sequence}");
            }).ToList();
            var total = books.Sum(b => b.Media.Duration);
            InfoText.Text = $"{books.Count} {(books.Count == 1 ? "Buch" : "Bücher")} · {Format.Duration(total)}";
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Serie konnte nicht geladen werden: {ex.Message}");
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
