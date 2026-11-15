using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

public class LibraryViewsTests
{
    [Fact]
    public void Buchbibliothek_HatBuecherSerienAutoren()
    {
        Assert.Equal([LibraryViews.Books, LibraryViews.Series, LibraryViews.Authors], LibraryViews.For("book"));
        Assert.True(LibraryViews.ShowSelector("book"));
    }

    [Fact]
    public void Podcastbibliothek_ZeigtKeinenReiterBuecher()
    {
        Assert.Empty(LibraryViews.For("podcast"));
        Assert.False(LibraryViews.ShowSelector("podcast"));
    }

    [Fact]
    public void UnbekannterTyp_WieBuecher()
        => Assert.True(LibraryViews.ShowSelector(null));
}
