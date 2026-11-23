using System.Collections.ObjectModel;
using System.Net;
using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lagerfeuer.Views;

/// <summary>Ein Buch der Sammlung. Texte bereinigt, Bearbeiten nur mit Recht.</summary>
public sealed class CollectionEntry(LibraryItem book, bool canEdit)
{
    public LibraryItem Book { get; } = book;
    public string Id => Book.Id;
    public string Title { get; } = PlaylistRules.DisplayTitle(new PlaylistItem { LibraryItemId = book.Id, LibraryItem = book });
    public string Author { get; } = PlaylistRules.DisplaySubtitle(new PlaylistItem { LibraryItemId = book.Id, LibraryItem = book });
    public bool CanEdit { get; } = canEdit;
    public Visibility EditVisibility => CanEdit ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Eine Sammlung (#35): alle Nutzer der Bibliothek sehen sie. Umbenennen, Ordnen und Entfernen nur mit dem Recht
/// „Aktualisieren“, Löschen nur mit „Löschen“. Meldet der Server trotzdem 403, werden die Rechte neu geholt.
/// </summary>
public sealed partial class CollectionPage : Page
{
    private readonly ObservableCollection<CollectionEntry> _entries = [];
    private Collection? _collection;
    private string? _collectionId;

    /// <summary>Eigene Änderungen, die gerade laufen. Solange lösen Live-Meldungen kein Neuladen aus.</summary>
    private int _busy;

    /// <summary>Reihenfolgen gehen nacheinander an den Server, in der Reihenfolge der Klicks.</summary>
    private readonly SemaphoreSlim _orderLock = new(1, 1);
    private int _ordersQueued;

    public CollectionPage()
    {
        InitializeComponent();
        List.ItemsSource = _entries;
        App.CollectionChanged += OnCollectionChanged;
        PlaylistUi.CollectionRightsChanged += OnRightsChanged;
        Unloaded += (_, _) =>
        {
            App.CollectionChanged -= OnCollectionChanged;
            PlaylistUi.CollectionRightsChanged -= OnRightsChanged;
        };
    }

    /// <summary>Die Sammlung gibt es nicht mehr: zurück, sonst zur Bibliothek (#82).</summary>
    private void Leave()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else App.MainWindow.OpenLibrary(_collection?.LibraryId);
    }

    /// <summary>Rechte geändert (#81): Knöpfe neu, solange keine eigene Änderung läuft (die zeigt danach ohnehin neu).</summary>
    private void OnRightsChanged(object? sender, EventArgs e)
    {
        if (_collection is not null && _busy == 0) Show(_collection);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not string id || !PlaylistRules.IsPlainId(id)) return;
        _collectionId = id;
        _ = LoadAsync();
    }

    private void OnCollectionChanged(object? sender, CollectionChange change)
    {
        // Nur solange die Seite noch angezeigt wird: Nach dem eigenen Löschen kann die Live-Meldung dazu erst
        // ankommen, wenn Leave() schon zurückgegangen ist – sonst ginge es ein zweites Mal zurück.
        if (change.CollectionId != _collectionId || _busy > 0 || !ReferenceEquals(Frame?.Content, this)) return;
        if (change.Kind == CollectionChangeKind.Removed)
        {
            App.MainWindow.ShowInfo("Diese Sammlung wurde gelöscht.");
            Leave();
            return;
        }
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_collectionId is null) return;
        try
        {
            Show(await App.Api.GetCollectionAsync(_collectionId));
            ContentRoot.Visibility = Visibility.Visible;
            App.MainWindow.ClearError(MessageOrigins.Collection);
        }
        catch (AbsApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            App.MainWindow.ShowError("Diese Sammlung gibt es nicht mehr.", MessageOrigins.Collection);
        }
        catch (Exception ex)
        {
            App.MainWindow.ShowError($"Sammlung konnte nicht geladen werden: {ex.Message}", MessageOrigins.Collection);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void Show(Collection collection)
    {
        _collection = collection;
        var rights = App.CollectionRights;
        TitleText.Text = CollectionRules.DisplayName(collection);
        DescriptionText.Text = CollectionRules.DisplayDescription(collection);
        DescriptionText.Visibility = DescriptionText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.Visibility = DescriptionButton.Visibility = rights.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = rights.CanDelete ? Visibility.Visible : Visibility.Collapsed;
        List.CanDragItems = List.CanReorderItems = List.AllowDrop = rights.CanEdit;
        _entries.Clear();
        foreach (var book in CollectionRules.Books(collection)) _entries.Add(new CollectionEntry(book, rights.CanEdit));
        CountText.Text = _entries.Count == 1 ? "1 Buch" : $"{_entries.Count} Bücher";
        PlayButton.IsEnabled = _entries.Count > 0;
    }

    /// <summary>403 trotz angezeigtem Knopf: Rechte haben sich geändert. Neu holen, Ansicht anpassen, Bescheid geben.</summary>
    private async Task HandleFailureAsync(string what, Exception ex)
    {
        await PlaylistUi.ReportCollectionErrorAsync(what, ex);
        await LoadAsync();
    }

    private static CollectionEntry? EntryOf(object sender) => ((FrameworkElement)sender).Tag as CollectionEntry;

    private void OnPlayAll(object sender, RoutedEventArgs e)
    {
        if (_collection is not null) _ = App.Player.PlayPlaylistAsync(CollectionRules.AsPlaylist(Visible()));
    }

    /// <summary>Was gerade angezeigt wird (nach Ziehen evtl. anders als zuletzt geladen).</summary>
    private Collection Visible() => new()
    {
        Id = _collection!.Id,
        LibraryId = _collection.LibraryId,
        Name = _collection.Name,
        Books = _entries.Select(x => x.Book).ToList(),
    };

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CollectionEntry entry) App.MainWindow.OpenItem(entry.Id);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) App.MainWindow.OpenItem(entry.Id);
    }

    private void OnPlayFrom(object sender, RoutedEventArgs e)
    {
        if (_collection is null || EntryOf(sender) is not { } entry) return;
        _ = App.Player.PlayPlaylistAsync(CollectionRules.AsPlaylist(Visible()), _entries.IndexOf(entry));
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => _ = MoveAsync(EntryOf(sender), -1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => _ = MoveAsync(EntryOf(sender), 1);

    private async Task MoveAsync(CollectionEntry? entry, int delta)
    {
        if (entry is null || !App.CollectionRights.CanEdit) return;
        var index = _entries.IndexOf(entry);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _entries.Count) return;
        _entries.Move(index, target);
        await SaveOrderAsync();
        if (List.ContainerFromItem(_entries.FirstOrDefault(x => x.Id == entry.Id)) is ListViewItem container) container.Focus(FocusState.Keyboard);
    }

    private void OnDragItemsCompleted(ListViewBase sender, Microsoft.UI.Xaml.Controls.DragItemsCompletedEventArgs args)
    {
        // Abgebrochenes Ziehen ändert nichts.
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.None) _ = SaveOrderAsync();
    }

    /// <summary>
    /// Schickt die angezeigte Reihenfolge. Mehrere schnelle Verschiebungen gehen nacheinander hinaus, jede mit dem
    /// Stand ihres Klicks; so überholt keine ältere Reihenfolge eine neuere am Server.
    /// </summary>
    private async Task SaveOrderAsync()
    {
        if (_collectionId is null) return;
        var order = _entries.Select(x => x.Id).ToList();
        _ordersQueued++;
        _busy++;
        await _orderLock.WaitAsync();
        try
        {
            var result = await App.Api.ReorderCollectionAsync(_collectionId, order);
            // Wartet schon die nächste Verschiebung, bleibt deren Stand sichtbar, bis sie gespeichert ist.
            if (!result.Saved || _ordersQueued == 1) Show(result.Current);
            if (!result.Saved)
                App.MainWindow.ShowInfo("Die Sammlung wurde inzwischen geändert. Die neue Reihenfolge ist nicht gespeichert – hier ist der aktuelle Stand.");
        }
        catch (Exception ex)
        {
            await HandleFailureAsync("Reihenfolge nicht gespeichert", ex);
        }
        finally
        {
            _ordersQueued--;
            _busy--;
            _orderLock.Release();
        }
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry || _collectionId is null || !App.CollectionRights.CanEdit) return;
        _busy++;
        try
        {
            Show(await App.Api.RemoveFromCollectionAsync(_collectionId, entry.Id));
        }
        catch (Exception ex)
        {
            await HandleFailureAsync("Buch nicht entfernt", ex);
        }
        finally
        {
            _busy--;
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_collection is null || !App.CollectionRights.CanEdit) return;
        var current = CollectionRules.DisplayName(_collection);
        if (await PlaylistUi.AskNameAsync(XamlRoot, "Sammlung umbenennen", "Umbenennen", current, "Name der Sammlung") is not { } name) return;
        _busy++;
        try
        {
            Show(await App.Api.RenameCollectionAsync(_collection.Id, name));
        }
        catch (Exception ex)
        {
            await HandleFailureAsync("Sammlung nicht umbenannt", ex);
        }
        finally
        {
            _busy--;
        }
    }

    /// <summary>Beschreibung bearbeiten (#35). Leeren entfernt sie; der Text geht bereinigt an den Server.</summary>
    private async void OnEditDescription(object sender, RoutedEventArgs e)
    {
        if (_collection is null || !App.CollectionRights.CanEdit) return;
        var current = CollectionRules.DisplayDescription(_collection);
        var box = new TextBox
        {
            Text = current,
            MaxLength = PlaylistRules.MaxDescriptionLength,
            PlaceholderText = "Beschreibung der Sammlung",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Beschreibung bearbeiten",
            Content = box,
            PrimaryButtonText = "Speichern",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (CollectionRules.NormalizeDescription(box.Text, current) is not { } description) return;
        _busy++;
        try
        {
            Show(await App.Api.SetCollectionDescriptionAsync(_collection.Id, description));
        }
        catch (Exception ex)
        {
            await HandleFailureAsync("Beschreibung nicht gespeichert", ex);
        }
        finally
        {
            _busy--;
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_collection is null || !App.CollectionRights.CanDelete) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Sammlung löschen?",
            Content = new TextBlock
            {
                Text = $"„{CollectionRules.DisplayName(_collection)}“ wird auf dem Server gelöscht – für alle Nutzer dieser Bibliothek. Die Bücher selbst bleiben.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Löschen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close, // Eingabetaste bricht ab
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _busy++;
        try
        {
            await App.Api.DeleteCollectionAsync(_collection.Id);
            Leave();
        }
        catch (Exception ex)
        {
            await HandleFailureAsync("Sammlung nicht gelöscht", ex);
        }
        finally
        {
            _busy--;
        }
    }
}
