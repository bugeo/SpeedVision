@echo off
rem Salva tutte le modifiche in un commit e le invia a GitHub.
rem Uso: doppio clic (chiede la descrizione) oppure  scripts\git-push.cmd "descrizione delle modifiche"
rem Il registro viene scritto in una cartella temporanea e alla fine copiato in git-log.txt.
cd /d "%~dp0.."
set LOG=%TEMP%\speedvision-git.txt
if exist .git\rebase-merge goto bloccato
if exist .git\rebase-apply goto bloccato
set MSG=%~1
if "%MSG%"=="" set /p MSG=Descrizione delle modifiche: 
if "%MSG%"=="" set MSG=Aggiornamento
echo ==== %DATE% %TIME% ==== > "%LOG%"
git rm -r -q --cached --ignore-unmatch git-log.txt wiki-log.txt ci-log.txt >> "%LOG%" 2>&1
git add -A >> "%LOG%" 2>&1
echo ---- FILE NEL COMMIT ---- >> "%LOG%"
git status --short >> "%LOG%" 2>&1
git commit -q -m "%MSG%" >> "%LOG%" 2>&1
echo COMMIT_EXIT=%ERRORLEVEL% >> "%LOG%"
echo ---- AGGIORNAMENTO DA GITHUB ---- >> "%LOG%"
rem Unisce le modifiche fatte direttamente su GitHub (es. file in .github) prima di inviare.
git pull --no-rebase --no-edit >> "%LOG%" 2>&1
echo PULL_EXIT=%ERRORLEVEL% >> "%LOG%"
echo ---- PUSH ---- >> "%LOG%"
git push >> "%LOG%" 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> "%LOG%"
git log --oneline -5 >> "%LOG%" 2>&1
copy /y "%LOG%" git-log.txt >nul
findstr /B "PUSH_EXIT=0" git-log.txt >nul && (echo Fatto: modifiche inviate a GitHub.) || (echo Invio NON riuscito: dettagli in git-log.txt.)
pause
exit /b

:bloccato
echo C'e' un aggiornamento di git rimasto a meta': lancia prima scripts\git-sblocca.cmd
pause
