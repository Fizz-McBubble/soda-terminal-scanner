; -*- coding: utf-8 -*-
; Only decompress and launch the independently verified installation program.
; Installation, repair and uninstall remain in the unchanged managed engine.
Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
Name "Soda Terminal 扫描助手"
OutFile "${OUTPUT_EXE}"
RequestExecutionLevel user
SilentInstall silent
CRCCheck force
ManifestDPIAware true
VIProductVersion "1.0.2.0"
VIAddVersionKey "ProductName" "Soda Terminal 扫描助手"
VIAddVersionKey "FileDescription" "扫描助手安装程序"
VIAddVersionKey "FileVersion" "1.0.2"
VIAddVersionKey "LegalCopyright" "Soda Terminal contributors; NSIS contributors"
!include "FileFunc.nsh"
Section
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=Soda-Scanner-Setup-inner.exe "${INNER_EXE}"
  ${GetParameters} $0
  ClearErrors
  ExecWait '"$PLUGINSDIR\Soda-Scanner-Setup-inner.exe" $0' $1
  IfErrors launch_failed
  SetErrorLevel $1
  Quit
launch_failed:
  SetErrorLevel 2
SectionEnd
