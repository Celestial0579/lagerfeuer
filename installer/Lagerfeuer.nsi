; NSIS-Installer für Lagerfeuer (Audiobookshelf für Windows)
;
; Installiert pro Benutzer nach %LOCALAPPDATA%\Programs\Lagerfeuer – ohne Adminrechte.
; Dadurch kann der eingebaute Updater Updates ohne UAC-Abfrage einspielen.
;
; Aufruf über build/publish.ps1 -Installer, oder manuell:
;   makensis /INPUTCHARSET UTF8 /DVERSION=0.1.0 /DARCH=x64 /DSRCDIR=<publish-ordner> /DOUTFILE=<setup.exe> /DICON=<ico> Lagerfeuer.nsi
;
; Unbeaufsichtigt:  Lagerfeuer-…-setup.exe /S [/D=C:\Pfad]
; Vom Updater:      Lagerfeuer-…-setup.exe /S /RESTART   (wartet auf das Beenden der App und startet sie danach neu)
; Deinstallation:   uninstall.exe /S

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
RequestExecutionLevel user

!ifndef VERSION
  !error "VERSION fehlt (/DVERSION=x.y.z)"
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

!define APPNAME     "Lagerfeuer"
!define DISPLAYNAME "Lagerfeuer – Audiobookshelf für Windows"
!define PUBLISHER   "Celestial0579"
!define EXENAME     "Lagerfeuer.exe"
!define UNINSTKEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
!define SETTINGSKEY "Software\${APPNAME}"
!define PROTOCOLKEY "Software\Classes\audiobookshelf"
!define HOMEPAGE    "https://github.com/Celestial0579/lagerfeuer"

Name "${DISPLAYNAME}"
OutFile "${OUTFILE}"
BrandingText "${APPNAME} ${VERSION} (${ARCH})"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "${SETTINGSKEY}" "InstallDir"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1031 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1031 "FileDescription" "${DISPLAYNAME} – Setup"
VIAddVersionKey /LANG=1031 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1031 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1031 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1031 "LegalCopyright" "GPL-3.0"

!include MUI2.nsh
!include LogicLib.nsh
!include x64.nsh
!include FileFunc.nsh
!include WinVer.nsh

Var Restart

; ---------- Oberfläche ----------
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!define MUI_FINISHPAGE_RUN_TEXT "Lagerfeuer jetzt starten"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
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
!macro CLOSE_APP_FUNC un
Function ${un}CloseRunningApp
  StrCpy $R8 0
  retry:
    nsExec::ExecToStack 'cmd.exe /C tasklist /FI "IMAGENAME eq ${EXENAME}" /NH | find /I "${EXENAME}"'
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

Function .onInit
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

  StrCpy $Restart 0
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RESTART" $R1
  ${IfNot} ${Errors}
    StrCpy $Restart 1
  ${EndIf}
FunctionEnd

Function .onInstSuccess
  ${If} $Restart == 1
    Call LaunchApp
  ${EndIf}
FunctionEnd

; ---------- Installation ----------

Section "Lagerfeuer (erforderlich)" SecMain
  SectionIn RO
  SetShellVarContext current
  Call CloseRunningApp

  SetOutPath "$INSTDIR"
  File /r "${SRCDIR}\*.*"
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

  ; Nur löschen, wenn es wirklich unser Ordner ist.
  ${If} ${FileExists} "$INSTDIR\${EXENAME}"
    RMDir /r "$INSTDIR"
  ${Else}
    Delete "$INSTDIR\uninstall.exe"
    RMDir "$INSTDIR"
  ${EndIf}

  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "${SETTINGSKEY}"

  ; Protokoll-Handler audiobookshelf:// (für SSO; legt die App selbst an) – nur wenn er auf uns zeigt
  ReadRegStr $0 HKCU "${PROTOCOLKEY}\shell\open\command" ""
  ${If} $0 == '"$INSTDIR\${EXENAME}" "%1"'
    DeleteRegKey HKCU "${PROTOCOLKEY}"
  ${EndIf}
SectionEnd

Section /o "un.Benutzerdaten entfernen" SecUnData
  ; Einstellungen, gespeicherte Anmeldung, Audio-/Cover-Cache, Log
  RMDir /r "$LOCALAPPDATA\Lagerfeuer"
SectionEnd

!insertmacro MUI_UNFUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecUnMain} "Entfernt die Anwendung."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecUnData} "Löscht zusätzlich Einstellungen, gespeicherte Anmeldung und den Audio-Cache."
!insertmacro MUI_UNFUNCTION_DESCRIPTION_END
