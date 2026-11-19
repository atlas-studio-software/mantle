@echo off
rem put-back-previous.cmd - the Start menu's "Put back the previous Mantle version", and a double-clickable copy.
rem It runs put-back-previous.ps1 with -Ask: it says what it would do and does it only after "Y". The script is
rem copied to %TEMP% first and the whole run is ONE line ending in exit, because the installer it starts replaces
rem this folder and cmd.exe reads a batch file line by line. Needs no Mantle, no Node and no Electron.
setlocal
set "MANTLE_PB=%TEMP%\mantle-put-back-%RANDOM%%RANDOM%.ps1"
copy /y "%~dp0put-back-previous.ps1" "%MANTLE_PB%" >nul && "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%MANTLE_PB%" -Ask %* & del "%MANTLE_PB%" >nul 2>&1 & exit /b
