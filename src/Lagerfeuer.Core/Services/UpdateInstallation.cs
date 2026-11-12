namespace Lagerfeuer.Services;

/// <summary>
/// Die Reihenfolge beim Einspielen eines Updates: erst das Update starten, dann die App herunterfahren.
///
/// Früher war es umgekehrt. Scheiterte dann der Start (Setup nicht ausführbar, Tausch der portablen EXE
/// misslungen), war der Player schon heruntergefahren: Die App lief weiter, spielte aber bis zum Neustart
/// nichts mehr ab, und auch ein zweiter Versuch scheiterte daran.
///
/// Dass die App nach dem Start noch kurz lebt, ist eingeplant: Das Setup (/RESTART) und die neue portable
/// Fassung (--after-update) warten jeweils bis zu 15 Sekunden auf ihr Ende, und das Herunterfahren des
/// Players ist auf 4 Sekunden begrenzt.
///
/// Der Start selbst läuft nicht im Thread des Aufrufers. Windows prüft eine frisch geladene EXE, bevor es sie
/// startet, und das kann viele Sekunden dauern (in der Test-VM 17). So lange bliebe sonst das Fenster stehen,
/// und die Fortschrittsmeldung des Players fiele aus.
/// </summary>
public static class UpdateInstallation
{
    /// <param name="launch">Startet das Update (<see cref="UpdateService.LaunchInstaller"/>). Wirft, wenn das nicht gelingt.</param>
    /// <param name="shutdownAsync">Fährt die App herunter: Wiedergabe beenden und den Fortschritt melden.</param>
    /// <param name="exit">Beendet die App.</param>
    public static async Task RunAsync(Action launch, Func<Task> shutdownAsync, Action exit)
    {
        // Scheitert der Start, fliegt die Ausnahme hier hinaus und nichts ist heruntergefahren:
        // Die App läuft weiter, die Wiedergabe auch, und der Versuch lässt sich wiederholen.
        // Im Hintergrund, damit der Aufrufer (in der App der Thread der Oberfläche) währenddessen frei bleibt.
        await Task.Run(launch);

        // Ab hier wartet das Update darauf, dass diese Instanz endet. Beendet wird deshalb in jedem Fall,
        // auch wenn das Herunterfahren hakt.
        // Bewusst ohne ConfigureAwait(false): Herunterfahren und exit laufen wieder im Kontext des Aufrufers,
        // in der App ist das der Thread der Oberfläche.
        try
        {
            await shutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Fehler beim Herunterfahren vor dem Update", ex);
        }
        exit();
    }
}
