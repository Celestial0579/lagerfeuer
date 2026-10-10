<p align="center">
  <img src="src/Lagerfeuer/Assets/AppIcon.png" width="96" alt="Lagerfeuer" />
</p>

<h1 align="center">Lagerfeuer</h1>

<p align="center">Hörbücher aus deinem <a href="https://www.audiobookshelf.org/">Audiobookshelf</a> – als native Windows-App.</p>

Lagerfeuer ist ein Windows-Client für Audiobookshelf, gebaut mit WinUI 3 (Windows App SDK 2.5) und .NET 10.
Die API-Anbindung wurde gegen den Quellcode von Audiobookshelf **v2.37.1** geprüft. Der Server muss mindestens Version **2.26** haben (JWT-Tokens).

## Installation

Unter [Releases](https://github.com/Celestial0579/lagerfeuer/releases) gibt es pro Version zwei Varianten für x64 und ARM64:

| Datei | Beschreibung |
|---|---|
| `Lagerfeuer-<ver>-win-x64-setup.exe` | **Installer** (empfohlen). Installiert pro Benutzer nach `%LOCALAPPDATA%\Programs\Lagerfeuer`, ohne Adminrechte. Legt Startmenü- und optional Desktop-Verknüpfung an und erscheint unter „Apps & Features“. |
| `Lagerfeuer-<ver>-win-x64.exe` | **Portable EXE**. Eine einzelne Datei, die .NET und Windows App SDK enthält. Beim ersten Start entpackt sie sich einmalig nach `%TEMP%\.net`. |

Lagerfeuer sucht selbst nach neuen Versionen (siehe [Updates](#updates)).

> Die Dateien sind nicht signiert. Windows SmartScreen warnt deshalb beim ersten Start („Weitere Informationen“ → „Trotzdem ausführen“).

## Funktionen

- **Anmeldung**
  - mit Benutzername und Passwort, oder
  - per **SSO** (OpenID Connect, z.B. Authentik) im **Standardbrowser**. Eine bestehende Sitzung beim Identity Provider wird mitgenutzt.
  - JWT-Access-Token plus rotierendes Refresh-Token, die bei Ablauf automatisch erneuert werden. Die Tokens liegen DPAPI-verschlüsselt im Benutzerprofil.
- **Bibliothek**
  - „Weiterhören“ über alle Bibliotheken hinweg, Buchbibliotheken mit Nachladen beim Scrollen, Suche.
  - Detailseite mit Beschreibung, Kapiteln und Fortschritt, „Als beendet markieren“.
- **Streaming-Wiedergabe**
  - Bücher mit mehreren Dateien, Kapitelnavigation, Geschwindigkeit von 0,75× bis 3×, Sleep-Timer (Minuten oder Kapitelende), Lautstärke.
  - Tastatur: Leertaste, ← (10 s zurück), → (30 s vor).
  - Windows-Mediensteuerung und Medientasten mit Cover. Weiter/Zurück springt kapitelweise.
- **Caching beim Streaming**
  - Audiodaten werden beim Abspielen blockweise per HTTP-Range auf die Platte geschrieben.
  - Springen lädt gezielt ab der neuen Stelle nach, der Rest wird im Hintergrund vorgeladen, ebenso der nächste Track.
  - Vollständig geladene Dateien werden direkt von der Platte gespielt.
  - Das Cache-Limit ist einstellbar; bei Überschreitung werden die am längsten nicht gehörten Bücher entfernt.
- **Fortschritts-Sync**
  - Playback-Sessions mit Sync alle 15 s und bei Pause, Abschluss beim Beenden.
  - Gehörte Zeit geht bei Netzfehlern nicht verloren.
  - Schließt der Server die Session, wird automatisch eine neue gestartet.
- **Updates** direkt aus den GitHub-Releases dieses Repositorys.

Das Farbschema (Glut, Funke, Stahl) ist an [firestrike.de](https://firestrike.de) angelehnt und funktioniert im hellen wie im dunklen Windows-Design.

## SSO mit Authentik einrichten

1. **Authentik** → Provider (OAuth2/OpenID, *confidential*), Redirect-URIs:
   - `https://<abs-host>/auth/openid/callback` (Web-Oberfläche)
   - `https://<abs-host>/auth/openid/mobile-redirect` (**nötig für Lagerfeuer** und die Mobile-Apps)
2. **Audiobookshelf** → Einstellungen → Authentifizierung → OpenID Connect aktivieren, Issuer-URL, Client-ID und Secret eintragen.
3. Unter **Allowed Mobile Redirect URIs** muss `audiobookshelf://oauth` stehen. Das ist der Standard.
4. Hinter einem Reverse-Proxy müssen die `X-Forwarded-*`-Header korrekt gesetzt sein, weil Audiobookshelf daraus die Redirect-URL bildet.

So läuft die Anmeldung ab:
1. Lagerfeuer startet `/auth/openid` selbst, mit eigenem Cookie-Jar und PKCE.
2. Die Login-Seite des Identity Providers öffnet sich im Standardbrowser. Bist du dort schon angemeldet, geht es ohne Eingabe weiter.
3. Audiobookshelf leitet auf `audiobookshelf://oauth?code=…&state=…` weiter. Der Browser fragt einmalig, ob Lagerfeuer geöffnet werden darf.
4. Windows startet Lagerfeuer. Diese Instanz reicht die URL an das laufende Fenster weiter und beendet sich sofort.
5. Lagerfeuer tauscht den Code mit `code_verifier` und demselben Session-Cookie über `/auth/openid/callback` gegen Tokens.

Den Handler für `audiobookshelf://` registriert Lagerfeuer beim ersten SSO-Login selbst (pro Benutzer unter `HKCU\Software\Classes`, ohne Adminrechte).

## Updates

Lagerfeuer fragt kurz nach dem Start und danach alle 12 Stunden die Releases-API von GitHub (`releases/latest`) nach einer neueren Version. Entwürfe und Vorabversionen werden ignoriert. Gibt es ein Update, erscheint oben ein Hinweis mit drei Möglichkeiten:
- **Jetzt aktualisieren:**
  1. Lagerfeuer lädt die passende Datei herunter und prüft sie gegen die SHA-256-Prüfsumme, die GitHub zum Asset liefert.
  2. Die installierte Version führt das Setup still aus (`/S /RESTART`); die portable EXE tauscht sich selbst aus.
  3. Danach startet Lagerfeuer neu.
- **Was ist neu?** öffnet die Release-Seite.
- **Diese Version überspringen.**

Die automatische Prüfung lässt sich in den Einstellungen abschalten; dort gibt es auch „Jetzt nach Updates suchen“.

## Bauen

Voraussetzungen: Windows 10 1809+ / Windows 11, [.NET 10 SDK](https://dotnet.microsoft.com/download), für den Installer zusätzlich [NSIS](https://nsis.sourceforge.io) 3.x (`winget install NSIS.NSIS`).

```bash
dotnet run --project src/Lagerfeuer/Lagerfeuer.csproj -p:Platform=x64
```

Portable EXE und Installer nach `artifacts/` bauen:

```bash
pwsh build/publish.ps1 -Platform x64,ARM64 -Installer
```

Daten (Einstellungen, Tokens, Cache, Log) liegen unter `%LOCALAPPDATA%\Lagerfeuer`.

### Release veröffentlichen

1. Version in `src/Lagerfeuer/Lagerfeuer.csproj` erhöhen.
2. Committen und ein Tag pushen, z.B.:
   ```bash
   git tag v0.2.0
   ```
   ```bash
   git push origin v0.2.0
   ```
3. Der Workflow [`release.yml`](.github/workflows/release.yml) baut beide Architekturen und legt das GitHub-Release mit allen Dateien an. Die installierten Apps finden es danach von selbst.

## Projektstruktur

```
src/Lagerfeuer/
  Services/   AbsApiClient (REST + Token-Refresh), OpenIdLogin (SSO), CachingHttpStream (Streaming-Cache),
              CacheManager/CoverCache, PlayerService (Wiedergabe, Sync, SMTC), UpdateService (GitHub-Releases),
              SingleInstance/ProtocolRegistration, AppSettings/CredentialStore/Log
  Views/      Login, Weiterhören, Bibliothek, Buch, Einstellungen
  Controls/   PlayerBar, BookTile, CoverImage
  Models/     DTOs der Audiobookshelf-API
installer/    NSIS-Skript
build/        publish.ps1 (portable EXE + Installer)
```

## Roadmap

### v2 – Offline-Wiedergabe
Der Cache ist dafür vorbereitet: ein Ordner pro Buch, darin die Audiodateien mit `.map` sowie `session.json` mit Tracks und Kapiteln.
- Bücher gezielt herunterladen („anheften“); angeheftete Bücher nimmt die Cache-Bereinigung aus.
- Offline-Bibliothek aus den lokalen `session.json`-Dateien.
- Lokale Sessions offline aufzeichnen und beim nächsten Online-Gang mit `POST /api/session/local-all` hochladen.
- Start ohne Server-Verbindung mit gespeicherten Tokens.

### Später
- **Taskleisten-Miniplayer**: Thumbnail-Toolbar-Buttons, Fortschritt im Taskleisten-Symbol, optional ein kompaktes „Immer im Vordergrund“-Fenster.
- **Automatisches Pausieren bei Anrufen** über WASAPI-Ducking-Benachrichtigungen (`IAudioVolumeDuckNotification`).
- Live-Sync per Socket.io (`user_item_progress_updated`).
- Lesezeichen, Serien- und Autorenansichten, Filter und Sortierung, Podcasts.
- Abmelden auf allen Geräten, Sitzungsverwaltung.
- Code-Signing, winget-Paket.

## Lizenz

[GPL-3.0](LICENSE) – wie Audiobookshelf selbst. Lagerfeuer ist ein unabhängiges Projekt und steht in keiner Verbindung zum Audiobookshelf-Projekt.
