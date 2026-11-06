@echo off
rem Crea il repository git del progetto, fa il primo commit e lo pubblica su GitHub.
rem L'esito viene scritto in git-log.txt nella cartella del progetto.
cd /d "%~dp0.."
set LOG=git-log.txt
set REMOTE=https://github.com/bugeo/SpeedVision.git
echo ==== %DATE% %TIME% ==== > %LOG%
where git >> %LOG% 2>&1
if errorlevel 1 (
  echo Git non e' installato. Installalo con:  winget install --id Git.Git -e
  echo poi chiudi e riapri la finestra e rilancia questo script.
  echo Git non installato. >> %LOG%
  pause
  exit /b 1
)
git --version >> %LOG% 2>&1

rem File non piu' usati dal programma
del /q "src\TrafficSpeedAnalyzer.Core\Localization\strings.*.json" 2>nul
del /q "src\TrafficSpeedAnalyzer.Core\Calibration\ReferenceVehicles.json" 2>nul
del /q "src\TrafficSpeedAnalyzer.Video\MotionTracker.cs" 2>nul

if not exist .git git init -b main >> %LOG% 2>&1
rem Autore dei commit: nome e indirizzo privato di GitHub (l'email vera resta nascosta).
git config user.name "Marco Staffoli"
git config user.email "bugeo@users.noreply.github.com"
git add -A >> %LOG% 2>&1
echo ---- FILE NEL COMMIT ---- >> %LOG%
git status --short >> %LOG% 2>&1
git rev-parse --verify HEAD >nul 2>&1
if errorlevel 1 git commit -q -m "SpeedVision: primo commit" >> %LOG% 2>&1
echo COMMIT_EXIT=%ERRORLEVEL% >> %LOG%
git log --oneline -3 >> %LOG% 2>&1
git count-objects -vH >> %LOG% 2>&1

git remote get-url origin >nul 2>&1 || git remote add origin %REMOTE%
echo ---- PUSH ---- >> %LOG%
echo Pubblicazione su GitHub: se si apre la finestra di accesso, entra con il tuo account GitHub.
git push -u origin main >> %LOG% 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> %LOG%
echo Fatto. Dettagli in %LOG%.
pause
