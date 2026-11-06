@echo off
rem Compila ed esegue i test, scrivendo tutto in ci-log.txt nella cartella del progetto.
cd /d "%~dp0.."
rem Se SpeedVision e' aperto i file in bin sono bloccati: la build fallisce a meta' e il programma non parte piu'.
tasklist /FI "IMAGENAME eq SpeedVision.exe" 2>nul | find /I "SpeedVision.exe" >nul
if not errorlevel 1 (
  echo SpeedVision e' ancora aperto: chiudilo e rilancia ci.cmd.
  echo SpeedVision aperto: build non eseguita. > ci-log.txt
  pause
  exit /b 1
)
set LOG=ci-log.txt
echo ==== %DATE% %TIME% ==== > %LOG%
echo ---- SDK ---- >> %LOG%
dotnet --list-sdks >> %LOG% 2>&1
echo ---- FFMPEG ---- >> %LOG%
where ffmpeg >> %LOG% 2>&1
where ffprobe >> %LOG% 2>&1
echo ---- BUILD ---- >> %LOG%
dotnet build SpeedVision.sln -c Release --no-incremental -nologo -clp:ErrorsOnly >> %LOG% 2>&1
echo BUILD_EXIT=%ERRORLEVEL% >> %LOG%
echo ---- TEST ---- >> %LOG%
dotnet test SpeedVision.sln -c Release --no-build -nologo --blame-hang-timeout 8m >> %LOG% 2>&1
echo TEST_EXIT=%ERRORLEVEL% >> %LOG%
echo ---- TEST JS ---- >> %LOG%
where node >nul 2>&1
if errorlevel 1 (
  echo Node.js non trovato: test JavaScript saltati. >> %LOG%
) else (
  node --test tests\TrafficSpeedAnalyzer.Web.JsTests\mp4demux.test.mjs tests\TrafficSpeedAnalyzer.Web.JsTests\yolox.test.mjs >> %LOG% 2>&1
  call echo TEST_JS_EXIT=%%ERRORLEVEL%% >> %LOG%
)
echo ==== FINE ==== >> %LOG%
