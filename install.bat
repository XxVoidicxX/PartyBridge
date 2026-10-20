@echo off
rem Installs PartyBridge for the current user: copies dist\PartyBridge.exe to %LOCALAPPDATA%\Programs\PartyBridge,
rem adds a Start menu shortcut (so Windows search finds "PartyBridge"), then opens the app.
setlocal
set "SRC=%~dp0dist\PartyBridge.exe"
set "DEST=%LOCALAPPDATA%\Programs\PartyBridge"
set "REPORT=%~dp0install_report.txt"

if not exist "%SRC%" (
    echo dist\PartyBridge.exe not found. Build it first, see README.md.
    echo FAIL no dist exe > "%REPORT%"
    pause
    exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"
copy /y "%SRC%" "%DEST%\PartyBridge.exe" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'PartyBridge.lnk';" ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk);" ^
  "$s.TargetPath = '%DEST%\PartyBridge.exe'; $s.WorkingDirectory = '%DEST%';" ^
  "$s.Description = 'Discord and Xbox party voice bridge'; $s.Save();" ^
  "$ok = (Test-Path $lnk) -and (Test-Path '%DEST%\PartyBridge.exe');" ^
  "Set-Content -Path '%REPORT%' -Value ((@('OK','FAIL')[[int](-not $ok)]) + ' ' + $lnk)"

start "" "%DEST%\PartyBridge.exe"
