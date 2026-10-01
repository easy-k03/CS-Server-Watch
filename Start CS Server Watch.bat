@echo off
cd /d "%~dp0"
if not exist "%~dp0publish\CsServerWatch.exe" (
  echo CS Server Watch has not been built yet.
  echo Expected: %~dp0publish\CsServerWatch.exe
  pause
  exit /b 1
)
start "" "%~dp0publish\CsServerWatch.exe"
