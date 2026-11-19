# Lagerfeuer im Microsoft Store: Stand und Plan

Stand: 13.11.2026. **Der Weg über den Store ist zurückgestellt:** Vorerst bleibt es beim unsignierten
Release auf GitHub. Eingereicht ist nichts, vorbereitet auch nichts, und es gibt kein Entwicklerkonto. Dieses
Dokument hält fest, was die Prüfung ergeben hat: was eine Einreichung verlangt, was sich dafür an Lagerfeuer
ändern müsste und was bei dir als Betreiber des Projekts bliebe.

Geprüft an Microsofts Dokumentation ([Microsoft Learn](https://learn.microsoft.com/windows/apps/publish/)),
den [Microsoft Store Policies](https://learn.microsoft.com/windows/apps/publish/store-policies) in der
Fassung 7.20 (gültig ab 22.10.2026) und dem App Developer Agreement 8.11 (gültig ab 17.04.2026).

## Warum der Store

Die Dateien von Lagerfeuer sind nicht signiert; Windows SmartScreen warnt deshalb beim ersten Start. Ein
Paket aus dem Store signiert Microsoft selbst, kostenlos und ohne eigenes Zertifikat. Installation und
Updates laufen dann über den Store.

Der Store nimmt zwei Arten von Einreichungen an. Für Lagerfeuer kommt nur die erste in Frage:

| Art | Signatur | Updates | Für Lagerfeuer |
|---|---|---|---|
| MSIX-Paket | Microsoft signiert nach der Prüfung | über den Store | ja |
| Setup als EXE oder MSI, bei dir gehostet | du musst selbst signieren, mit einem Zertifikat eines öffentlichen Ausstellers | bleiben deine Sache | nein, solange es kein Zertifikat gibt |

Setup und portable EXE auf GitHub bleiben daneben bestehen. Sie bleiben unsigniert, bis es dafür einen
eigenen Weg gibt (siehe [signatur.md](signatur.md)).

## Was eine Einreichung verlangt

| Punkt | Was Microsoft verlangt | Stand bei Lagerfeuer |
|---|---|---|
| Entwicklerkonto | Einzelkonto, kostenlos, über [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com); persönliches Microsoft-Konto, amtlicher Ausweis und ein Selfie. Ein Einzelkonto lässt sich später nicht in ein Firmenkonto umwandeln | fehlt, legst du selbst an |
| Herausgebername | Mit dem Einzelkonto veröffentlichst du laut Microsoft „unter deinem eigenen Namen“. Ob sich ein anderer Anzeigename wählen lässt, sagt die Dokumentation nicht; das zeigt erst die Anmeldung | deine Entscheidung |
| Name der App | muss im Store einmalig sein und wird im Partner Center reserviert | ob „Lagerfeuer“ frei ist, zeigt erst die Reservierung |
| Paket-Identität | `Identity/Name`, `Identity/Publisher` und `PublisherDisplayName` im Manifest müssen genau den Werten aus dem Partner Center entsprechen | erst nach Konto und Reservierung bekannt |
| Version | vier Teile; der erste darf nicht 0 sein, der vierte muss 0 bleiben | **0.2.x geht nicht.** Entweder erscheint die erste Store-Fassung als 1.0.0, oder das Paket zählt anders als die App |
| Datenschutzerklärung | Pflicht für jede Desktop-App, als Adresse im Partner Center (Richtlinie 10.5.1) | Entwurf: [DATENSCHUTZ.md](../DATENSCHUTZ.md), von dir zu prüfen |
| Abhängigkeit nennen | Braucht die App fremde Software oder einen Dienst, muss das am Anfang der Beschreibung stehen (10.2.4) | Entwurf der Beschreibung unten |
| Prüfbarkeit | Braucht die App eine Anmeldung, will Microsoft ein funktionierendes Demokonto; braucht sie einen Server, muss er erreichbar sein (10.3.1, 10.3.2) | **fehlt:** ein öffentlich erreichbarer Audiobookshelf-Server mit Testinhalten und Demokonto |
| Store-Eintrag | Beschreibung, mindestens ein Screenshot als PNG mit 1366 × 768 Pixeln oder mehr (empfohlen vier und mehr), Logo, Kategorie, Fragebogen zur Altersfreigabe | fehlt |
| Sprache | Der Eintrag muss in jeder Sprache vorliegen, die die App anbietet (10.7) | Lagerfeuer ist deutsch, also ein deutscher Eintrag |
| Lizenz | Eigene Lizenzbedingungen sind erlaubt und gehen den Standardbedingungen des Store vor. Bei freier Software bist du für die Einhaltung der Lizenz verantwortlich, auch für den Zugang zum Quelltext (Agreement 4 c) | GPL-3.0 mit Zusatzerlaubnis; im Eintrag auf Lizenz und Quelltext verweisen |
| Rechte des Pakets | Eine Desktop-App erklärt die eingeschränkte Fähigkeit `runFullTrust` und begründet sie bei der Einreichung | ein Satz genügt: klassische Desktop-App |
| Deinstallation | muss sauber möglich sein (10.2.7) | erledigt Windows bei einem Paket selbst |
| Gebühren | keine für kostenlose Apps | Lagerfeuer bleibt kostenlos |

## Was sich an Lagerfeuer ändern muss

Im Paket läuft Lagerfeuer als gewöhnliche Desktop-App mit vollen Rechten, aber mit einer eigenen Sicht auf
Registry und Benutzerdaten. Daraus folgt:

| Stelle | Heute | Im Store-Paket |
|---|---|---|
| Updater | fragt GitHub nach neuen Fassungen, lädt Setup oder portable EXE und startet sie | abgeschaltet. Ein Paket lässt sich nicht von innen ersetzen, und das Setup würde eine zweite Installation daneben anlegen. Der Store aktualisiert; die Einstellungen sagen das statt der Update-Schalter |
| `audiobookshelf://` für die SSO-Anmeldung | trägt sich selbst unter `HKCU\Software\Classes` ein | Einträge unter `HKCU` landen im Paket in einem privaten Bereich und wirken nach außen nicht. Das Protokoll gehört ins Manifest (`uap:Protocol`); die Adresse kommt dann nicht mehr über die Befehlszeile, sondern über `AppInstance.GetActivatedEventArgs` |
| Daten unter `%LOCALAPPDATA%\Lagerfeuer` | liegen dort | Windows leitet sie in den Ordner des Pakets um. Für die App ändert sich nichts; „Ordner öffnen“ muss aber den tatsächlichen Ort zeigen, sonst öffnet der Explorer einen leeren Pfad |
| Anzeige „installiert“ oder „portabel“ | aus der Art der EXE | dritte Art: „Microsoft Store“ |
| Laufzeit | .NET und Windows App SDK liegen bei | kann so bleiben (größeres Paket) oder das Windows App SDK kommt als Abhängigkeit über den Store |
| x64 und ARM64 | zwei Setups, zwei portable EXE | ein Bündel mit beiden Paketen |
| Symbole | `AppIcon.ico`, `AppIcon.png` | zusätzlich die Logos des Pakets als PNG in den verlangten Größen |

Unverändert bleiben können: die Anmeldung, der Streaming-Cache, die Weitergabe an die laufende Instanz
(eigener Mutex und eigene Pipe), die Taskleisten-Knöpfe.

## Bau

Das Projekt bekommt ein `Package.appxmanifest` und baut das Paket nur auf Wunsch; Setup und portable EXE
entstehen weiter wie bisher. Für den Store wird nicht signiert:

```powershell
msbuild src\Lagerfeuer\Lagerfeuer.csproj /restore /p:Configuration=Release /p:Platform=x64 `
    /p:WindowsPackageType=MSIX /p:GenerateAppxPackageOnBuild=true `
    /p:AppxBundle=Always /p:AppxBundlePlatforms="x64|ARM64" `
    /p:UapAppxPackageBuildMode=StoreUpload /p:AppxPackageSigningEnabled=false
```

Das ist der Befehl aus Microsofts Anleitung, auf dieses Projekt übertragen; gelaufen ist er hier noch nicht.
Die Werte der Paket-Identität kommen als Parameter dazu, sobald es sie gibt. Geheim sind sie nicht.

## Was bei dir bleibt

1. **Entscheiden:** Soll die erste Store-Fassung 1.0.0 heißen? Unter welchem Herausgebernamen?
2. **Konto anlegen** über [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com). Früh
   anfangen: Manche Prüfungen gehen in die händische Durchsicht.
3. **Namen reservieren** im Partner Center und die drei Werte der Paket-Identität weitergeben.
4. **Testserver bereitstellen:** einen öffentlich erreichbaren Audiobookshelf-Server mit selbst erzeugten
   Testinhalten und einem Demokonto, dessen Zugang in die „Notes for certification“ kommt. Das Projekt
   Audiobookshelf nennt zwar eine öffentliche Demo; sie gehört aber nicht dir und kann jederzeit verschwinden.
5. **Datenschutzerklärung prüfen** und entscheiden, wer darin als Verantwortlicher steht.
6. **Store-Eintrag anlegen:** Beschreibung, Screenshots, Kategorie, Altersfreigabe, Verweis auf Lizenz und
   Quelltext.
7. **Einreichen** und Rückfragen der Prüfung beantworten.

## Was sich vorbereiten lässt, ohne Konto

| Schritt | Aufwand | Braucht |
|---|---|---|
| Manifest, Logos, Bau des Pakets in der CI mit Platzhaltern für die Identität | etwa ein halber Tag | nichts |
| Anpassungen der App (Updater aus, Protokoll, „Ordner öffnen“, Anzeige) mit Tests | etwa ein Tag | nichts |
| Paket in der Windows-Test-VM installieren und durchspielen, Windows App Certification Kit | etwa ein halber Tag | die VM; für die Installation ein selbst erzeugtes Testzertifikat |
| Paket mit den echten Werten bauen, Eintrag füllen, einreichen | wenige Stunden, dazu die Wartezeit der Prüfung | Konto, Reservierung, Testserver |

## Entwurf für die Beschreibung im Store

> **Benötigt einen eigenen Audiobookshelf-Server.** Lagerfeuer ist ein Programm für Windows, mit dem du die
> Hörbücher und Podcasts von deinem Audiobookshelf-Server hörst. Ohne einen solchen Server lässt es sich
> nicht nutzen.
>
> Lagerfeuer merkt sich deinen Hörstand und gleicht ihn mit dem Server ab, lädt Bücher zum Hören ohne
> Verbindung herunter, zeigt Serien, Autoren, Kapitel und Lesezeichen und lässt sich über die Medientasten
> und die Taskleiste steuern. Die Anmeldung geht mit Benutzername und Passwort oder über SSO (OpenID Connect).
>
> Lagerfeuer ist freie Software unter der GPL-3.0. Den Quelltext findest du auf GitHub. Lagerfeuer ist ein
> unabhängiges Projekt und gehört nicht zu Audiobookshelf.

## Quellen

- [Store-Wege im Vergleich und Signatur](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
- [Checkliste vor dem Hochladen](https://learn.microsoft.com/windows/apps/publish/store-readiness-checklist)
- [Entwicklerkonto anlegen](https://learn.microsoft.com/windows/apps/publish/partner-center/open-a-developer-account)
- [Anforderungen an MSIX-Pakete, Versionsnummern](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-package-requirements)
- [Ein Projekt als MSIX verpacken](https://learn.microsoft.com/windows/apps/windows-app-sdk/single-project-msix) und [Bau in der CI](https://learn.microsoft.com/windows/apps/package-and-deploy/ci-for-winui3)
- [Wie verpackte Desktop-Apps laufen](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
- [Aktivierung über ein Protokoll](https://learn.microsoft.com/windows/apps/develop/launch/handle-uri-activation)
- [Screenshots und Bilder](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
