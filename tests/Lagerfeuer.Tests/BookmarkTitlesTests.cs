using System.Text.Json;
using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class BookmarkTitlesTests
{
    [Theory]
    [InlineData("Neuer Titel", "Teil zwei", "Neuer Titel")]
    [InlineData("  Rand  ", "alt", "Rand")]
    [InlineData("zwei\r\nZeilen", "alt", "zwei Zeilen")]
    [InlineData("viel   \t Platz", "alt", "viel Platz")]
    [InlineData("mit\u0007Steuerzeichen", "alt", "mit Steuerzeichen")]
    public void Normalize_BereinigtDenTitel(string input, string current, string expected)
        => Assert.Equal(expected, BookmarkTitles.Normalize(input, current));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    [InlineData("Teil zwei")]
    [InlineData("  Teil   zwei ")]
    public void Normalize_LeerOderUnveraendert_NichtsZuSpeichern(string? input)
        => Assert.Null(BookmarkTitles.Normalize(input, "Teil zwei"));

    [Fact]
    public void Normalize_SchneidetLangeTitelAb()
    {
        var title = BookmarkTitles.Normalize(new string('a', 500), "alt");
        Assert.Equal(BookmarkTitles.MaxLength, title!.Length);
    }

    [Fact]
    public void Normalize_TrenntKeinZeichenAusZweiHaelften()
    {
        // Ein Emoji besteht in UTF-16 aus zwei Hälften; die Grenze fällt genau zwischen sie.
        var input = new string('a', BookmarkTitles.MaxLength - 1) + "🔥";
        var title = BookmarkTitles.Normalize(input, "alt")!;
        Assert.Equal(BookmarkTitles.MaxLength - 1, title.Length);
        Assert.False(char.IsHighSurrogate(title[^1]));
    }

    [Fact]
    public void Upsert_ErsetztLesezeichenAnDerselbenStelle()
    {
        var list = new List<Bookmark>
        {
            new() { LibraryItemId = "buch", Time = 39, Title = "Teil zwei" },
            new() { LibraryItemId = "buch", Time = 90, Title = "anderes" },
            new() { LibraryItemId = "anderes-buch", Time = 39, Title = "fremd" },
        };

        BookmarkTitles.Upsert(list, new Bookmark { LibraryItemId = "buch", Time = 39, Title = "Neu" });

        Assert.Equal(3, list.Count);
        Assert.Equal("Neu", list.Single(b => b.LibraryItemId == "buch" && b.Time == 39).Title);
        Assert.Equal("anderes", list.Single(b => b.Time == 90).Title);
        Assert.Equal("fremd", list.Single(b => b.LibraryItemId == "anderes-buch").Title);
    }

    [Theory]
    [InlineData("buch", null, true)]
    [InlineData("podcast", "folge", false)]
    [InlineData(null, null, false)]
    public void CanSet_NurBeiBuechern(string? itemId, string? episodeId, bool expected)
        => Assert.Equal(expected, BookmarkTitles.CanSet(itemId, episodeId));

    [Fact]
    public async Task Umbenennen_SchicktPatchMitZeitUndNeuemTitel()
    {
        string? body = null;
        var handler = new FakeHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return FakeHandler.Json("""{"libraryItemId":"buch","title":"Neu","time":39.5,"createdAt":1}""");
        });
        using var api = new AbsApiClient(handler);
        api.Configure(new Uri("https://abs.test/"), "token", null);
        var list = new List<Bookmark> { new() { LibraryItemId = "buch", Time = 39.5, Title = "Teil zwei" } };

        var renamed = await api.RenameBookmarkAsync("buch", 39.5, "Neu", TestContext.Current.CancellationToken);
        BookmarkTitles.Upsert(list, renamed);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/me/item/buch/bookmark", request.RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(39.5, json.RootElement.GetProperty("time").GetDouble());
        Assert.Equal("Neu", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("Neu", list.Single().Title);
    }
}
