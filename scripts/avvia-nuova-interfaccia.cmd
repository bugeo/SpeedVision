@echo off
rem Compila e avvia la nuova interfaccia di SpeedVision (Avalonia, in sviluppo).
rem L'app per Windows di sempre non cambia: questa e' una seconda finestra, separata.
cd /d "%~dp0.."
echo Compilazione della nuova interfaccia in corso, attendi...
dotnet build src\TrafficSpeedAnalyzer.Desktop -c Release -v quiet -nologo > avvia-log.txt 2>&1
if errorlevel 1 (
  echo Compilazione non riuscita: dettagli in avvia-log.txt
  pause
  exit /b 1
)
start "" "src\TrafficSpeedAnalyzer.Desktop\bin\Release\net10.0\SpeedVision.exe"
