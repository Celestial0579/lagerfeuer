using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lagerfeuer.Views;

/// <summary>Dialoge und das Menü „Zur Wiedergabeliste hinzufügen …“ (#36).</summary>
public static class PlaylistUi
{
    /// <summary>Fragt nach einem Namen. Null bei Abbruch, leerer Eingabe oder unverändertem Namen.</summary>
    public static async Task<string?> AskNameAsync(XamlRoot root, string title, string primary, string current = "")
    {
        var box = new TextBox
        {
            Text = current,
            MaxLength = PlaylistRules.MaxNameLength,
            PlaceholderText = "Name der Wiedergabeliste",
            AcceptsReturn = false,
        };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return PlaylistRules.NormalizeName(box.Text, current);
    }

    public static async Task<bool> ConfirmDeleteAsync(XamlRoot root, Playlist playlist)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Wiedergabeliste löschen?",
            // Der Name kommt vom Server: als schlichter Text in einem TextBlock, nie als Markup.
            Content = new TextBlock
            {
                Text = $"„{PlaylistRules.DisplayName(playlist)}“ wird auf dem Server gelöscht. Die Bücher und Folgen selbst bleiben.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Löschen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Baut das Menü zum Hinzufügen, wenn es aufgeht: die eigenen Listen der Bibliothek (Häkchen, wo der
    /// Titel schon drin ist) und „Neue Liste …“.
    /// </summary>
    public static MenuFlyout CreateAddFlyout(Func<(string LibraryId, string ItemId, string? EpisodeId)?> target, XamlRoot? root = null)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += async (_, _) =>
        {
            flyout.Items.Clear();
            if (target() is not { } t) return;
            var loading = new MenuFlyoutItem { Text = "Wird geladen …", IsEnabled = false };
            flyout.Items.Add(loading);
            List<Playlist> lists;
            try
            {
                lists = await App.Api.GetPlaylistsAsync(t.LibraryId);
            }
            catch (Exception ex)
            {
                loading.Text = "Wiedergabelisten nicht verfügbar";
                Log.Error("Wiedergabelisten konnten nicht geladen werden", ex);
                return;
            }
            flyout.Items.Clear();
            foreach (var list in lists.Where(l => PlaylistRules.IsPlainId(l.Id)).OrderBy(PlaylistRules.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var contained = PlaylistRules.Contains(list, t.ItemId, t.EpisodeId);
                var item = new ToggleMenuFlyoutItem { Text = PlaylistRules.DisplayName(list), IsChecked = contained, IsEnabled = !contained };
                ToolTipService.SetToolTip(item, contained ? "Schon in dieser Liste" : null);
                item.Click += async (_, _) =>
                {
                    try
                    {
                        await App.Api.AddToPlaylistAsync(list.Id, t.ItemId, t.EpisodeId);
                        App.MainWindow.ShowInfo($"Zu „{PlaylistRules.DisplayName(list)}“ hinzugefügt.");
                    }
                    catch (Exception ex)
                    {
                        App.MainWindow.ShowError($"Konnte nicht hinzugefügt werden: {ex.Message}");
                    }
                };
                flyout.Items.Add(item);
            }
            if (flyout.Items.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = "Neue Liste …", Icon = new FontIcon { Glyph = "" } };
            create.Click += async (_, _) =>
            {
                if (await AskNameAsync(root ?? App.MainWindow.Content.XamlRoot, "Neue Wiedergabeliste", "Anlegen") is not { } name) return;
                try
                {
                    await App.Api.CreatePlaylistAsync(t.LibraryId, name, t.ItemId, t.EpisodeId);
                    App.MainWindow.ShowInfo($"Wiedergabeliste „{name}“ angelegt.");
                }
                catch (Exception ex)
                {
                    App.MainWindow.ShowError($"Wiedergabeliste konnte nicht angelegt werden: {ex.Message}");
                }
            };
            flyout.Items.Add(create);
        };
        return flyout;
    }
}
