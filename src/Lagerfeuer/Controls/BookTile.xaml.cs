using Lagerfeuer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lagerfeuer.Controls;

public sealed partial class BookTile : UserControl
{
    public BookTile() => InitializeComponent();

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(BookItemViewModel), typeof(BookTile), new PropertyMetadata(null));

    public BookItemViewModel? Item
    {
        get => (BookItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
