@echo off
rem Compila ed esegue i test, scrivendo tutto in ci-log.txt nella cartella del progetto.
cd /d "%~dp0.."
set LOG=ci-log.txt
echo ==== %DATE% %TIME% ==== > %LOG%
echo ---- SDK ---- >> %LOG%
dotnet --list-sdks >> %LOG% 2>&1
echo ---- FFMPEG ---- >> %LOG%
where ffmpeg >> %LOG% 2>&1
where ffprobe >> %LOG% 2>&1
echo ---- BUILD ---- >> %LOG%
dotnet build TrafficSpeedAnalyzer.sln -c Release --no-incremental -nologo -clp:ErrorsOnly >> %LOG% 2>&1
echo BUILD_EXIT=%ERRORLEVEL% >> %LOG%

echo ==== FINE ==== >> %LOG%
