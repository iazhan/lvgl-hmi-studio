; ============================================================================
; hmi-editor.nsi —— 智枢 HMI 编辑器的 Windows 安装包（NSIS 3 / Unicode）
;
; 负载由 tools/publish-win.ps1 产出（编辑器 win-x64 自包含 + 随包运行时）：
;   <PAYLOAD>\HmiEditor.App.exe
;   <PAYLOAD>\chenge-runtime\chenge-runtime.exe | chenge-preview-host.exe | SDL2.dll
;
; 用法（在 Windows 上，或 Git Bash 里若有 makensis）：
;   makensis -DVERSION=2.22.1 -DPAYLOAD=D:\dist\win-x64 hmi-editor.nsi
; 不传参时用下面的默认值，适合从仓库根直接跑。
; ============================================================================

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef PAYLOAD
  !define PAYLOAD "..\..\dist\win-x64"
!endif

Unicode true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define APP_NAME      "智枢 HMI 编辑器"
!define APP_EXE       "HmiEditor.App.exe"
!define APP_PUBLISHER "智枢"
!define APP_KEY       "Software\Microsoft\Windows\CurrentVersion\Uninstall\HmiStudio"
!define UNINST_KEY    "Software\HmiStudio"

Name "${APP_NAME}"
OutFile "..\..\dist\HMI-Studio-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\HMI Studio"
InstallDirRegKey HKLM "${UNINST_KEY}" "InstallDir"
RequestExecutionLevel admin

; ── 界面 ──
!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ${APP_NAME}"
!define MUI_FINISHPAGE_SHOWREADME ""
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${NSISDIR}\Docs\Modern UI\License.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ============================================================================
Section "编辑器与运行时（必需）" SecCore
    SectionIn RO
    SetOutPath "$INSTDIR"
    ; /r 递归：publish 出来的 260 多个文件 + chenge-runtime 子目录一起装
    File /r "${PAYLOAD}\*.*"

    WriteRegStr HKLM "${UNINST_KEY}" "InstallDir" "$INSTDIR"
    WriteRegStr HKLM "${APP_KEY}" "DisplayName"     "${APP_NAME}"
    WriteRegStr HKLM "${APP_KEY}" "DisplayVersion"  "${VERSION}"
    WriteRegStr HKLM "${APP_KEY}" "Publisher"       "${APP_PUBLISHER}"
    WriteRegStr HKLM "${APP_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${APP_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "${APP_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegDWORD HKLM "${APP_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${APP_KEY}" "NoRepair" 1

    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegDWORD HKLM "${APP_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "开始菜单快捷方式" SecStartMenu
    CreateDirectory "$SMPROGRAMS\${APP_NAME}"
    CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
    CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "桌面快捷方式" SecDesktop
    CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

Section /o "把 .hmiproj 关联到本编辑器" SecAssoc
    ; 工程文件后缀由编辑器自己写盘时决定；这里只登记关联，不改文件内容
    WriteRegStr HKCR ".hmiproj" "" "HmiStudio.Project"
    WriteRegStr HKCR "HmiStudio.Project" "" "HMI 工程"
    WriteRegStr HKCR "HmiStudio.Project\DefaultIcon" "" "$INSTDIR\${APP_EXE},0"
    WriteRegStr HKCR "HmiStudio.Project\shell\open\command" "" '"$INSTDIR\${APP_EXE}" "%1"'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
!insertmacro MUI_DESCRIPTION_TEXT ${SecCore}     "编辑器本体 + 随包运行时（设计画布与实时预览都靠它，装完不需要再填运行时路径）"
!insertmacro MUI_DESCRIPTION_TEXT ${SecStartMenu} "在开始菜单建一个程序组"
!insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop}  "在桌面放一个快捷方式"
!insertmacro MUI_DESCRIPTION_TEXT ${SecAssoc}    "双击 .hmiproj 用本编辑器打开"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ── 装完自查：随包运行时必须在，否则画布/预览会报"找不到运行时" ──
Function .onInstSuccess
    IfFileExists "$INSTDIR\chenge-runtime\chenge-preview-host.exe" +3 0
        MessageBox MB_ICONEXCLAMATION|MB_OK \
            "安装完成，但随包运行时缺失（chenge-runtime\chenge-preview-host.exe）。$\r$\n\
             设计画布与实时预览会不可用 —— 请重新下载完整的安装包，或在设置里手工指定运行时目录。"
FunctionEnd

Section "Uninstall"
    Delete "$INSTDIR\Uninstall.exe"
    Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
    Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
    RMDir  "$SMPROGRAMS\${APP_NAME}"
    Delete "$DESKTOP\${APP_NAME}.lnk"

    ; 整个安装目录（包括随包运行时）。用户的工程放在自己的目录里，不在这里，不会被删
    RMDir /r "$INSTDIR"

    DeleteRegKey HKLM "${APP_KEY}"
    DeleteRegKey HKLM "${UNINST_KEY}"
    DeleteRegKey HKCR ".hmiproj"
    DeleteRegKey HKCR "HmiStudio.Project"
SectionEnd
