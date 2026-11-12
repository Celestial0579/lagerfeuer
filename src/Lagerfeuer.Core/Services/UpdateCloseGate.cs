namespace Lagerfeuer.Services;

/// <summary>Was mit dem Wunsch geschieht, das Fenster zu schließen.</summary>
public enum CloseRequest
{
    /// <summary>Herunterfahren und schließen.</summary>
    Proceed,

    /// <summary>Das Schließen läuft schon; ein weiterer Klick stößt nichts Neues an.</summary>
    AlreadyClosing,

    /// <summary>Gerade startet ein Update. Das Fenster bleibt, die App beendet sich danach selbst.</summary>
    NotNow,
}

/// <summary>
/// Verzahnt das Schließen des Fensters mit dem Einspielen eines Updates.
///
/// Das Update startet im Hintergrund (<see cref="UpdateInstallation.RunAsync"/>), das Fenster bleibt so lange
/// bedienbar. Schlösse es jemand in diesen Sekunden, endete der Prozess mitten im Start. Bei der portablen
/// Fassung ist die EXE dann schon getauscht, die neue aber vielleicht noch nicht gestartet – und startet sie
/// nicht, wird der Rückweg zur bisherigen nicht mehr gegangen. Darum gilt:
///
/// - Während ein Update startet, schließt das Fenster nicht. Die App beendet sich danach selbst. Scheitert der
///   Start, bleibt sie offen und lässt sich wieder schließen.
/// - Schließt das Fenster schon, beginnt kein Update mehr. Es wird beim nächsten Start wieder angeboten.
/// </summary>
public sealed class UpdateCloseGate
{
    private readonly object _sync = new();
    private bool _closing;
    private bool _installing;

    /// <summary>Das Fenster soll schließen.</summary>
    public CloseRequest RequestClose()
    {
        lock (_sync)
        {
            if (_installing) return CloseRequest.NotNow;
            if (_closing) return CloseRequest.AlreadyClosing;
            _closing = true;
            return CloseRequest.Proceed;
        }
    }

    /// <summary>
    /// Ein Update soll starten. False: Das Fenster schließt schon, oder es startet bereits eines – dann beginnt
    /// kein weiteres.
    /// </summary>
    public bool TryBeginInstall()
    {
        lock (_sync)
        {
            if (_closing || _installing) return false;
            _installing = true;
            return true;
        }
    }

    /// <summary>Der Start ist gescheitert: Die App läuft weiter und lässt sich wieder schließen.</summary>
    public void InstallFailed()
    {
        lock (_sync) _installing = false;
    }
}
