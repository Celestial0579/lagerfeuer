using Lagerfeuer.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Lagerfeuer.Views;

/// <summary>Kompaktes Wiedergabefenster, das immer im Vordergrund bleibt (Windows "Compact Overlay").</summary>
public sealed partial class MiniPlayerWindow : Window
{
    public PlayerService Player => App.Player;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
        AppWindow.Resize(new SizeInt32(380, 230));
        Player.PropertyChanged += OnPlayerChanged;
        Closed += (_, _) => Player.PropertyChanged -= OnPlayerChanged;
    }

    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Wiedergabe beendet → Mini-Player schließen
        if (e.PropertyName == nameof(PlayerService.HasSession) && !Player.HasSession) Close();
    }

    public string PlayGlyph(bool playing) => playing ? "" : "";

    public string TimeText(double position, double duration)
        => $"{Format.Time(position)} / {Format.Time(duration)}";

    private void OnPlayPause(object sender, RoutedEventArgs e) => Player.TogglePlayPause();
    private void OnBack(object sender, RoutedEventArgs e) => _ = Player.SkipAsync(-10);
    private void OnForward(object sender, RoutedEventArgs e) => _ = Player.SkipAsync(30);
    private void OnNextChapter(object sender, RoutedEventArgs e) => Player.NextChapter();
}
