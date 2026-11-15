using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lagerfeuer.Views;

/// <summary>Dialog zum Umbenennen eines Lesezeichens – von der Buchseite und aus der Meldung „Lesezeichen gesetzt“.</summary>
internal static class BookmarkRename
{
    public static async Task RunAsync(XamlRoot root, Bookmark bookmark)
    {
        if (App.IsOffline)
        {
            App.MainWindow.ShowError("Lesezeichen können nur mit Server-Verbindung umbenannt werden.");
            return;
        }

        var box = new TextBox
        {
            Text = bookmark.Title,
            MaxLength = BookmarkTitles.MaxLength,
            Header = $"Lesezeichen bei {Format.Time(bookmark.Time)}",
        };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Lesezeichen umbenennen",
            Content = box,
            PrimaryButtonText = "Umbenennen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (BookmarkTitles.Normalize(box.Text, bookmark.Title) is not { } title) return;

        try
        {
            var renamed = await App.Api.RenameBookmarkAsync(bookmark.LibraryItemId, bookmark.Time, title);
            if (App.User is { } user) BookmarkTitles.Upsert(user.Bookmarks, renamed);
            App.RaiseBookmarksChanged();
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Lesezeichen konnte nicht umbenannt werden: {ex.Message}");
        }
    }
}
