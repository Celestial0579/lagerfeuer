using System.Diagnostics;

namespace Lagerfeuer.Services;

/// <summary>
/// Programme von Windows, die Lagerfeuer selbst startet. Sie werden immer mit vollem Pfad genannt und ohne
/// Umweg über die Shell gestartet.
///
/// Beim bloßen Namen sucht Windows zuerst im aktuellen Ordner und im Ordner der App. Bei der portablen EXE
/// ist das meist der Download-Ordner, und dort kann ein fremdes Programm gleichen Namens liegen: Es liefe
/// statt des echten.
/// </summary>
public static class SystemPrograms
{
    /// <summary>Startangaben, um einen Ordner im Explorer zu öffnen.</summary>
    /// <param name="folder">Voller Pfad des Ordners.</param>
    public static ProcessStartInfo OpenFolder(string folder)
        => OpenFolder(folder, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <param name="folder">Voller Pfad des Ordners.</param>
    /// <param name="windowsDirectory">Der Windows-Ordner; für Tests von außen vorgegeben.</param>
    internal static ProcessStartInfo OpenFolder(string folder, string windowsDirectory)
    {
        // Lieber nichts starten als auf den bloßen Namen zurückfallen.
        if (string.IsNullOrWhiteSpace(windowsDirectory) || !Path.IsPathFullyQualified(windowsDirectory))
            throw new InvalidOperationException("Der Windows-Ordner ließ sich nicht bestimmen – der Explorer wird nicht gestartet.");
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Der Ordner muss mit vollem Pfad angegeben sein.", nameof(folder));

        // UseShellExecute = false: Windows startet genau diese Datei und sucht nicht.
        var info = new ProcessStartInfo(Path.Combine(windowsDirectory, "explorer.exe")) { UseShellExecute = false };
        info.ArgumentList.Add(Path.TrimEndingDirectorySeparator(folder));
        return info;
    }
}
