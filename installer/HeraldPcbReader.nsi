; Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
; SPDX-License-Identifier: GPL-3.0-only
; Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
; additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
; Herald PCB Reader (formerly Altium Reader 2) — per-user installer (no admin rights).
; Makes this program the default for .SchDoc / .PcbDoc / .PrjPcb (previous default restored on uninstall).
; Run again over an existing installation: offers 修復／更新 (install over it, keeping settings) or 解除安裝.
; Replaces an Altium Reader 2 installation (2.0–2.3): its files, shortcuts and Apps entry go, while settings
; (%APPDATA%\AltiumReader2) and the file-type ProgIDs (AltiumReader2.*, which "always use this app" choices point to) stay.
Unicode true
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"

!define APP      "Herald PCB Reader"
!define EXE      "HeraldPcbReader.exe"
!define VER      "2.9.4"
!define AUTHOR   "Johnny.ch.Tsai"
!define REPO     "https://github.com/vip3310495-Johnny/HeraldPcbReader"
!define UNKEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\HeraldPcbReader"
!define CAPKEY   "Software\HeraldPcbReader\Capabilities"
!define OLDAPP   "Altium Reader 2"
!define OLDEXE   "AltiumReader2.exe"
!define OLDUNKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AltiumReader2"

Name "${APP}"
OutFile "..\dist\Herald PCB Reader Setup ${VER}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP}"
InstallDirRegKey HKCU "${UNKEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APP} ${VER} · Copyright (C) 2026 ${AUTHOR} · GNU GPL v3"

!define MUI_ICON "..\src\AdApp\app.ico"
!define MUI_UNICON "..\src\AdApp\app.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "啟動 ${APP}"
; the copyright / licence notice comes first: who wrote it, the GPL, no warranty, trademarks (informational: GPL §9 needs
; no acceptance to install or run the program, so the button just goes on)
!define MUI_LICENSEPAGE_TEXT_TOP "安裝前請閱讀：著作權、授權（GNU GPL v3）、不提供擔保與責任限制"
!define MUI_LICENSEPAGE_TEXT_BOTTOM "本程式為自由軟體；安裝與使用不需要同意授權條款，複製、修改或散布時須遵守 GPL 並保留著作權聲明。"
!define MUI_LICENSEPAGE_BUTTON "下一步(&N) >"
!insertmacro MUI_PAGE_LICENSE "copyright.txt"
Page custom ExistingPage ExistingLeave
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirPre
!define MUI_DIRECTORYPAGE_TEXT_TOP "${APP}（原名 ${OLDAPP}）。這台電腦如果裝過 ${OLDAPP}，會換成新版：設定、最近開啟的檔案與檔案關聯都會保留。"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "TradChinese"

; version information of the setup file itself (Properties → Details)
VIProductVersion "${VER}.0"
VIAddVersionKey /LANG=${LANG_TRADCHINESE} "ProductName" "${APP}"
VIAddVersionKey /LANG=${LANG_TRADCHINESE} "ProductVersion" "${VER}"
VIAddVersionKey /LANG=${LANG_TRADCHINESE} "FileVersion" "${VER}"
VIAddVersionKey /LANG=${LANG_TRADCHINESE} "FileDescription" "${APP} 安裝程式"
VIAddVersionKey /LANG=${LANG_TRADCHINESE} "CompanyName" "${AUTHOR}"
VIAddVersionKey /LANG=${LANG_TRADCHINESE} "LegalCopyright" "Copyright (C) 2026 ${AUTHOR} · GNU GPL v3 · 不提供任何擔保"
VIAddVersionKey /LANG=${LANG_TRADCHINESE} "Comments" "${REPO}"

Var OldDir
Var OldVer
Var Repair
Var hRepair
Var hRemove

Function .onInit
  ReadRegStr $OldDir HKCU "${UNKEY}" "InstallLocation"
  ReadRegStr $OldVer HKCU "${UNKEY}" "DisplayVersion"
  StrCpy $Repair 0
  ${If} $OldDir != ""
  ${AndIf} ${FileExists} "$OldDir\${EXE}"
    StrCpy $Repair 1
    StrCpy $INSTDIR $OldDir
  ${EndIf}
FunctionEnd

; shown only when a previous installation exists
Function ExistingPage
  ${If} $Repair == 0
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "已經安裝 ${APP}" "可以直接修復或更新，不必先解除安裝"
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 30u "這台電腦已經安裝 ${APP} $OldVer$\r$\n位置：$OldDir"
  Pop $0
  ${NSD_CreateRadioButton} 0 40u 100% 14u "修復／更新為 ${VER}（建議）：覆蓋安裝，保留設定、最近開啟的檔案與檔案關聯"
  Pop $hRepair
  ${NSD_Check} $hRepair
  ${NSD_CreateRadioButton} 0 60u 100% 14u "解除安裝 ${APP}"
  Pop $hRemove
  nsDialogs::Show
FunctionEnd

Function ExistingLeave
  ${NSD_GetState} $hRemove $0
  ${If} $0 == ${BST_CHECKED}
    ExecWait '"$OldDir\Uninstall ${APP}.exe" _?=$OldDir'
    Delete "$OldDir\Uninstall ${APP}.exe"
    RMDir "$OldDir"
    Quit
  ${EndIf}
FunctionEnd

Function DirPre
  ${If} $Repair == 1
    Abort   ; repair / update installs into the existing folder
  ${EndIf}
FunctionEnd

!macro OpenWith EXT PROGID DESC ICON
  WriteRegStr HKCU "Software\Classes\${PROGID}" "" "${DESC}"
  WriteRegStr HKCU "Software\Classes\${PROGID}\DefaultIcon" "" "$INSTDIR\icons\${ICON}.ico"
  WriteRegStr HKCU "Software\Classes\${PROGID}\shell\open\command" "" '"$INSTDIR\${EXE}" "%1"'
  WriteRegStr HKCU "Software\Classes\${EXT}\OpenWithProgids" "${PROGID}" ""
  WriteRegStr HKCU "Software\Classes\Applications\${EXE}\SupportedTypes" "${EXT}" ""
!macroend
; Make this program the default for EXT; the previous default ProgID is kept so uninstalling restores it.
; (Has no effect where the user already picked a program with "Always use this app" — Windows keeps that choice.)
!macro DefaultFor EXT PROGID
  ReadRegStr $0 HKCU "Software\Classes\${EXT}" ""
  StrCmp $0 "${PROGID}" +2
  WriteRegStr HKCU "Software\Classes\${PROGID}" "PrevDefault" $0
  WriteRegStr HKCU "Software\Classes\${EXT}" "" "${PROGID}"
!macroend
!macro UnDefaultFor EXT PROGID
  ReadRegStr $0 HKCU "Software\Classes\${EXT}" ""
  StrCmp $0 "${PROGID}" 0 +4
  ReadRegStr $1 HKCU "Software\Classes\${PROGID}" "PrevDefault"
  WriteRegStr HKCU "Software\Classes\${EXT}" "" $1
  Nop
!macroend
!macro UnOpenWith EXT PROGID
  DeleteRegKey HKCU "Software\Classes\${PROGID}"
  DeleteRegValue HKCU "Software\Classes\${EXT}\OpenWithProgids" "${PROGID}"
!macroend

; Altium Reader 2 (same program, old name): remove its files, shortcuts and Apps entry.
; Its ProgIDs are rewritten below to start this program, so file associations carry over.
Function RemoveOldName
  ReadRegStr $1 HKCU "${OLDUNKEY}" "InstallLocation"
  StrCmp $1 "" done
  DetailPrint "移除舊版 ${OLDAPP}（$1）"
  Delete "$1\${OLDEXE}"
  Delete "$1\icons\SchDoc.ico"
  Delete "$1\icons\PcbDoc.ico"
  Delete "$1\icons\PrjPcb.ico"
  RMDir "$1\icons"
  Delete "$1\Uninstall ${OLDAPP}.exe"
  RMDir "$1"
  Delete "$SMPROGRAMS\${OLDAPP}.lnk"
  Delete "$DESKTOP\${OLDAPP}.lnk"
  DeleteRegKey HKCU "${OLDUNKEY}"
  DeleteRegValue HKCU "Software\RegisteredApplications" "${OLDAPP}"
  DeleteRegKey HKCU "Software\AltiumReader2\Capabilities"
  DeleteRegKey /ifempty HKCU "Software\AltiumReader2"
  done:
FunctionEnd

Section "Install"
  ; a running copy would lock the exe: ask it to close (it saves its settings), then make sure it is gone
  ; taskkill by its full path: not a taskkill.exe someone placed next to the setup (e.g. in Downloads)
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM "${EXE}" /IM "${OLDEXE}"'
  Pop $0
  Sleep 1500
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${EXE}" /IM "${OLDEXE}"'
  Pop $0
  Call RemoveOldName
  SetOutPath "$INSTDIR"
  File "..\publish\${EXE}"
  ; the program's licence (GNU GPL v3) and its notice (copyright, no warranty, the AvalonDock permission, source code);
  ; licences of the third-party components (tools\notices.ps1 builds it). The program shows all three (說明 menu).
  File "..\LICENSE.txt"
  File "..\NOTICE.txt"
  File "..\src\AdApp\Help\THIRD-PARTY-NOTICES.txt"
  ; file-type icons for Explorer: schematic yellow, PCB green, project blue (same colours as the document tabs)
  SetOutPath "$INSTDIR\icons"
  File "icons\SchDoc.ico"
  File "icons\PcbDoc.ico"
  File "icons\PrjPcb.ico"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall ${APP}.exe"
  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${EXE}"
  CreateShortCut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}"

  WriteRegStr HKCU "Software\Classes\Applications\${EXE}" "FriendlyAppName" "${APP}"
  WriteRegStr HKCU "Software\Classes\Applications\${EXE}\shell\open\command" "" '"$INSTDIR\${EXE}" "%1"'
  ; a choice made for the old exe ("always use this app") keeps working: same key, new program
  DeleteRegKey HKCU "Software\Classes\Applications\${OLDEXE}\SupportedTypes"
  WriteRegStr HKCU "Software\Classes\Applications\${OLDEXE}" "FriendlyAppName" "${APP}"
  WriteRegStr HKCU "Software\Classes\Applications\${OLDEXE}\shell\open\command" "" '"$INSTDIR\${EXE}" "%1"'
  !insertmacro OpenWith ".SchDoc" "AltiumReader2.SchDoc" "Altium 電路圖" "SchDoc"
  !insertmacro OpenWith ".PcbDoc" "AltiumReader2.PcbDoc" "Altium PCB" "PcbDoc"
  !insertmacro OpenWith ".PrjPcb" "AltiumReader2.PrjPcb" "Altium PCB 專案" "PrjPcb"
  !insertmacro OpenWith ".kicad_sch" "AltiumReader2.KicadSch" "KiCad 電路圖" "SchDoc"
  ; list in Settings → Default apps, so every file type can be switched on one page
  WriteRegStr HKCU "${CAPKEY}" "ApplicationName" "${APP}"
  WriteRegStr HKCU "${CAPKEY}" "ApplicationDescription" "Altium Designer 電路圖 / PCB 檢視器"
  WriteRegStr HKCU "${CAPKEY}\FileAssociations" ".SchDoc" "AltiumReader2.SchDoc"
  WriteRegStr HKCU "${CAPKEY}\FileAssociations" ".PcbDoc" "AltiumReader2.PcbDoc"
  WriteRegStr HKCU "${CAPKEY}\FileAssociations" ".PrjPcb" "AltiumReader2.PrjPcb"
  WriteRegStr HKCU "${CAPKEY}\FileAssociations" ".kicad_sch" "AltiumReader2.KicadSch"
  WriteRegStr HKCU "Software\RegisteredApplications" "${APP}" "${CAPKEY}"
  !insertmacro DefaultFor ".SchDoc" "AltiumReader2.SchDoc"
  !insertmacro DefaultFor ".PcbDoc" "AltiumReader2.PcbDoc"
  !insertmacro DefaultFor ".PrjPcb" "AltiumReader2.PrjPcb"

  WriteRegStr HKCU "${UNKEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNKEY}" "DisplayVersion" "${VER}"
  WriteRegStr HKCU "${UNKEY}" "Publisher" "${AUTHOR}"
  WriteRegStr HKCU "${UNKEY}" "Comments" "Copyright (C) 2026 ${AUTHOR} · GNU GPL v3 · 不提供任何擔保"
  WriteRegStr HKCU "${UNKEY}" "URLInfoAbout" "${REPO}"
  WriteRegStr HKCU "${UNKEY}" "HelpLink" "${REPO}"
  WriteRegStr HKCU "${UNKEY}" "DisplayIcon" "$INSTDIR\${EXE},0"
  WriteRegStr HKCU "${UNKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNKEY}" "UninstallString" '"$INSTDIR\Uninstall ${APP}.exe"'
  WriteRegDWORD HKCU "${UNKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNKEY}" "NoRepair" 1
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section "Uninstall"
  Delete "$INSTDIR\icons\SchDoc.ico"
  Delete "$INSTDIR\icons\PcbDoc.ico"
  Delete "$INSTDIR\icons\PrjPcb.ico"
  RMDir "$INSTDIR\icons"
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\NOTICE.txt"
  Delete "$INSTDIR\Uninstall ${APP}.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  !insertmacro UnDefaultFor ".SchDoc" "AltiumReader2.SchDoc"
  !insertmacro UnDefaultFor ".PcbDoc" "AltiumReader2.PcbDoc"
  !insertmacro UnDefaultFor ".PrjPcb" "AltiumReader2.PrjPcb"
  !insertmacro UnOpenWith ".SchDoc" "AltiumReader2.SchDoc"
  !insertmacro UnOpenWith ".PcbDoc" "AltiumReader2.PcbDoc"
  !insertmacro UnOpenWith ".PrjPcb" "AltiumReader2.PrjPcb"
  !insertmacro UnOpenWith ".kicad_sch" "AltiumReader2.KicadSch"
  DeleteRegKey HKCU "Software\Classes\Applications\${EXE}"
  DeleteRegKey HKCU "Software\Classes\Applications\${OLDEXE}"
  DeleteRegValue HKCU "Software\RegisteredApplications" "${APP}"
  DeleteRegKey HKCU "${CAPKEY}"
  DeleteRegKey /ifempty HKCU "Software\HeraldPcbReader"
  DeleteRegKey HKCU "${UNKEY}"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd
