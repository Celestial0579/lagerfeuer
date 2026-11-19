namespace Lagerfeuer.Services;

/// <summary>Eine Wiedergabe-Session, wie sie der Abgleich mit dem Server kennt.</summary>
public sealed class SessionTicket
{
    internal SessionTicket(string sessionId, string itemId, string? episodeId, double duration)
    {
        SessionId = sessionId;
        ItemId = itemId;
        EpisodeId = episodeId;
        Duration = duration;
    }

    /// <summary>Kann sich ändern, wenn der Server die Session verloren hat und eine neue vergibt.</summary>
    public string SessionId { get; set; }
    public string ItemId { get; }
    public string? EpisodeId { get; }
    public double Duration { get; }

    /// <summary>Gehörte Sekunden, die ein fehlgeschlagener Abgleich nicht losgeworden ist – nur für diese Session.</summary>
    public double CarryOverListened { get; set; }

    /// <summary>Die Session ist geschlossen. Danach geht für sie nichts mehr an den Server.</summary>
    public bool Ended { get; internal set; }
}

/// <summary>
/// Hält den Fortschrittsabgleich jeder Session bei ihrer eigenen Session und ihrem eigenen Titel (#36).
/// Beim Wechsel auf einen anderen Titel (etwa automatisch in einer Wiedergabeliste) darf eine verspätete
/// Meldung des alten Titels weder den neuen noch den gerade geschlossenen alten Stand überschreiben.
/// Genau das passiert in der Android-App (audiobookshelf-app#1491).
/// <para>
/// Regeln: Abgleiche einer Session laufen nacheinander. <see cref="EndAsync"/> wartet auf einen laufenden
/// Abgleich und schließt die Session danach mit dem letzten Stand. Erst dann darf die nächste Session
/// beginnen. Abgleiche einer beendeten oder nicht mehr aktuellen Session werden nicht mehr geschickt, und
/// ihre Rückmeldungen gelten als veraltet.
/// </para>
/// </summary>
public sealed class SessionSyncGuard
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionTicket? _current;
    private readonly Queue<string> _ownIds = new();

    public SessionTicket? Current => _current;

    /// <summary>
    /// Stammt eine Live-Meldung von einer Session dieses Geräts (auch einer schon geschlossenen)? Solche
    /// Meldungen kommen oft verspätet und dürfen den hier bekannten Stand nicht zurücksetzen.
    /// </summary>
    public bool IsOwnSession(string? sessionId)
    {
        if (sessionId is null) return false;
        lock (_ownIds) return _ownIds.Contains(sessionId) || _current?.SessionId == sessionId;
    }

    /// <summary>Merkt sich eine Session-ID als eigene (die letzten 50).</summary>
    public void RememberOwn(string sessionId)
    {
        lock (_ownIds)
        {
            if (_ownIds.Contains(sessionId)) return;
            _ownIds.Enqueue(sessionId);
            while (_ownIds.Count > 50) _ownIds.Dequeue();
        }
    }

    /// <summary>
    /// Beginnt eine neue Session. Die vorige muss vorher mit <see cref="EndAsync"/> geschlossen sein,
    /// sonst ist das ein Programmfehler.
    /// </summary>
    public SessionTicket Begin(string sessionId, string itemId, string? episodeId, double duration)
    {
        if (_current is { Ended: false })
            throw new InvalidOperationException("Die vorige Session ist noch offen.");
        RememberOwn(sessionId);
        return _current = new SessionTicket(sessionId, itemId, episodeId, duration);
    }

    /// <summary>Gilt eine Rückmeldung dieser Session noch? (Nicht beendet und noch die aktuelle.)</summary>
    public bool IsCurrent(SessionTicket? ticket) => ticket is not null && ReferenceEquals(ticket, _current) && !ticket.Ended;

    /// <summary>
    /// Führt einen Abgleich aus, wenn die Session dann noch aktuell ist. Läuft schon einer, wird gewartet.
    /// Liefert false, wenn nichts geschickt wurde.
    /// </summary>
    public async Task<bool> SyncAsync(SessionTicket ticket, Func<SessionTicket, Task> send)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsCurrent(ticket)) return false;
            await send(ticket);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Schließt die Session: wartet auf einen laufenden Abgleich, schickt dann den letzten Stand und markiert
    /// die Session als beendet – auch wenn das Schließen scheitert. Zweites Schließen tut nichts.
    /// </summary>
    public async Task<bool> EndAsync(SessionTicket ticket, Func<SessionTicket, Task> close)
    {
        await _gate.WaitAsync();
        try
        {
            if (ticket.Ended) return false;
            try { await close(ticket); }
            finally { ticket.Ended = true; }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
