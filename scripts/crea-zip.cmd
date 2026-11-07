@echo off
rem Crea lo zip da distribuire, uguale a quello delle release: SpeedVision.exe + FFmpeg + licenze.
rem Il risultato e' nella cartella "out" del progetto: out\SpeedVision (da provare) e out\SpeedVision-v...-win-x64.zip
cd /d "%~dp0.."
set /p VER=Numero di versione (es. 0.2.0, Invio per 0.0.0): 
if "%VER%"=="" set VER=0.0.0
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-release.ps1 -Version %VER% -OutDir out
if errorlevel 1 (
  echo Creazione dello zip NON riuscita.
) else (
  explorer out
)
pause
