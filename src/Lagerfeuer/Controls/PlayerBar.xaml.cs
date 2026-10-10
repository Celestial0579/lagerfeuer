using System.ComponentModel;
using Lagerfeuer.Services;
using Lagerfeuer.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;

namespace Lagerfeuer.Controls;

public sealed partial class PlayerBar : UserControl
{
    private static readonly double[] Rates = [0.75, 0.9, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    private bool _updatingSlider;
    private bool _dragging;

    public PlayerService Player => App.Player;

    /// <summary>Benutzer möchte das gerade laufende Buch öffnen.</summary>
    public event EventHandler<string>? OpenItemRequested;

    public PlayerBar()
    {
        InitializeComponent();

        foreach (var rate in Rates)
        {
            var item = new MenuFlyoutItem { Text = FormatRate(rate), Tag = rate };
            item.Click += (_, _) => Player.PlaybackRate = rate;
            RateFlyout.Items.Add(item);
        }

        PositionSlider.ThumbToolTipValueConverter = new TimeConverter();
        PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSliderReleased), true);
        PositionSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSliderReleased), true);

        Player.PropertyChanged += OnPlayerPropertyChanged;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerService.Position) or nameof(PlayerService.Duration) && !_dragging)
        {
            _updatingSlider = true;
            PositionSlider.Value = Math.Min(Player.Position, Player.Duration);
            _updatingSlider = false;
        }
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Tastatur/Klick ohne Ziehen → sofort springen; beim Ziehen erst beim Loslassen.
        if (_updatingSlider || _dragging) return;
        if (Math.Abs(e.NewValue - Player.Position) > 1.5) _ = Player.SeekAsync(e.NewValue);
    }

    private void OnSliderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _ = Player.SeekAsync(PositionSlider.Value);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => Player.TogglePlayPause();
    private void OnBack(object sender, RoutedEventArgs e) => _ = Player.SkipAsync(-10);
    private void OnForward(object sender, RoutedEventArgs e) => _ = Player.SkipAsync(30);
    private void OnPreviousChapter(object sender, RoutedEventArgs e) => Player.PreviousChapter();
    private void OnNextChapter(object sender, RoutedEventArgs e) => Player.NextChapter();
    private void OnStop(object sender, RoutedEventArgs e) => _ = Player.StopAsync();
    private void OnMiniPlayer(object sender, RoutedEventArgs e) => App.MainWindow.ShowMiniPlayer();

    private void OnOpenItem(object sender, RoutedEventArgs e)
    {
        if (Player.ItemId is { } id) OpenItemRequested?.Invoke(this, id);
    }

    private void OnSleep(object sender, RoutedEventArgs e)
    {
        var tag = (string)((MenuFlyoutItem)sender).Tag;
        if (tag == "chapter") Player.SetSleepAtChapterEnd();
        else
        {
            var minutes = int.Parse(tag);
            Player.SetSleepTimer(minutes == 0 ? null : TimeSpan.FromMinutes(minutes));
        }
    }

    // x:Bind-Funktionen
    public string PlayGlyph(bool playing) => playing ? "" : "";
    public Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public string FormatTime(double seconds) => Format.Time(seconds);
    public string FormatRemaining(double position, double duration) => "-" + Format.Time(duration - position);
    public string FormatRate(double rate) => rate.ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture) + "×";

    private sealed partial class TimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => Format.Time(value is double d ? d : 0);

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
