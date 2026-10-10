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
- Updates werden vor dem Ausführen gegen die SHA-256-Prüfsumme geprüft, die GitHub zum Release-Asset liefert.
- Das Log (`%LOCALAPPDATA%\Lagerfeuer\log.txt`) enthält keine Tokens, aber Server-Adressen und Buch-IDs. Bitte vor dem Teilen prüfen.
