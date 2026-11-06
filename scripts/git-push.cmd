@echo off
rem Salva tutte le modifiche in un commit e le invia a GitHub.
rem Uso: doppio clic (chiede la descrizione) oppure  scripts\git-push.cmd "descrizione delle modifiche"
rem L'esito viene scritto in git-log.txt nella cartella del progetto.
cd /d "%~dp0.."
set LOG=git-log.txt
set MSG=%~1
if "%MSG%"=="" set /p MSG=Descrizione delle modifiche: 
if "%MSG%"=="" set MSG=Aggiornamento
echo ==== %DATE% %TIME% ==== > %LOG%
git add -A >> %LOG% 2>&1
echo ---- FILE NEL COMMIT ---- >> %LOG%
git status --short >> %LOG% 2>&1
git commit -q -m "%MSG%" >> %LOG% 2>&1
echo COMMIT_EXIT=%ERRORLEVEL% >> %LOG%
echo ---- AGGIORNAMENTO DA GITHUB ---- >> %LOG%
git pull --rebase >> %LOG% 2>&1
echo PULL_EXIT=%ERRORLEVEL% >> %LOG%
echo ---- PUSH ---- >> %LOG%
git push >> %LOG% 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> %LOG%
git log --oneline -3 >> %LOG% 2>&1
echo Fatto. Dettagli in %LOG%.
pause
