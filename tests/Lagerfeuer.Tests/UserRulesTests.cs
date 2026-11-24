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
}
