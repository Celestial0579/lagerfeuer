# Datenschutz

Stand: 13.11.2026. Entwurf, noch nicht verbindlich: Wer als Verantwortlicher genannt wird, ist offen.

Lagerfeuer ist ein Programm für Windows, mit dem du Hörbücher und Podcasts von einem
Audiobookshelf-Server hörst. Den Server betreibst du selbst oder jemand, dem du vertraust.

## Kurz gesagt

- Lagerfeuer schickt keine Daten an die Entwickler von Lagerfeuer. Es gibt keine Nutzungsmessung, keine
  Werbung und keine Konten bei uns.
- Lagerfeuer spricht mit genau zwei Gegenstellen: mit deinem Audiobookshelf-Server und, für die Suche nach
  Updates, mit GitHub.

## Was an deinen Audiobookshelf-Server geht

Alles, was zum Hören nötig ist. Wer den Server betreibt, kann diese Daten sehen; für sie gilt, was dort
gilt.

- **Anmeldung:** Benutzername und Passwort, einmal beim Anmelden. Bei der SSO-Anmeldung gibst du deine
  Zugangsdaten im Browser beim Anmeldedienst ein, nicht in Lagerfeuer. Danach arbeitet Lagerfeuer mit Tokens,
  die der Server ausstellt.
- **Hören:** welches Buch und welche Folge du abspielst, deine Hörposition und Hörzeit, Lesezeichen, „als
  beendet markiert“.
- **Gerät:** eine zufällig erzeugte Kennung dieser Installation und der Name „Lagerfeuer“ samt Version und
  Windows-Fassung. Der Server zeigt damit an, von welchem Gerät gehört wird.
- **Technisch bedingt:** deine IP-Adresse, wie bei jeder Verbindung.

## Was an GitHub geht

- **Suche nach Updates:** Lagerfeuer fragt kurz nach dem Start und danach alle 12 Stunden bei GitHub nach,
  ob es eine neuere Fassung gibt (`api.github.com`). GitHub erfährt dabei deine IP-Adresse und die Kennung
  „Lagerfeuer“ mit der Versionsnummer, sonst nichts. Die Suche lässt sich in den Einstellungen abschalten.
- **Update laden:** nur wenn du „Jetzt aktualisieren“ wählst, von `github.com`.
- **Links:** „Quellcode“, „Fehler melden“ und „Was ist neu?“ öffnen eine Seite auf GitHub in deinem Browser.

Für GitHub gilt die [Datenschutzerklärung von GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

## Was auf deinem Rechner gespeichert wird

Alles liegt in deinem Windows-Profil unter `%LOCALAPPDATA%\Lagerfeuer`:

| Was | Wozu |
|---|---|
| Einstellungen: Adresse des Servers, Benutzername, die zufällige Kennung, Lautstärke, Tempo, Fenstergröße und Ähnliches | damit Lagerfeuer beim nächsten Start so ist wie zuvor |
| Tokens des Servers, mit Windows-Mitteln für dein Benutzerkonto verschlüsselt (DPAPI). Dein Passwort wird nicht gespeichert | damit du dich nicht jedes Mal anmelden musst |
| Zwischengespeicherte und heruntergeladene Audiodateien, Titelbilder und Angaben zu Büchern | zum Hören, auch ohne Verbindung |
| Hörstände, die noch nicht an den Server gemeldet sind | damit beim Hören ohne Verbindung nichts verloren geht |
| Ein Protokoll (`log.txt`) mit Fehlern und Abläufen. Es enthält die Adresse des Servers und Kennungen von Büchern, aber keine Tokens und keine Passwörter | zur Fehlersuche. Es verlässt deinen Rechner nur, wenn du es weitergibst |

## Was du in der Hand hast

- **Abmelden** löscht die Tokens auf diesem Rechner; „Auf allen Geräten abmelden“ macht sie auch am Server
  ungültig.
- **Cache leeren** in den Einstellungen entfernt zwischengespeicherte Dateien.
- **Update-Suche abschalten** in den Einstellungen.
- **Alles entfernen:** Bei der Deinstallation „Benutzerdaten entfernen“ wählen oder den Ordner
  `%LOCALAPPDATA%\Lagerfeuer` löschen.
- Was der Server über dich gespeichert hat, verwaltest du dort.

## Fragen

Über die [Issues des Projekts](https://github.com/Celestial0579/lagerfeuer/issues). Sicherheitslücken bitte
vertraulich, siehe [SECURITY.md](SECURITY.md).
