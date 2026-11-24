using Lagerfeuer.Models;

namespace Lagerfeuer.Services;

/// <summary>Regeln zum angemeldeten Nutzer und seinen Rechten (#81).</summary>
public static class UserRules
{
    /// <summary>
    /// Gilt eine Meldung mit Nutzerdaten (Live-Meldung <c>user_updated</c>, Antwort von <c>/api/me</c>) dem angemeldeten
    /// Nutzer? Audiobookshelf schickt <c>user_updated</c> auch dem Verwalter, der einen anderen Nutzer ändert – mit
    /// dessen Daten. Die dürfen Fortschritt, Lesezeichen und Rechte des angemeldeten Nutzers nie ersetzen.
    /// </summary>
    public static bool IsCurrentUser(AbsUser? current, AbsUser update)
        => current is not null && current.Id.Length > 0 && string.Equals(update.Id, current.Id, StringComparison.Ordinal);
}

/// <summary>
/// Wann Lagerfeuer die Rechte neu nachfragt (#81). Ändert ein Verwalter die Rechte eines Nutzers, meldet Audiobookshelf
/// (2.37) das nur dem Verwalter selbst, nicht dem betroffenen Nutzer. Lagerfeuer fragt deshalb nach, wenn Knöpfe für
/// Sammlungen erscheinen oder das Fenster wieder aktiv wird – höchstens einmal je <see cref="MinInterval"/>.
/// </summary>
public sealed class RightsRefreshThrottle(TimeSpan minInterval, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset? _last;

    public TimeSpan MinInterval { get; } = minInterval;

    /// <summary>True, wenn jetzt nachgefragt werden soll; der Zeitpunkt zählt dann als letzte Nachfrage.</summary>
    public bool TryStart()
    {
        var now = _clock();
        // Eine zurückgestellte Uhr soll das Nachfragen nicht für lange Zeit sperren.
        if (_last is { } last && now >= last && now - last < MinInterval) return false;
        _last = now;
        return true;
    }
}

/// <summary>
/// Welche Angabe zu den Rechten die jüngste ist (#87). Jede Nachfrage bei <c>/api/me</c> zieht beim Start eine
/// Nummer, ebenso jede Live-Meldung <c>user_updated</c>, die Rechte trägt. Eine Antwort gilt nur, wenn seit ihrer
/// Nachfrage nichts Jüngeres kam: Sonst überschriebe eine ältere Antwort, die als letzte ankommt, neuere Rechte.
/// </summary>
public sealed class RightsSequence
{
    private long _last;

    /// <summary>Eine Nachfrage beginnt: ihre Nummer für <see cref="TakeAnswer"/>.</summary>
    public long Begin() => Interlocked.Increment(ref _last);

    /// <summary>
    /// Übernimmt Typ und Rechte aus der Antwort der Nachfrage <paramref name="ticket"/>, wenn sie noch die jüngste
    /// ist. Liefert wie <see cref="CollectionRules.TakeRights"/>, ob sich etwas geändert hat.
    /// </summary>
    public bool TakeAnswer(long ticket, AbsUser target, AbsUser answer)
        => Interlocked.Read(ref _last) == ticket && CollectionRules.TakeRights(target, answer);

    /// <summary>
    /// Übernimmt Typ und Rechte aus einer Live-Meldung sofort. Trägt sie welche, gelten Antworten auf Nachfragen, die
    /// vorher begonnen haben, nicht mehr. Eine Meldung ohne Rechte ändert nichts.
    /// </summary>
    public bool TakeUpdate(AbsUser target, AbsUser update)
    {
        if (update.Type is null || update.Permissions is null) return false;
        Interlocked.Increment(ref _last);
        return CollectionRules.TakeRights(target, update);
    }
}
