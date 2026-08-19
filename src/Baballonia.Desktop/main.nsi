;--------------------------------
;Includes

  !include "MUI2.nsh"
  !include "logiclib.nsh"
  !include "FileFunc.nsh"

;--------------------------------
;Custom defines

  !define NAME "Baballonia Expressions Preview"
  !define APPFILE "Baballonia.Desktop.exe"
  !define PUBLISHER "Baballonia Expressions Preview Contributors"
  !define FILE_VERSION "1.1.1.1"
  !define VERSION "1.1.1.1-expressions.preview.1"
  !ifdef WORKFLOW_VERSION ; override version string with one provided through GitHub Actions.
    !undef VERSION
    !define VERSION "${WORKFLOW_VERSION}"
  !endif
  !ifndef PUBLISH_DIR
    !define PUBLISH_DIR "bin\Release\net10.0\win-x64\publish"
  !endif
  !ifndef OUTPUT_FILE
    !define OUTPUT_FILE "Baballonia-Expressions-Preview-1-Setup-x64.exe"
  !endif
  !define SLUG "${NAME} v${VERSION}"

;--------------------------------
;General

  Name "${NAME}"
  OutFile "${OUTPUT_FILE}"
  ;Default install directory in user's AppData folder
  InstallDir "$LOCALAPPDATA\${NAME}"
  InstallDirRegKey HKCU "Software\${NAME}" ""
  RequestExecutionLevel user
  Target "amd64-unicode"  ; 64-bit
  VIProductVersion "${FILE_VERSION}"
  VIAddVersionKey "ProductName" "${NAME}"
  VIAddVersionKey "ProductVersion" "${VERSION}"
  VIAddVersionKey "FileVersion" "${FILE_VERSION}"
  VIAddVersionKey "FileDescription" "${NAME} Setup"
  VIAddVersionKey "CompanyName" "${PUBLISHER}"
  VIAddVersionKey "LegalCopyright" "Project Babble and Baballonia Expressions Preview contributors"
  VIAddVersionKey "OriginalFilename" "Baballonia-Expressions-Preview-1-Setup-x64.exe"

;--------------------------------
;Compression
  SetCompressor /SOLID lzma ; "/FINAL" can be added to prevent anything from changing this later down the line.
  SetCompressorDictSize 32
  FileBufSize 64
  ManifestDPIAware true
  ;ManifestLongPathAware true ; disabled until the rest of the app is confirmed to have this setup right
  XPStyle on ; Retained for compatibility with the standard NSIS Modern UI configuration.

;--------------------------------
;UI

  !define MUI_ICON "Assets\IconOpaque.ico"
  !define MUI_HEADERIMAGE
  !define MUI_WELCOMEFINISHPAGE_BITMAP "Assets\MUI_WELCOMEFINISHPAGE_BITMAP.bmp"
  !define MUI_HEADERIMAGE_BITMAP "Assets\MUI_HEADERIMAGE_BITMAP.bmp"
  !define MUI_ABORTWARNING
  !define MUI_WELCOMEPAGE_TITLE "${SLUG} Setup"

;--------------------------------
;Pages

  ;Installer pages
  !insertmacro MUI_PAGE_WELCOME
  !insertmacro MUI_PAGE_LICENSE "Assets\license.txt"
  !insertmacro MUI_PAGE_COMPONENTS
  !insertmacro MUI_PAGE_DIRECTORY
  !insertmacro MUI_PAGE_INSTFILES
  !insertmacro MUI_PAGE_FINISH

  ;Uninstaller pages
  !insertmacro MUI_UNPAGE_CONFIRM
  !insertmacro MUI_UNPAGE_INSTFILES

  ;Set UI language
  !insertmacro MUI_LANGUAGE "English"

;--------------------------------
;Section - Install App

  Section "-hidden app"
    SectionIn RO
    SetOutPath "$INSTDIR"

    ;Copy all files except Calibration, Firmware and publish folders
    File /r /x "Calibration" /x "Firmware" /x "*.pdb" "${PUBLISH_DIR}\*"

    ;Create Firmware directory
    CreateDirectory "$INSTDIR\Firmware"

    ;Copy Windows-only Firmware tooling
    CreateDirectory "$INSTDIR\Firmware\Windows"
    SetOutPath "$INSTDIR\Firmware\Windows"
    File /r "${PUBLISH_DIR}\Firmware\Windows\*"

    ;Copy firmware over
    CreateDirectory "$INSTDIR\Firmware\Binaries"
    SetOutPath "$INSTDIR\Firmware\Binaries"
    File /r "${PUBLISH_DIR}\Firmware\Binaries\*"

    ;Create Windows-only Calibration tooling
    CreateDirectory "$INSTDIR\Calibration"
    SetOutPath "$INSTDIR\Calibration"
    File /r /x "*.pdb" "${PUBLISH_DIR}\Calibration\Windows"

    ;Reset output path and write registry values
    SetOutPath "$INSTDIR"

    ;Get size to show in Control Panel
    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0

    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}" "DisplayName" "${NAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}" "DisplayIcon" "$INSTDIR\${APPFILE}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}" "DisplayVersion" "${VERSION}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}" "Publisher" "${PUBLISHER}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}" "InstallLocation" "$INSTDIR"
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}" "EstimatedSize" "$0"

    CreateShortCut "$SMPROGRAMS\${Name}.lnk" "$INSTDIR\${APPFILE}"
    CreateShortCut "$SMPROGRAMS\Uninstall ${Name}.lnk" "$INSTDIR\Uninstall.exe"

    ;Launch app when finished
    ExecShell "" "$INSTDIR\${APPFILE}"

  SectionEnd

;--------------------------------
;Section - Shortcut

  Section "Desktop Shortcut" DeskShort
    CreateShortCut "$DESKTOP\${NAME}.lnk" "$INSTDIR\${APPFILE}"
  SectionEnd

;--------------------------------
;Descriptions

  ;Language strings
  LangString DESC_DeskShort ${LANG_ENGLISH} "Create Shortcut on Desktop."

  ;Assign language strings to sections
  !insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${DeskShort} $(DESC_DeskShort)
  !insertmacro MUI_FUNCTION_DESCRIPTION_END

;--------------------------------
;Remove empty parent directories

  Function un.RMDirUP
    !define RMDirUP '!insertmacro RMDirUPCall'

    !macro RMDirUPCall _PATH
          push '${_PATH}'
          Call un.RMDirUP
    !macroend

    ;$0 - current folder
    ClearErrors

    Exch $0
    ; Remove the parent directory when it is empty.
    RMDir "$0\.."

    IfErrors Skip
    ${RMDirUP} "$0\.."
    Skip:

    Pop $0

  FunctionEnd

;--------------------------------
;Section - Uninstaller

Section "Uninstall"

  ;Prompt to delete local data
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "Do you also want to delete local user data/settings (stored under %APPDATA%\ProjectBabble)?" \
    IDNO skip_userdata

  RMDir /r "$APPDATA\ProjectBabble"

  skip_userdata:

  ;Delete Shortcut
  Delete "$DESKTOP\${NAME}.lnk"

  ;Delete Uninstall
  Delete "$SMPROGRAMS\${Name}.lnk"
  Delete "$SMPROGRAMS\Uninstall ${Name}.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${NAME}"
  Delete "$INSTDIR\Uninstall.exe"

  ;Delete Folder
  RMDir /r "$INSTDIR"
  ${RMDirUP} "$INSTDIR"

SectionEnd
