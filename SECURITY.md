# Sicherheit

## Lücke melden

Bitte melde Sicherheitslücken **nicht** als öffentliches Issue, sondern vertraulich über
[GitHub → Security → Report a vulnerability](https://github.com/Celestial0579/lagerfeuer/security/advisories/new).

Hilfreich sind eine Beschreibung, betroffene Versionen und – falls möglich – Schritte zum Nachvollziehen.

## Unterstützte Versionen

Sicherheitskorrekturen gibt es für die jeweils neueste Version. Lagerfeuer aktualisiert sich über den eingebauten Updater selbst.

## Wie Lagerfeuer mit Zugangsdaten umgeht

- Passwörter werden nicht gespeichert. Gespeichert werden nur die Tokens des Servers, DPAPI-verschlüsselt im Windows-Profil (`%LOCALAPPDATA%\Lagerfeuer\credentials.bin`).
- Die SSO-Anmeldung läuft im Standardbrowser mit PKCE. Den Code tauscht Lagerfeuer selbst beim Audiobookshelf-Server ein.
- Updates werden vor dem Ausführen gegen die SHA-256-Prüfsumme geprüft, die GitHub zum Release-Asset liefert. Fehlt die Prüfsumme, wird nichts installiert.
- Eine heruntergeladene Update-Datei muss außerdem nach ihrem Kopf ein Windows-Programm für die Architektur des Rechners sein. Sonst wird sie verworfen, bevor etwas getauscht oder gestartet wird.
- Das Log (`%LOCALAPPDATA%\Lagerfeuer\log.txt`) enthält keine Tokens, aber Server-Adressen und Buch-IDs. Bitte vor dem Teilen prüfen.

## Programme, die Lagerfeuer startet

- Setup und App starten Programme von Windows nur mit vollem Pfad: das Setup `cmd`, `tasklist` und `find` aus dem Systemordner, die App den Explorer aus dem Windows-Ordner. Ein gleichnamiges Programm im Ordner des Setups oder der portablen EXE läuft dadurch nicht an ihrer Stelle.
- Der Arbeitsordner des Setups ist von Beginn an der Systemordner, nicht der Ordner, aus dem es gestartet wurde.
