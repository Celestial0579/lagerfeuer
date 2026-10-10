<p align="center">
  <img src="src/Lagerfeuer/Assets/AppIcon.png" width="96" alt="Lagerfeuer" />
</p>

<h1 align="center">Lagerfeuer</h1>

<p align="center">Hörbücher aus deinem <a href="https://www.audiobookshelf.org/">Audiobookshelf</a> – als native Windows-App.</p>

<p align="center">
  <a href="https://github.com/Celestial0579/lagerfeuer/actions/workflows/ci.yml"><img src="https://github.com/Celestial0579/lagerfeuer/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://github.com/Celestial0579/lagerfeuer/releases/latest"><img src="https://img.shields.io/github/v/release/Celestial0579/lagerfeuer?color=AB5019" alt="Release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/Lizenz-GPL--3.0-33414B" alt="GPL-3.0" /></a>
</p>

Lagerfeuer ist ein Windows-Client für Audiobookshelf, gebaut mit WinUI 3 (Windows App SDK 2.5) und .NET 10.
Die API-Anbindung wurde gegen den Quellcode von Audiobookshelf **v2.37.1** geprüft. Der Server muss mindestens Version **2.26** haben (JWT-Tokens).

## Installation

Unter [Releases](https://github.com/Celestial0579/lagerfeuer/releases) gibt es pro Version zwei Varianten für x64 und ARM64:

| Datei | Beschreibung |
|---|---|
| `Lagerfeuer-<ver>-win-x64-setup.exe` | **Installer** (empfohlen). Installiert pro Benutzer nach `%LOCALAPPDATA%\Programs\Lagerfeuer`, ohne Adminrechte. Legt Startmenü- und optional Desktop-Verknüpfung an und erscheint unter „Apps & Features“. |
| `Lagerfeuer-<ver>-win-x64.exe` | **Portable EXE**. Eine einzelne Datei, die .NET und Windows App SDK enthält. Beim ersten Start entpackt sie sich einmalig nach `%TEMP%\.net`. |

Lagerfeuer sucht selbst nach neuen Versionen (siehe [Updates](#updates)).

> Solange kein Code-Signing-Zertifikat eingebunden ist, sind die Dateien nicht signiert. Windows SmartScreen warnt dann beim ersten Start („Weitere Informationen“ → „Trotzdem ausführen“).

## Funktionen

- **Anmeldung**
  - mit Benutzername und Passwort, oder
  - per **SSO** (OpenID Connect, z.B. Authentik) im **Standardbrowser**. Eine bestehende Sitzung beim Identity Provider wird mitgenutzt.
  - JWT-Access-Token plus rotierendes Refresh-Token, die bei Ablauf automatisch erneuert werden. Die Tokens liegen DPAPI-verschlüsselt im Benutzerprofil.
  - Angemeldete Geräte einsehen und einzeln abmelden, „Auf allen Geräten abmelden“.
- **Bibliothek**
  - „Weiterhören“ über alle Bibliotheken hinweg, inklusive angefangener Podcast-Folgen.
  - Bücher, **Serien** und **Autoren** mit eigenen Seiten. Serien erscheinen in Lesereihenfolge, Autoren mit Bild, Beschreibung und Serien.
  - **Sortierung** nach Titel, Autor, hinzugefügt, Erscheinungsjahr, Dauer oder zuletzt gehört.
  - **Filter** nach Fortschritt: nicht begonnen, angefangen, nicht beendet, beendet.
  - Suche, Nachladen beim Scrollen.
  - Buchseite mit Beschreibung, Kapiteln, Fortschritt, **Lesezeichen**, Links zu Autor und Serie, „Als beendet markieren“.
- **Podcasts**
  - Podcast-Bibliotheken, Podcast-Seite mit allen Folgen (neueste zuerst, gehörte ausblendbar).
  - Wiedergabe und Fortschritt pro Folge.
- **Wiedergabe**
  - Streaming von Büchern mit mehreren Dateien, Kapitelnavigation, Geschwindigkeit von 0,75× bis 3×, Sleep-Timer (Minuten oder Kapitelende), Lautstärke.
  - **Lesezeichen** mit einem Klick an der aktuellen Stelle.
  - Tastatur: Leertaste, ← (10 s zurück), → (30 s vor).
  - Windows-Mediensteuerung und Medientasten mit Cover. Weiter/Zurück springt kapitelweise.
  - **Taskleisten-Miniplayer:** fünf Steuerknöpfe im Vorschaubild, Hörfortschritt im Taskleisten-Symbol.
  - **Mini-Player-Fenster**, immer im Vordergrund.
  - **Automatisches Pausieren bei Anrufen** (Teams, Zoom, Telefon-Link …), danach optional automatisch fortsetzen.
- **Caching beim Streaming**
  - Audiodaten werden beim Abspielen blockweise per HTTP-Range auf die Platte geschrieben.
  - Springen lädt gezielt ab der neuen Stelle nach, der Rest wird im Hintergrund vorgeladen, ebenso der nächste Track.
  - Das Cache-Limit ist einstellbar; die am längsten nicht gehörten Bücher werden zuerst entfernt.
- **Offline**
  - Bücher **herunterladen**. Sie sind dann angeheftet, und die Cache-Bereinigung lässt sie in Ruhe.
  - Seite **Downloads**.
  - Die Admin-Berechtigung „Download“ wird respektiert.
  - **Wiedergabe ohne Server:** Offline gehörte Zeit wird gespeichert und beim nächsten Online-Gang hochgeladen (`/api/session/local-all`).
  - **Start ohne Verbindung:** Downloads bleiben nutzbar, die Verbindung wird automatisch wiederhergestellt.
- **Fortschritts-Sync**
  - Playback-Sessions mit Sync alle 15 s und bei Pause, Abschluss beim Beenden. Gehörte Zeit geht bei Netzfehlern nicht verloren.
  - **Live-Sync** per Socket.IO: Fortschritt von anderen Geräten erscheint sofort. Ist dasselbe Buch hier pausiert, springt die Wiedergabe an die neue Stelle.
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

### Tests

Die UI-unabhängige Logik liegt in `src/Lagerfeuer.Core` und ist mit xUnit v3 getestet (`tests/Lagerfeuer.Tests`):
- **API-Client:** Login, Token-Refresh inkl. Rotation und parallelen Anfragen, abgelaufene Sitzung, Unterpfade.
- **Streaming-Cache:** Range-Requests, Springen, Fortsetzen teilweise geladener Dateien, Lesen ohne Server, Server ohne Range-Support.
- **Updater:** Versionsvergleich, Auswahl der passenden Datei, Entwürfe und Vorabversionen, Prüfsummen.
- **Cache-Verwaltung, Datenmodelle und Formatierung.**

```bash
dotnet test --project tests/Lagerfeuer.Tests/Lagerfeuer.Tests.csproj
```

Auf GitHub laufen die Tests bei jedem Push und Pull Request ([`ci.yml`](.github/workflows/ci.yml)) sowie vor jedem Release.

### Release veröffentlichen

1. Version in `src/Lagerfeuer/Lagerfeuer.csproj` erhöhen.
2. Committen und ein Tag pushen, z.B.:
   ```bash
   git tag v0.2.0
   ```
   ```bash
   git push origin v0.2.0
   ```
3. Der Workflow [`release.yml`](.github/workflows/release.yml) führt die Tests aus, baut beide Architekturen und legt das GitHub-Release mit allen Dateien an. Die installierten Apps finden es danach von selbst.

## Projektstruktur

```
src/Lagerfeuer.Core/    UI-unabhängige Logik (getestet)
  Services/   AbsApiClient (REST + Token-Refresh), OpenIdLogin (SSO), CachingHttpStream (Streaming-Cache),
              CacheManager/CoverCache, DownloadManager, LocalSessionStore, ProgressStore (Offline),
              LiveSyncClient (Socket.IO), CallDetector (Anrufe), UpdateService (GitHub-Releases),
              LibraryQuery, SingleInstance/ProtocolRegistration, AppSettings/CredentialStore/Log, Format
  Models/     DTOs der Audiobookshelf-API
src/Lagerfeuer/         WinUI-3-App
  Services/   PlayerService (Wiedergabe, Sync, SMTC, Offline-Sessions), TaskbarIntegration
  Views/      Login, Weiterhören, Bibliothek, Serie, Autor, Podcast, Buch, Downloads, Einstellungen, Mini-Player
  Controls/   PlayerBar, BookTile, CoverImage
tests/Lagerfeuer.Tests/ xUnit-v3-Tests
installer/              NSIS-Skript und Grafiken
build/                  publish.ps1 (EXE, Installer, Signieren), make-art.ps1 (Grafiken), New-WingetManifest.ps1
```

## Roadmap

Erledigt: Offline-Wiedergabe (v2), Taskleisten-Miniplayer, Auto-Pause bei Anrufen, Live-Sync, Lesezeichen, Serien- und Autorenansichten, Filter und Sortierung, Podcasts, Geräteverwaltung.

Vorbereitet, braucht noch eine Entscheidung bzw. ein Zertifikat:
- **Code-Signing:** `build/publish.ps1` signiert App, portable EXE, Setup und Deinstaller, sobald ein Zertifikat angegeben ist. Das geht per `-CertificateThumbprint` oder `-CertificatePath`. In GitHub Actions hinterlegst du dafür die Secrets `SIGNING_CERT_PFX_BASE64` und `SIGNING_CERT_PASSWORD`.
- **winget:**
  - `build/New-WingetManifest.ps1 -Version x.y.z` erzeugt geprüfte Manifeste (`Celestial0579.Lagerfeuer`). Der Release-Workflow legt sie bei jedem Release als Artefakt ab.
  - Fehlt noch: die Einreichung als Pull Request bei [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).

Ideen für später:
- Podcast-Folgen herunterladen (offline zunächst nur Bücher).
- Lesezeichen umbenennen, Sammlungen und Playlists.
- Statistiken (Hörzeit pro Tag/Woche).
## Lizenz

Lagerfeuer – Copyright (C) 2026 Celestial0579

Freie Software unter der [GPL-3.0](LICENSE), mit einer [zusätzlichen Erlaubnis nach § 7](LICENSE-ADDITIONAL-PERMISSION.md) für die Kopplung an .NET und das Windows App SDK. Lagerfeuer wird ohne jede Gewährleistung bereitgestellt.

Mitgelieferte Komponenten von Drittanbietern (.NET, Windows App SDK, WebView2, CommunityToolkit) stehen unter ihren eigenen Lizenzen, siehe [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) und den Ordner [`licenses/`](licenses). In der App findest du alle Texte unter Einstellungen → Über Lagerfeuer.

Lagerfeuer ist ein unabhängiges Projekt und steht in keiner Verbindung zum Audiobookshelf-Projekt.