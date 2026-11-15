; NSIS-Installer für Lagerfeuer (Audiobookshelf für Windows)
;
; Installiert pro Benutzer nach %LOCALAPPDATA%\Programs\Lagerfeuer – ohne Adminrechte.
; Dadurch kann der eingebaute Updater Updates ohne UAC-Abfrage einspielen.
;
; Aufruf über build/publish.ps1 -Installer, oder manuell:
;   makensis /INPUTCHARSET UTF8 /DVERSION=0.1.0 /DARCH=x64 /DSRCDIR=<publish-ordner> /DOUTFILE=<setup.exe> /DICON=<ico> Lagerfeuer.nsi
;
; Unbeaufsichtigt:  Lagerfeuer-…-setup.exe /S [/D=%LOCALAPPDATA%\Programs\Lagerfeuer]
;                   Ein Ordner außerhalb des Benutzerprofils bekommt eingeschränkte Rechte (siehe SecureInstDir).
; Vom Updater:      Lagerfeuer-…-setup.exe /S /RESTART   (wartet auf das Beenden der App und startet sie danach neu)
; Deinstallation:   uninstall.exe /S

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
RequestExecutionLevel user

!ifndef VERSION
  !error "VERSION fehlt (/DVERSION=x.y.z)"
!endif
; Rein numerisch für die Versionsinfo der EXE (X.Y.Z); VERSION darf eine Vorabversion sein (0.2.5-beta.1).
!ifndef VERSIONNUM
  !define VERSIONNUM "${VERSION}"
!endif
!ifndef SRCDIR
  !error "SRCDIR fehlt (/DSRCDIR=<dotnet publish Ausgabeordner>)"
!endif
!ifndef OUTFILE
  !define OUTFILE "Lagerfeuer-${VERSION}-setup.exe"
!endif
!ifndef ARCH
  !define ARCH "x64"
!endif
!ifndef ICON
  !define ICON "..\src\Lagerfeuer\Assets\AppIcon.ico"
!endif

; Was das Setup installiert, als Delete-Befehle für die Deinstallation: beim Übersetzen aus SRCDIR erzeugt (#47).
; So entfernt die Deinstallation genau die installierten Dateien und nichts, was sonst im frei wählbaren Ordner
; liegt. Dieselbe Liste kommt als Textdatei mit in den Installationsordner (MANIFESTNAME). Ein späteres Setup liest
; dort, was die bisherige Fassung installiert hat, und entfernt es vor dem Kopieren; so bleibt nichts liegen, wenn
; in einer neuen Fassung eine Datei wegfällt. Befolgt wird nur eine Liste, die mit der Kennzeile MANIFESTHEAD beginnt.
; Muster: WidgetTea. Fassungen bis 0.2.4 hatten keine Liste; was sie mitbrachten und die neue nicht mehr enthält,
; bleibt nach einem Update liegen und mit ihm der Ordner nach der Deinstallation.
!define MANIFESTNAME "Lagerfeuer-installed-files.txt"
!define MANIFESTHEAD "Lagerfeuer installed files 1"
!tempfile PAYLOADLIST
!tempfile MANIFESTFILE
!system 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "${__FILEDIR__}\New-PayloadList.ps1" -Source "${SRCDIR}" -Out "${PAYLOADLIST}" -Manifest "${MANIFESTFILE}" -ManifestHeader "${MANIFESTHEAD}"' = 0

!define APPNAME     "Lagerfeuer"
!define DISPLAYNAME "Lagerfeuer – Audiobookshelf für Windows"
!define PUBLISHER   "Celestial0579"
!define EXENAME     "Lagerfeuer.exe"
!define UNINSTKEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
; Eigene Einstellungen unter Software\<Hersteller>\<Produkt> (#48). Bis 0.2.4 lag der Schlüssel direkt unter
; Software\Lagerfeuer; das Setup zieht ihn beim Update um.
!define PUBLISHERKEY "Software\${PUBLISHER}"
!define SETTINGSKEY "${PUBLISHERKEY}\${APPNAME}"
!define OLDSETTINGSKEY "Software\${APPNAME}"
!define PROTOCOLKEY "Software\Classes\lagerfeuer"
!define FALLBACKPROTOCOLKEY "Software\Classes\audiobookshelf"
!define HOMEPAGE    "https://github.com/Celestial0579/lagerfeuer"

Name "${APPNAME}"
Caption "Lagerfeuer ${VERSION} – Installation"
UninstallCaption "Lagerfeuer – Deinstallation"
OutFile "${OUTFILE}"
BrandingText "${APPNAME} ${VERSION} (${ARCH})"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "${SETTINGSKEY}" "InstallDir"

VIProductVersion "${VERSIONNUM}.0"
VIAddVersionKey /LANG=1031 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1031 "FileDescription" "${DISPLAYNAME} – Setup"
VIAddVersionKey /LANG=1031 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1031 "FileVersion" "${VERSIONNUM}"
VIAddVersionKey /LANG=1031 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1031 "LegalCopyright" "© 2026 Celestial0579 · GPL-3.0"

; Optional signieren (von build/publish.ps1 gesetzt): Setup und eingebetteten Deinstaller
!ifdef SIGNCMD
  !finalize '${SIGNCMD}'
  !uninstfinalize '${SIGNCMD}'
!endif

!include MUI2.nsh
!include LogicLib.nsh
!include x64.nsh
!include FileFunc.nsh
!include WinVer.nsh
!include WordFunc.nsh

Var Restart

; ---------- Oberfläche ----------
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!define MUI_FINISHPAGE_RUN_TEXT "Lagerfeuer jetzt starten"
!define MUI_FINISHPAGE_LINK "Lagerfeuer auf GitHub"
!define MUI_FINISHPAGE_LINK_LOCATION "${HOMEPAGE}"

; Grafiken im Farbschema von firestrike.de (erzeugt von build/make-art.ps1, doppelte Auflösung für hohe DPI)
!define MUI_WELCOMEFINISHPAGE_BITMAP "art\welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "art\welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "art\header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "art\header.bmp"

; Eigene Texte in Du-Form (wie in der App)
!define MUI_WELCOMEPAGE_TITLE "Willkommen bei Lagerfeuer"
!define MUI_WELCOMEPAGE_TEXT "Lagerfeuer bringt die Hörbücher aus deinem Audiobookshelf als native App auf den Windows-Desktop.$\r$\n$\r$\nDie Installation erfolgt nur für dein Benutzerkonto und braucht keine Administratorrechte. Updates holt sich Lagerfeuer später selbst.$\r$\n$\r$\nKlicke auf „Weiter“, um fortzufahren."
!define MUI_COMPONENTSPAGE_TEXT_TOP "Wähle aus, was installiert werden soll."
!define MUI_DIRECTORYPAGE_TEXT_TOP "Lagerfeuer wird in folgendes Verzeichnis installiert. Du kannst auch ein anderes wählen."
!define MUI_FINISHPAGE_TITLE "Lagerfeuer ist bereit"
!define MUI_FINISHPAGE_TEXT "Lagerfeuer wurde installiert. Du findest es im Startmenü.$\r$\n$\r$\nMelde dich beim ersten Start mit deinem Audiobookshelf-Server an."

!insertmacro MUI_PAGE_WELCOME
!define MUI_LICENSEPAGE_TEXT_TOP "Lagerfeuer ist freie Software (GPL-3.0). Bitte lies die Hinweise zu den mitgelieferten Komponenten."
!define MUI_LICENSEPAGE_CHECKBOX
!define MUI_LICENSEPAGE_CHECKBOX_TEXT "Ich stimme den Lizenzbedingungen der mitgelieferten Komponenten zu."
!insertmacro MUI_PAGE_LICENSE "license-de.txt"
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE DirectoryLeave
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_COMPONENTS
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "German"

; ---------- Hilfsfunktionen ----------

; Wartet, bis Lagerfeuer beendet ist (Dateien wären sonst gesperrt).
; Beim Update (/RESTART) beendet sich die App gerade selbst → bis zu 15 s still warten.
;
; Hilfsprogramme von Windows immer mit vollem Pfad aus dem Systemordner. Beim bloßen Namen sucht Windows zuerst
; im Ordner des Setups und im aktuellen Ordner, und dort kann ein fremdes Programm gleichen Namens liegen.
; Zur Schreibweise: cmd /C entfernt das erste und das letzte Anführungszeichen des Befehls, der Rest bleibt.
!macro CLOSE_APP_FUNC un
Function ${un}CloseRunningApp
  StrCpy $R8 0
  retry:
    nsExec::ExecToStack '"$SYSDIR\cmd.exe" /C ""$SYSDIR\tasklist.exe" /FI "IMAGENAME eq ${EXENAME}" /NH | "$SYSDIR\find.exe" /I "${EXENAME}""'
    Pop $0
    Pop $1
    ${If} $0 == 0
      ${If} $Restart == 1
      ${AndIf} $R8 < 30
        IntOp $R8 $R8 + 1
        Sleep 500
        Goto retry
      ${EndIf}
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
        "Lagerfeuer wird gerade ausgeführt.$\r$\n$\r$\nBitte beende Lagerfeuer und klicke auf „Wiederholen“." \
        /SD IDCANCEL IDRETRY retry
      Abort
    ${EndIf}
FunctionEnd
!macroend
!insertmacro CLOSE_APP_FUNC ""
!insertmacro CLOSE_APP_FUNC "un."

Function LaunchApp
  ; Über explorer.exe starten, damit die App nie die Rechte des Setups erbt.
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXENAME}"'
FunctionEnd

; Arbeitsordner auf den Systemordner stellen. Nach einem Doppelklick ist es der Ordner der Setup-Datei, und alles,
; was das Setup startet, erbte ihn samt dem, was dort sonst noch liegt.
!macro WORKDIR_SYSTEM
  SetOutPath "$SYSDIR"
!macroend

Function .onInit
  !insertmacro WORKDIR_SYSTEM

  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Lagerfeuer benötigt Windows 10 (Version 1809) oder neuer." /SD IDOK
    Abort
  ${EndIf}
  !if "${ARCH}" == "ARM64"
    ${IfNot} ${IsNativeARM64}
      MessageBox MB_OK|MB_ICONSTOP "Dieses Setup ist für ARM64-Geräte. Bitte das x64-Setup verwenden." /SD IDOK
      Abort
    ${EndIf}
  !else
    ${IfNot} ${IsNativeAMD64}
    ${AndIfNot} ${IsNativeARM64}
      MessageBox MB_OK|MB_ICONSTOP "Lagerfeuer benötigt ein 64-Bit-Windows." /SD IDOK
      Abort
    ${EndIf}
  !endif

  ; Update von 0.2.4 oder älter: Der neue Schlüssel fehlt noch, der Ordner steht im alten. Ohne diesen Schritt
  ; installierte das Update (der Updater ruft das Setup ohne /D auf) in den Vorgabeordner statt über die alte Fassung.
  ; Hat der Aufruf mit /D einen anderen Ordner gewählt, bleibt es dabei.
  ReadRegStr $R0 HKCU "${SETTINGSKEY}" "InstallDir"
  ${If} $R0 == ""
  ${AndIf} $INSTDIR == "$LOCALAPPDATA\Programs\${APPNAME}"
    ReadRegStr $R0 HKCU "${OLDSETTINGSKEY}" "InstallDir"
    ${If} $R0 != ""
      StrCpy $INSTDIR $R0
    ${EndIf}
  ${EndIf}

  StrCpy $Restart 0
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RESTART" $R1
  ${IfNot} ${Errors}
    StrCpy $Restart 1
  ${EndIf}
FunctionEnd

Function un.onInit
  !insertmacro WORKDIR_SYSTEM
FunctionEnd

Function .onInstSuccess
  ${If} $Restart == 1
    Call LaunchApp
  ${EndIf}
FunctionEnd

; ---------- Dateiliste der bisherigen Fassung (#47) ----------

; Push Pfad / Pop 1, wenn er ein schlichter Pfad unterhalb des Installationsordners ist: nicht leer, ohne
; führenden „\“, ohne „..“ und ohne : / * ?. Sonst 0.
Function IsPlainRelativePath
  Exch $R1
  Push $R2
  Push $R3
  Push $R4
  Push $R5
  StrCpy $R3 1
  StrLen $R5 $R1
  ${If} $R5 == 0
    StrCpy $R3 0
  ${EndIf}
  StrCpy $R2 $R1 1
  ${If} $R2 == "\"
    StrCpy $R3 0
  ${EndIf}
  StrCpy $R4 0
  ${DoWhile} $R4 < $R5
    StrCpy $R2 $R1 1 $R4
    ${If} $R2 == ":"
    ${OrIf} $R2 == "/"
    ${OrIf} $R2 == "*"
    ${OrIf} $R2 == "?"
      StrCpy $R3 0
    ${EndIf}
    StrCpy $R2 $R1 2 $R4
    ${If} $R2 == ".."
      StrCpy $R3 0
    ${EndIf}
    IntOp $R4 $R4 + 1
  ${Loop}
  StrCpy $R1 $R3
  Pop $R5
  Pop $R4
  Pop $R3
  Pop $R2
  Exch $R1
FunctionEnd

; Entfernt, was die bisher installierte Fassung mitgebracht hat, laut ihrer Liste im Installationsordner.
; Was die neue Fassung weiter enthält, schreibt das Setup gleich danach wieder hin; was weggefallen ist, bleibt
; weg. Fremde Dateien stehen in keiner Liste und bleiben liegen. Ein Ordner verschwindet nur, wenn er leer ist.
; Ohne Liste (bis 0.2.4) oder ohne die Kennzeile geschieht nichts.
Function RemovePreviousPayload
  ${IfNot} ${FileExists} "$INSTDIR\${MANIFESTNAME}"
    Return
  ${EndIf}
  ClearErrors
  FileOpen $R0 "$INSTDIR\${MANIFESTNAME}" r
  ${If} ${Errors}
    Return
  ${EndIf}
  FileRead $R0 $R1
  ${Do}                             ; Zeilenende der Kennzeile abschneiden
    StrCpy $R2 $R1 1 -1
    ${If} $R2 == "$\r"
    ${OrIf} $R2 == "$\n"
      StrCpy $R1 $R1 -1
    ${Else}
      ${ExitDo}
    ${EndIf}
  ${Loop}
  ${If} $R1 S!= "${MANIFESTHEAD}"
    FileClose $R0
    Return
  ${EndIf}
  ${Do}
    ClearErrors
    FileRead $R0 $R1
    ${If} ${Errors}
      ${ExitDo}
    ${EndIf}
    ${Do}                           ; Zeilenende abschneiden
      StrCpy $R2 $R1 1 -1
      ${If} $R2 == "$\r"
      ${OrIf} $R2 == "$\n"
        StrCpy $R1 $R1 -1
      ${Else}
        ${ExitDo}
      ${EndIf}
    ${Loop}
    StrCpy $R2 $R1 1 -1
    ${If} $R2 == "\"                ; Ordner stehen mit „\“ am Ende in der Liste
      StrCpy $R1 $R1 -1
      Push $R1
      Call IsPlainRelativePath
      Pop $R2
      ${If} $R2 == 1
        RMDir "$INSTDIR\$R1"
      ${EndIf}
    ${Else}
      Push $R1
      Call IsPlainRelativePath
      Pop $R2
      ${If} $R2 == 1
        Delete "$INSTDIR\$R1"
      ${EndIf}
    ${EndIf}
  ${Loop}
  FileClose $R0
FunctionEnd

; ---------- Rechte des Installationsordners (#46) ----------
; Unter dem Benutzerprofil (Vorgabe: %LOCALAPPDATA%\Programs\Lagerfeuer) kann nur der Benutzer selbst schreiben.
; Ein frei gewählter Ordner wie C:\Lagerfeuer erbt dagegen die Rechte seines Elternordners; im Laufwerksstamm dürfen
; oft alle angemeldeten Benutzer Dateien anlegen. Ein anderer Benutzer könnte dann Lagerfeuer.exe oder eine DLL
; austauschen, und Lagerfeuer liefe beim nächsten Start mit den Rechten des Opfers.

; $R0 = 1, wenn $INSTDIR nicht im Benutzerprofil liegt, sonst 0
Function IsOutsideProfile
  Push $R1
  Push $R2
  StrLen $R1 "$PROFILE"
  StrCpy $R2 "$INSTDIR" $R1
  StrCpy $R0 1
  ${If} $R2 == "$PROFILE"           ; der Vergleich von LogicLib achtet nicht auf Groß- und Kleinschreibung
    StrCpy $R2 "$INSTDIR" 1 $R1
    ${If} $R2 == "\"
    ${OrIf} $R2 == ""
      StrCpy $R0 0
    ${EndIf}
  ${EndIf}
  Pop $R2
  Pop $R1
FunctionEnd

Function DirectoryLeave
  Push $R0
  Call IsOutsideProfile
  ${If} $R0 == 1
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "Der Ordner liegt außerhalb deines Benutzerprofils.$\r$\n$\r$\nEmpfohlen ist der vorgeschlagene Ordner unter %LOCALAPPDATA%\Programs. Das Setup beschränkt die Rechte des gewählten Ordners auf dich, damit kein anderer Benutzer die Programmdateien austauschen kann. Gehört der Ordner einem anderen Benutzer oder liegen dort schon fremde Dateien, hilft das nicht.$\r$\n$\r$\nTrotzdem dorthin installieren?" IDYES ok
    Pop $R0
    Abort
  ok:
  ${EndIf}
  Pop $R0
FunctionEnd

; Außerhalb des Profils: Vererbung aus, Vollzugriff nur für den Benutzer, SYSTEM und die Administratoren; was
; schon im Ordner liegt, übernimmt diese Rechte. Gelingt das nicht, wird nicht installiert.
Function SecureInstDir
  Push $R0
  Push $R1
  Push $R2
  Call IsOutsideProfile
  ${If} $R0 == 1
    CreateDirectory "$INSTDIR"
    ; Den Benutzer über seine SID nennen, nicht als %USERDOMAIN%\%USERNAME%: Die Umgebungsvariablen stimmen nicht
    ; in jeder Sitzung (über SSH steht dort etwa WORKGROUP), und dann fände icacls das Konto nicht.
    ; whoami /user /fo csv /nh liefert "rechner\name","S-1-5-21-…".
    nsExec::ExecToStack '"$SYSDIR\whoami.exe" /user /fo csv /nh'
    Pop $R0
    Pop $R1
    StrCpy $R2 ""
    ${If} $R0 == 0
      ${WordFind} '$R1' '","' "+2" $R2
      ${WordFind} '$R2' '"' "+1" $R2
    ${EndIf}
    StrCpy $R1 $R2 4
    ${If} $R1 == "S-1-"
      nsExec::ExecToStack '"$SYSDIR\icacls.exe" "$INSTDIR" /inheritance:r /grant:r "*$R2:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"'
      Pop $R0                       ; Exitcode
      Pop $R1                       ; Ausgabe
    ${Else}
      StrCpy $R0 1                  ; SID unbekannt: nicht installieren
    ${EndIf}
    ${If} $R0 != 0
      MessageBox MB_OK|MB_ICONSTOP "Die Rechte des Installationsordners ließen sich nicht auf dich beschränken:$\r$\n$INSTDIR$\r$\n$\r$\nBitte wähle einen Ordner in deinem Benutzerprofil." /SD IDOK
      Pop $R2
      Pop $R1
      Pop $R0
      Abort
    ${EndIf}
    ; Was schon darin liegt, erbt ab jetzt nur noch diese Rechte (fremde ausdrückliche Rechte fallen weg).
    nsExec::ExecToStack '"$SYSDIR\icacls.exe" "$INSTDIR\*" /reset /T /C /Q'
    Pop $R0
    Pop $R1
  ${EndIf}
  Pop $R2
  Pop $R1
  Pop $R0
FunctionEnd

; ---------- Installation ----------

Section "Lagerfeuer (erforderlich)" SecMain
  SectionIn RO
  SetShellVarContext current
  Call CloseRunningApp
  Call SecureInstDir
  ; Was die bisherige Fassung installiert hat und die neue nicht mehr enthält, soll nicht liegen bleiben.
  Call RemovePreviousPayload

  SetOutPath "$INSTDIR"
  File /r "${SRCDIR}\*.*"
  File "/oname=${MANIFESTNAME}" "${MANIFESTFILE}"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  CreateShortcut "$SMPROGRAMS\Lagerfeuer.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0 SW_SHOWNORMAL "" "Hörbücher aus Audiobookshelf"

  ; Eintrag unter "Apps & Features"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayName" "${DISPLAYNAME}"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr   HKCU "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr   HKCU "${UNINSTKEY}" "URLInfoAbout" "${HOMEPAGE}"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXENAME}"
  WriteRegStr   HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr   HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"

  WriteRegStr HKCU "${SETTINGSKEY}" "InstallDir" "$INSTDIR"
  DeleteRegKey HKCU "${OLDSETTINGSKEY}"
SectionEnd

Section "Desktop-Verknüpfung" SecDesktop
  ; Beim Update keine gelöschte Verknüpfung wiederherstellen.
  ${If} $Restart != 1
    CreateShortcut "$DESKTOP\Lagerfeuer.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0
  ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "Die Anwendung selbst inkl. .NET- und Windows-App-SDK-Laufzeit."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Legt eine Verknüpfung auf dem Desktop an."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- Deinstallation ----------

Section "un.Lagerfeuer" SecUnMain
  SectionIn RO
  SetShellVarContext current
  Call un.CloseRunningApp

  Delete "$SMPROGRAMS\Lagerfeuer.lnk"
  Delete "$DESKTOP\Lagerfeuer.lnk"

  ; Nur entfernen, was das Setup installiert hat (#47). Der Ordner ist frei wählbar; was dort sonst noch liegt,
  ; gehört nicht Lagerfeuer und bleibt. Der Ordner selbst verschwindet nur, wenn er danach leer ist.
  !include "${PAYLOADLIST}"
  Delete "$INSTDIR\${MANIFESTNAME}"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "${SETTINGSKEY}"
  DeleteRegKey HKCU "${OLDSETTINGSKEY}"
  DeleteRegKey /ifempty HKCU "${PUBLISHERKEY}"

  ; Protokoll-Handler lagerfeuer:// und audiobookshelf:// (für SSO; legt die App selbst an) – nur wenn sie auf uns zeigen
  ReadRegStr $0 HKCU "${PROTOCOLKEY}\shell\open\command" ""
  ${If} $0 == '"$INSTDIR\${EXENAME}" "%1"'
    DeleteRegKey HKCU "${PROTOCOLKEY}"
  ${EndIf}
  ReadRegStr $0 HKCU "${FALLBACKPROTOCOLKEY}\shell\open\command" ""
  ${If} $0 == '"$INSTDIR\${EXENAME}" "%1"'
    DeleteRegKey HKCU "${FALLBACKPROTOCOLKEY}"
  ${EndIf}
SectionEnd

Section /o "un.Benutzerdaten entfernen" SecUnData
  ; Einstellungen, gespeicherte Anmeldung, Audio-/Cover-Cache, Log
  RMDir /r "$LOCALAPPDATA\Lagerfeuer"
SectionEnd

!insertmacro MUI_UNFUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecUnMain} "Entfernt die Anwendung. Andere Dateien im Installationsordner bleiben."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecUnData} "Löscht zusätzlich Einstellungen, gespeicherte Anmeldung und den Audio-Cache."
!insertmacro MUI_UNFUNCTION_DESCRIPTION_END

!delfile "${PAYLOADLIST}"
!delfile "${MANIFESTFILE}"
