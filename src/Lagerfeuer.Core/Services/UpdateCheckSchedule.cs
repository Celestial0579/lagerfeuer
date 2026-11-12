namespace Lagerfeuer.Services;

/// <summary>
/// Wann Lagerfeuer nach Updates sucht: einmal kurz nach dem Start, danach in festem Abstand.
///
/// Früher erledigte das ein Zeitgeber der Oberfläche, der im ersten Durchlauf von 8 Sekunden auf 12 Stunden
/// umgestellt wurde. Der neue Abstand griff aber erst nach dem zweiten Durchlauf: Lagerfeuer fragte GitHub bei
/// jedem Start zweimal, nach 8 und nach 16 Sekunden. Hier wartet eine Schleife erst die eine Zeit ab, dann
/// immer die andere.
/// </summary>
public static class UpdateCheckSchedule
{
    /// <summary>Wartezeit vor der ersten Prüfung. Die App soll erst einmal stehen.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(8);

    /// <summary>Abstand der weiteren Prüfungen, solange die App läuft.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>
    /// Ruft <paramref name="check"/> einmal nach <see cref="FirstDelay"/> auf und danach alle
    /// <see cref="Interval"/>, bis <paramref name="ct"/> abbricht.
    /// Bewusst ohne ConfigureAwait(false): Die Prüfung läuft im Kontext des Aufrufers, in der App ist das der
    /// Thread der Oberfläche.
    /// </summary>
    /// <param name="check">Die Prüfung. Scheitert sie, geht es nach dem nächsten Abstand weiter.</param>
    /// <param name="delay">Für Tests: ersetzt <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="ct">Beendet die Schleife.</param>
    public static async Task RunAsync(Func<Task> check, Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        delay ??= Task.Delay;
        var wait = FirstDelay;
        while (true)
        {
            await delay(wait, ct);
            try
            {
                await check();
            }
            catch (Exception ex)
            {
                Log.Error("Update-Prüfung fehlgeschlagen", ex);
            }
            wait = Interval;
        }
    }
}
