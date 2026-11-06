@echo off
rem Avvia il prototipo web sul PC: dipendenze, compilazione e server locale con apertura del browser.
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\web-deps.ps1
if errorlevel 1 goto :errore
dotnet publish src\TrafficSpeedAnalyzer.Web -c Release -o publish\web -nologo
if errorlevel 1 goto :errore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\web-serve.ps1 -Root publish\web\wwwroot -Port 5080 -Open
goto :eof
:errore
echo.
echo Operazione non riuscita: vedi i messaggi sopra.
pause
