# Signatur der Dateien: Stand und Wege

Stand: 13.11.2026.

## Stand

- Setup und portable EXE sind nicht signiert. Windows SmartScreen warnt beim ersten Start.
- Der Updater prüft eine heruntergeladene Datei gegen die Prüfsumme aus dem Release und daran, ob sie ein
  Windows-Programm für diesen Rechner ist. Eine Signatur prüft er nicht. Was das bedeutet, steht in der
  [SECURITY.md](../SECURITY.md).
- Der Release-Ablauf kann nur mit einem Zertifikat als Datei signieren. Solche Dateien geben öffentliche
  Aussteller seit dem 1.6.2023 nicht mehr aus: Der Schlüssel muss in Hardware oder bei einem Signaturdienst
  liegen.

Entschieden ist am 13.11.2026: Vorerst bleibt es bei unsignierten Dateien auf GitHub. Die beiden Wege
unten sind nur festgehalten; vorbereitet ist keiner.

## Weg 1: Microsoft Store

Ein Paket aus dem Store signiert Microsoft selbst. Das kostet nichts und braucht kein eigenes Zertifikat, gilt
aber nur für die Fassung aus dem Store. Was dafür nötig ist, steht in [microsoft-store.md](microsoft-store.md).

## Weg 2: Certum Open Source Code Signing

Damit ließen sich Setup und portable EXE auf GitHub signieren. Geprüft an den Seiten von Certum
([Angebot](https://shop.certum.eu/open-source-code-signing-on-simplysign.html),
[verlangte Unterlagen](https://support.certum.eu/en/code-signing-required-documents/)).

| | |
|---|---|
| Kosten | 49 € für das Zertifikat in der Cloud (SimplySign). Die Laufzeit nennt die Angebotsseite nicht |
| Wer es bekommt | nur natürliche Personen, für freie Software. Wird damit kommerziell vertriebene Software signiert, widerruft Certum das Zertifikat |
| Was du brauchst | eine Identitätsprüfung (empfohlen automatisch, sonst Fotos beider Seiten eines Ausweises), eine Versorgerrechnung als Nachweis der Anschrift, und die Adresse eines laufenden, öffentlichen Projekts, das deinen Bezug zum Projekt klar erkennen lässt. Erkennt Certum das Projekt nicht anhand öffentlicher Angaben, gibt es kein Zertifikat |
| Was das Zertifikat öffentlich zeigt | deinen Namen, mit dem Zusatz „Open Source Developer“. Als Organisation steht „Open Source Developer“. Solche Zertifikate nennen üblicherweise auch Ort und Land; die Seiten von Certum führen die Felder nicht einzeln auf. Jeder, der die Datei ansieht, kann das lesen |
| Wie signiert wird | an deinem Windows-Rechner: SimplySign Desktop bindet eine virtuelle Karte ein, freigeschaltet mit einem Code aus der SimplySign-App auf dem Handy. In GitHub Actions geht das nicht |
| Grenzen | 5000 Signaturen im Monat. SmartScreen warnt auch bei signierten Dateien zunächst weiter; der Ruf des Herausgebers baut sich erst auf |

Für den Release-Ablauf hieße das: Die CI baut wie bisher, aber nur einen Entwurf des Release. Signiert,
geprüft und veröffentlicht würde danach von Hand an deinem Rechner. Die Prüfsummen, die der Updater
vergleicht, entstehen erst mit den signierten Dateien.

## Was am Projekt für Certum erkennbar sein muss

Certum will den Bezug zwischen dir als Antragsteller und dem Projekt an öffentlichen Angaben erkennen. Was
heute am Projekt steht und ob das genügt, steht nicht in diesem Dokument, sondern ist vor einem Antrag zu
klären. Ob dein Name in Lizenz oder README erscheint, entscheidest du.
