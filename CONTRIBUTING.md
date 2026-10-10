# Mitmachen

Beiträge sind willkommen: Fehlerberichte, Ideen und Pull Requests.

## Entwickeln

Voraussetzungen: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download), für den Installer [NSIS](https://nsis.sourceforge.io).

```bash
dotnet run --project src/Lagerfeuer/Lagerfeuer.csproj -p:Platform=x64
```

```bash
dotnet test --project tests/Lagerfeuer.Tests/Lagerfeuer.Tests.csproj
```

- **`src/Lagerfeuer.Core`** enthält alles ohne Oberfläche: API-Client, Streaming-Cache, Offline, Live-Sync, Updater. Neue Logik gehört nach Möglichkeit hierher und bekommt Tests in `tests/Lagerfeuer.Tests`.
- **`src/Lagerfeuer`** ist die WinUI-3-App (Seiten, Player, Taskleiste).
- Schnittstellen bitte gegen den aktuellen [Audiobookshelf-Quellcode](https://github.com/advplyr/audiobookshelf) prüfen, nicht nur gegen die API-Doku, die teils veraltet ist.
- Oberfläche und Texte sind auf Deutsch, in Du-Form.

## Pull Requests

- Ein Thema pro Pull Request.
- Die CI (Tests und Build für x64/ARM64) muss grün sein.
- Mit dem Einreichen stimmst du zu, dass dein Beitrag unter der [GPL-3.0](LICENSE) steht.

## Releases

Releases erstellt der Maintainer per Versions-Tag, siehe [README → Release veröffentlichen](README.md#release-veröffentlichen).
