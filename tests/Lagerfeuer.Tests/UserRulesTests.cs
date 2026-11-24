using Lagerfeuer.Models;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>Meldungen mit Nutzerdaten und das Nachfragen der Rechte (#81).</summary>
public class UserRulesTests
{
    [Fact]
    public void Meldung_ZumAngemeldetenNutzer_Gilt()
        => Assert.True(UserRules.IsCurrentUser(new AbsUser { Id = "u1" }, new AbsUser { Id = "u1" }));

    [Theory]
    [InlineData("u1", "u2")] // ein Verwalter ändert einen anderen Nutzer: Audiobookshelf schickt ihm dessen Daten
    [InlineData("u1", "")]
    [InlineData("", "")]
    [InlineData("u1", "U1")]
    public void Meldung_ZuEinemAnderenNutzer_GiltNicht(string current, string update)
        => Assert.False(UserRules.IsCurrentUser(new AbsUser { Id = current }, new AbsUser { Id = update }));

    [Fact]
    public void Meldung_OhneAngemeldetenNutzer_GiltNicht()
        => Assert.False(UserRules.IsCurrentUser(null, new AbsUser { Id = "u1" }));

    [Fact]
    public void Nachfragen_HoechstensEinmalJeAbstand()
    {
        var now = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
        var throttle = new RightsRefreshThrottle(TimeSpan.FromSeconds(30), () => now);

        Assert.True(throttle.TryStart());
        now = now.AddSeconds(10);
        Assert.False(throttle.TryStart());
        now = now.AddSeconds(25);
        Assert.True(throttle.TryStart());
        Assert.False(throttle.TryStart());
    }

    [Fact]
    public void Nachfragen_UhrZurueckgestellt_SperrtNicht()
    {
        var now = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
        var throttle = new RightsRefreshThrottle(TimeSpan.FromSeconds(30), () => now);

        Assert.True(throttle.TryStart());
        now = now.AddMinutes(-5);
        Assert.True(throttle.TryStart());
    }

    private static AbsUser Rights(bool update, bool delete)
        => new() { Id = "u1", Type = "user", Permissions = new UserPermissions { Update = update, Delete = delete } };

    [Fact]
    public void Antwort_EinerAelterenNachfrage_UeberschreibtNeuereRechteNicht()
    {
        var order = new RightsSequence();
        var user = Rights(false, false);
        var older = order.Begin();
        var newer = order.Begin();

        Assert.True(order.TakeAnswer(newer, user, Rights(true, false)));
        Assert.False(order.TakeAnswer(older, user, Rights(false, false))); // kommt als letzte an (#87)

        Assert.Equal(new CollectionRules.Rights(true, false), CollectionRules.For(user));
    }

    [Fact]
    public void Antwort_NachEinerLiveMeldungMitRechten_GiltNicht()
    {
        var order = new RightsSequence();
        var user = Rights(true, true);
        var ticket = order.Begin();

        Assert.True(order.TakeUpdate(user, Rights(false, false)));
        Assert.False(order.TakeAnswer(ticket, user, Rights(true, true)));

        Assert.Equal(new CollectionRules.Rights(false, false), CollectionRules.For(user));
    }

    [Fact]
    public void LiveMeldungOhneRechte_LaesstDieLaufendeNachfrageGelten()
    {
        var order = new RightsSequence();
        var user = Rights(false, false);
        var ticket = order.Begin();

        Assert.False(order.TakeUpdate(user, new AbsUser { Id = "u1" }));
        Assert.True(order.TakeAnswer(ticket, user, Rights(true, true)));

        Assert.Equal(new CollectionRules.Rights(true, true), CollectionRules.For(user));
    }
}
