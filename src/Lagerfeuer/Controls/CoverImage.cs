using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lagerfeuer.Controls;

/// <summary>Zeigt das Cover eines Items aus dem lokalen Cover-Cache (lädt bei Bedarf nach).</summary>
public sealed partial class CoverImage : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly FontIcon _placeholder = new()
    {
        Glyph = "", // Audio
        FontSize = 32,
        Opacity = 0.4,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public CoverImage()
    {
        CornerRadius = new CornerRadius(6);
        Background = (Brush)Application.Current.Resources["LfSurfaceDeepBrush"];
        Children.Add(_placeholder);
        Children.Add(_image);
    }

    public static readonly DependencyProperty ItemIdProperty = DependencyProperty.Register(
        nameof(ItemId), typeof(string), typeof(CoverImage), new PropertyMetadata(null, (d, _) => ((CoverImage)d).Load()));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth), typeof(int), typeof(CoverImage), new PropertyMetadata(300));

    public string? ItemId
    {
        get => (string?)GetValue(ItemIdProperty);
        set => SetValue(ItemIdProperty, value);
    }

    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    private async void Load()
    {
        var id = ItemId;
        _image.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(id)) return;

        try
        {
            var path = await App.Covers.GetCoverPathAsync(id);
            if (path is null || id != ItemId) return;

            var bitmap = new BitmapImage { DecodePixelWidth = DecodeWidth, DecodePixelType = DecodePixelType.Logical };
            using (var fs = File.OpenRead(path))
                await bitmap.SetSourceAsync(fs.AsRandomAccessStream());
            if (id != ItemId) return;

            _image.Source = bitmap;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error($"Cover {id} konnte nicht angezeigt werden", ex);
        }
    }
}
