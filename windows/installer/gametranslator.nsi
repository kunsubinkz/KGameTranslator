; Game Translator — bộ cài per-user (không cần quyền admin).
; Dùng: makensis /DARCH=x64 /DVERSION=1.0.0 /DSOURCE=<thư mục publish> /DOUTFILE=<file .exe> gametranslator.nsi
Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"

!ifndef ARCH
  !define ARCH "x64"
!endif
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef SOURCE
  !define SOURCE "..\artifacts\publish\win-${ARCH}"
!endif
!ifndef OUTFILE
  !define OUTFILE "..\artifacts\GameTranslator-${VERSION}-${ARCH}-setup.exe"
!endif

!define APPNAME "Game Translator"
!define APPEXE "GameTranslator.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\KGameTranslator"

Name "${APPNAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\GameTranslator"
InstallDirRegKey HKCU "Software\KGameTranslator" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION} (${ARCH})"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "KGameTranslator"
VIAddVersionKey "FileDescription" "${APPNAME} - bộ cài"

!define MUI_ICON "icon.ico"
!define MUI_UNICON "icon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Mở ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Vietnamese"
!insertmacro MUI_LANGUAGE "English"

Function CloseRunningApp
  ; Đóng app đang chạy trước khi cài đè hoặc gỡ.
  nsExec::Exec 'taskkill /IM ${APPEXE} /F'
  Pop $0
  Sleep 500
FunctionEnd

Section "Cài đặt" SecMain
  Call CloseRunningApp
  SetOutPath "$INSTDIR"
  File /r "${SOURCE}\*.*"
  File "icon.ico"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\icon.ico"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Gỡ cài đặt.lnk" "$INSTDIR\uninstall.exe"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\icon.ico"

  WriteRegStr HKCU "Software\KGameTranslator" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "KGameTranslator"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\icon.ico"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Call un.CloseRunningApp
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\KGameTranslator"
  ; Hồ sơ game, cache và API key (Credential Manager) được giữ lại để cài lại không mất dữ liệu.
  ; Dữ liệu dịch offline (Python, PyTorch, model: vài GB) chỉ xóa khi người dùng đồng ý.
  IfFileExists "$LOCALAPPDATA\GameTranslator\nllb\*.*" 0 +3
    MessageBox MB_YESNO|MB_ICONQUESTION "Xóa luôn dữ liệu dịch offline (Python, PyTorch, model NLLB; có thể vài GB)?" /SD IDNO IDNO +2
    RMDir /r "$LOCALAPPDATA\GameTranslator\nllb"
SectionEnd

Function un.CloseRunningApp
  nsExec::Exec 'taskkill /IM ${APPEXE} /F'
  Pop $0
  Sleep 500
FunctionEnd
