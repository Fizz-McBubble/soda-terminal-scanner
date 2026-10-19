; -*- coding: utf-8 -*-
; Ordered single solid stream: UI first, payload after launch, then wait for UI.
Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 96
; A late plugin would require decompressing the payload before CreateProcess.
ReserveFile /plugin System.dll
Name "Soda Terminal · 画面扫描"
Icon "src\Soda.Scanner.Setup\Resources\soda-brand-icon.ico"
OutFile "${OUTPUT_EXE}"
RequestExecutionLevel user
SilentInstall silent
CRCCheck force
ManifestDPIAware true
!ifndef INSTALLER_VERSION
  !define INSTALLER_VERSION "1.0.8"
!endif
!ifndef RUNTIME_ASSET_NAME
  !define RUNTIME_ASSET_NAME "soda-scanner-runtime-18-rc8-8-win-x64.zip"
!endif
VIProductVersion "${INSTALLER_VERSION}.0"
VIAddVersionKey "ProductName" "Soda Terminal · 画面扫描"
VIAddVersionKey "FileDescription" "画面扫描安装程序"
VIAddVersionKey "FileVersion" "${INSTALLER_VERSION}"
VIAddVersionKey "LegalCopyright" "Soda Terminal contributors; NSIS contributors"
!include "FileFunc.nsh"
Section
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=Soda-Scanner-Setup-inner.exe "${INNER_EXE}"
  ${GetParameters} $0
  StrCpy $8 '"$PLUGINSDIR\Soda-Scanner-Setup-inner.exe" $0'
  ; NSIS is x86: STARTUPINFO=68 and PROCESS_INFORMATION=16 bytes.
  System::Alloc 68
  Pop $1
  System::Call '*$1(i 68)'
  System::Alloc 16
  Pop $2
  System::Call 'kernel32::CreateProcessW(p 0, w r8, p 0, p 0, i 0, i 0, p 0, p 0, p r1, p r2) i.r3'
  IntCmp $3 0 launch_failed
  System::Call '*$2(p .r4, p .r5, i .r6, i .r7)'
  System::Call 'kernel32::CloseHandle(p r5)'
  System::Free $1
  System::Free $2
  SetOutPath "$PLUGINSDIR\payload"
  ClearErrors
  ; Keep the two .NET bundles close enough for the 128 MB solid dictionary.
  File /oname=Soda-Scanner-Uninstall.exe "${STUB_EXE}"
  IfErrors extract_failed
  File /oname=${RUNTIME_ASSET_NAME} "${RUNTIME_ARCHIVE}"
  IfErrors extract_failed
  FileOpen $1 "$PLUGINSDIR\payload\ready.tmp" w
  IfErrors extract_failed
  FileWrite $1 "SODA-OFFLINE-PAYLOAD-1"
  FileClose $1
  IfErrors extract_failed
  ; Publish only a closed, complete marker; child never observes an empty file.
  Rename "$PLUGINSDIR\payload\ready.tmp" "$PLUGINSDIR\payload\ready"
  IfErrors extract_failed
  Goto wait_child
extract_failed:
  FileOpen $1 "$PLUGINSDIR\payload\failed" w
  FileClose $1
wait_child:
  ; The private extraction directory remains available for the child's lifetime.
  System::Call 'kernel32::WaitForSingleObject(p r4, i -1) i.r3'
  System::Call 'kernel32::GetExitCodeProcess(p r4, *i .r3)'
  System::Call 'kernel32::CloseHandle(p r4)'
  SetErrorLevel $3
  Quit
launch_failed:
  System::Free $1
  System::Free $2
  SetErrorLevel 2
SectionEnd
