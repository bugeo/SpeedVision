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
rem I file di registro non vanno nel repository (sono in .gitignore).
git rm -r -q --cached --ignore-unmatch git-log.txt wiki-log.txt ci-log.txt >> %LOG% 2>&1
git add -A >> %LOG% 2>&1
echo ---- FILE NEL COMMIT ---- >> %LOG%
git status --short >> %LOG% 2>&1
git commit -q -m "%MSG%" >> %LOG% 2>&1
echo COMMIT_EXIT=%ERRORLEVEL% >> %LOG%
echo ---- AGGIORNAMENTO DA GITHUB ---- >> %LOG%
rem Prende le modifiche fatte direttamente su GitHub (es. file in .github) prima di inviare.
git pull --rebase --autostash >> %LOG% 2>&1
echo PULL_EXIT=%ERRORLEVEL% >> %LOG%
echo ---- PUSH ---- >> %LOG%
git push >> %LOG% 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> %LOG%
git log --oneline -5 >> %LOG% 2>&1
findstr /B "PUSH_EXIT=0" %LOG% >nul && (echo Fatto: modifiche inviate a GitHub.) || (echo Invio NON riuscito: dettagli in %LOG%.)
pause
