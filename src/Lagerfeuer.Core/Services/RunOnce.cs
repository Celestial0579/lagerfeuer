namespace Lagerfeuer.Services;

/// <summary>
/// Führt einen Vorgang höchstens einmal aus. Jeder weitere Aufruf bekommt denselben laufenden oder schon
/// beendeten Vorgang zurück, statt ihn neu zu beginnen.
///
/// Gebraucht wird das beim Herunterfahren des Players: Es wird vom Schließen des Fensters und vom Update
/// angestoßen, und beides kann zusammenfallen. Zweimal ausgeführt hieße: die Wiedergabe-Sitzung am Server
/// zweimal schließen und den Player zweimal freigeben.
/// </summary>
public sealed class RunOnce(Func<Task> action)
{
    private readonly object _sync = new();
    private Task? _task;

    /// <summary>Ist der Vorgang schon angestoßen?</summary>
    public bool Started
    {
        get { lock (_sync) return _task is not null; }
    }

    public Task RunAsync()
    {
        lock (_sync)
        {
            if (_task is not null) return _task;
            try
            {
                return _task = action();
            }
            catch (Exception ex)
            {
                // Scheitert schon der Aufruf selbst, gilt er trotzdem als ausgeführt: nicht noch einmal.
                return _task = Task.FromException(ex);
            }
        }
    }
}
