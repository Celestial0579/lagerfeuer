using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lagerfeuer.Views;

/// <summary>Dialoge und das Menü „Zur Wiedergabeliste hinzufügen …“ (#36).</summary>
public static class PlaylistUi
{
    /// <summary>Fragt nach einem Namen. Null bei Abbruch, leerer Eingabe oder unverändertem Namen.</summary>
    public static async Task<string?> AskNameAsync(XamlRoot root, string title, string primary, string current = "",
        string placeholder = "Name der Wiedergabeliste")
    {
        var box = new TextBox
        {
            Text = current,
            MaxLength = PlaylistRules.MaxNameLength,
            PlaceholderText = placeholder,
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

    /// <summary>
    /// Menü „Zur Sammlung …“ (#35): Sammlungen der Bibliothek (Haken, wo das Buch schon drin ist) und
    /// „Neue Sammlung …“. Nur anbieten, wenn der Nutzer das Recht „Aktualisieren“ hat.
    /// </summary>
    public static MenuFlyout CreateCollectionFlyout(Func<(string LibraryId, string BookId)?> target, XamlRoot? root = null)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += async (_, _) =>
        {
            flyout.Items.Clear();
            if (target() is not { } t) return;
            var loading = new MenuFlyoutItem { Text = "Wird geladen …", IsEnabled = false };
            flyout.Items.Add(loading);
            List<Collection> collections;
            try
            {
                collections = await App.Api.GetCollectionsAsync(t.LibraryId);
            }
            catch (Exception ex)
            {
                loading.Text = "Sammlungen nicht verfügbar";
                Log.Error("Sammlungen konnten nicht geladen werden", ex);
                return;
            }
            flyout.Items.Clear();
            foreach (var c in collections.Where(c => PlaylistRules.IsPlainId(c.Id)).OrderBy(CollectionRules.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var contained = CollectionRules.Contains(c, t.BookId);
                var item = new ToggleMenuFlyoutItem { Text = CollectionRules.DisplayName(c), IsChecked = contained, IsEnabled = !contained };
                item.Click += async (_, _) =>
                {
                    try
                    {
                        await App.Api.AddToCollectionAsync(c.Id, t.BookId);
                        App.MainWindow.ShowInfo($"Zu „{CollectionRules.DisplayName(c)}“ hinzugefügt.");
                    }
                    catch (Exception ex)
                    {
                        await ReportCollectionErrorAsync("Konnte nicht hinzugefügt werden", ex);
                    }
                };
                flyout.Items.Add(item);
            }
            if (flyout.Items.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = "Neue Sammlung …", Icon = new FontIcon { Glyph = "\uE710" } };
            create.Click += async (_, _) =>
            {
                if (await AskNameAsync(root ?? App.MainWindow.Content.XamlRoot, "Neue Sammlung", "Anlegen", "", "Name der Sammlung") is not { } name) return;
                try
                {
                    await App.Api.CreateCollectionAsync(t.LibraryId, name, t.BookId);
                    App.MainWindow.ShowInfo($"Sammlung „{name}“ angelegt.");
                }
                catch (Exception ex)
                {
                    await ReportCollectionErrorAsync("Sammlung konnte nicht angelegt werden", ex);
                }
            };
            flyout.Items.Add(create);
        };
        return flyout;
    }

    /// <summary>
    /// Menü „Zur Sammlung …“ für mehrere Bücher auf einmal (#65): vorhandene Sammlungen und „Neue Sammlung …“.
    /// Danach meldet Lagerfeuer, wie viele hinzugekommen sind, schon drin waren oder nicht übernommen wurden.
    /// </summary>
    public static MenuFlyout CreateBatchCollectionFlyout(Func<(string LibraryId, IReadOnlyList<string> BookIds)?> target, XamlRoot root,
        Action? done = null)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += async (_, _) =>
        {
            flyout.Items.Clear();
            if (target() is not { } t) return;
            var loading = new MenuFlyoutItem { Text = "Wird geladen …", IsEnabled = false };
            flyout.Items.Add(loading);
            List<Collection> collections;
            try
            {
                collections = await App.Api.GetCollectionsAsync(t.LibraryId);
            }
            catch (Exception ex)
            {
                loading.Text = "Sammlungen nicht verfügbar";
                Log.Error("Sammlungen konnten nicht geladen werden", ex);
                return;
            }
            flyout.Items.Clear();
            foreach (var c in collections.Where(c => PlaylistRules.IsPlainId(c.Id)).OrderBy(CollectionRules.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new MenuFlyoutItem { Text = CollectionRules.DisplayName(c) };
                item.Click += async (_, _) =>
                {
                    try
                    {
                        var after = await App.Api.AddBooksToCollectionAsync(c.Id, t.BookIds);
                        var result = BatchRules.Summarize(t.BookIds, c.Books.Select(b => b.Id), after.Books.Select(b => b.Id));
                        App.MainWindow.ShowInfo(BatchRules.Message(result, CollectionRules.DisplayName(c)));
                        done?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        await ReportCollectionErrorAsync("Konnte nicht hinzugefügt werden", ex);
                    }
                };
                flyout.Items.Add(item);
            }
            if (flyout.Items.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = "Neue Sammlung …", Icon = new FontIcon { Glyph = "\uE710" } };
            create.Click += async (_, _) =>
            {
                if (await AskNameAsync(root, "Neue Sammlung", "Anlegen", "", "Name der Sammlung") is not { } name) return;
                try
                {
                    var created = await App.Api.CreateCollectionAsync(t.LibraryId, name, t.BookIds);
                    var result = BatchRules.Summarize(t.BookIds, [], created.Books.Select(b => b.Id));
                    App.MainWindow.ShowInfo(BatchRules.Message(result, CollectionRules.DisplayName(created)));
                    done?.Invoke();
                }
                catch (Exception ex)
                {
                    await ReportCollectionErrorAsync("Sammlung konnte nicht angelegt werden", ex);
                }
            };
            flyout.Items.Add(create);
        };
        return flyout;
    }

    /// <summary>Menü „Zur Wiedergabeliste …“ für mehrere Bücher auf einmal (#65): eigene Listen und „Neue Liste …“.</summary>
    public static MenuFlyout CreateBatchPlaylistFlyout(Func<(string LibraryId, IReadOnlyList<string> BookIds)?> target, XamlRoot root,
        Action? done = null)
    {
        static IEnumerable<string> BookIds(Playlist p) => p.Items.Where(i => i.EpisodeId is null).Select(i => i.LibraryItemId);
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
                var item = new MenuFlyoutItem { Text = PlaylistRules.DisplayName(list) };
                item.Click += async (_, _) =>
                {
                    try
                    {
                        var after = await App.Api.AddBooksToPlaylistAsync(list.Id, t.BookIds);
                        var result = BatchRules.Summarize(t.BookIds, BookIds(list), BookIds(after));
                        App.MainWindow.ShowInfo(BatchRules.Message(result, PlaylistRules.DisplayName(list)));
                        done?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        App.MainWindow.ShowError($"Konnte nicht hinzugefügt werden: {ex.Message}");
                    }
                };
                flyout.Items.Add(item);
            }
            if (flyout.Items.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = "Neue Liste …", Icon = new FontIcon { Glyph = "\uE710" } };
            create.Click += async (_, _) =>
            {
                if (await AskNameAsync(root, "Neue Wiedergabeliste", "Anlegen") is not { } name) return;
                try
                {
                    var created = await App.Api.CreatePlaylistAsync(t.LibraryId, name, t.BookIds);
                    var result = BatchRules.Summarize(t.BookIds, [], BookIds(created));
                    App.MainWindow.ShowInfo(BatchRules.Message(result, PlaylistRules.DisplayName(created)));
                    done?.Invoke();
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

    /// <summary>Bei 403 die Rechte neu holen, damit die Knöpfe verschwinden, und das klar sagen.</summary>
    public static async Task ReportCollectionErrorAsync(string what, Exception ex)
    {
        if (ex is AbsApiException { Status: System.Net.HttpStatusCode.Forbidden })
        {
            await App.RefreshRightsAsync();
            App.MainWindow.ShowError($"{what}: Dafür fehlt dir am Server inzwischen das Recht.");
            CollectionRightsChanged?.Invoke(null, EventArgs.Empty);
        }
        else
        {
            App.MainWindow.ShowError($"{what}: {ex.Message}");
        }
    }

    /// <summary>Nach einem 403 neu geholte Rechte – offene Seiten passen ihre Knöpfe an.</summary>
    public static event EventHandler? CollectionRightsChanged;
}
