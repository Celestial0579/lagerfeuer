using Lagerfeuer.Services;
using Lagerfeuer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

/// <summary>Autor mit Beschreibung, Serien und allen Büchern.</summary>
public sealed partial class AuthorPage : Page
{
    public AuthorPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not string authorId) return;
        try
        {
            var author = await App.Api.GetAuthorAsync(authorId);
            Portrait.AuthorId = author.Id;
            NameText.Text = author.Name;
            var count = author.LibraryItems.Count;
            InfoText.Text = $"{count} {(count == 1 ? "Buch" : "Bücher")}" +
                            (author.Series.Count > 0 ? $" · {author.Series.Count} Serie{(author.Series.Count == 1 ? "" : "n")}" : "");
            DescriptionText.Text = Format.StripHtml(author.Description);
            DescriptionText.Visibility = string.IsNullOrWhiteSpace(author.Description) ? Visibility.Collapsed : Visibility.Visible;

            foreach (var series in author.Series)
            {
                SeriesPanel.Children.Add(new TextBlock { Text = series.Name, Style = (Style)Application.Current.Resources["LfSubtitleStyle"] });
                var grid = new GridView
                {
                    IsItemClickEnabled = true,
                    SelectionMode = ListViewSelectionMode.None,
                    ItemTemplate = BooksGrid.ItemTemplate,
                    ItemsSource = series.Items.Select(b =>
                    {
                        var seq = b.Media.Metadata.Series.FirstOrDefault()?.Sequence;
                        return new BookItemViewModel(b, string.IsNullOrEmpty(seq) ? null : $"Band {seq}");
                    }).ToList(),
                };
                grid.ItemClick += OnItemClick;
                SeriesPanel.Children.Add(grid);
            }

            BooksGrid.ItemsSource = author.LibraryItems.Select(b => new BookItemViewModel(b)).ToList();
            ContentRoot.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Autor konnte nicht geladen werden: {ex.Message}");
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
