@echo off
rem Ripara l'invio bloccato: annulla l'aggiornamento interrotto, toglie i file di registro
rem dal repository e invia tutto a GitHub. Da lanciare una volta.
cd /d "%~dp0.."
set LOG=%TEMP%\speedvision-git.txt
echo ==== %DATE% %TIME% RIPARAZIONE ==== > "%LOG%"
if exist .git\rebase-merge git rebase --abort >> "%LOG%" 2>&1
if exist .git\rebase-apply git rebase --abort >> "%LOG%" 2>&1
echo ---- STATO DOPO L'ANNULLAMENTO ---- >> "%LOG%"
git status --short >> "%LOG%" 2>&1
git stash list >> "%LOG%" 2>&1
findstr /x /c:"git-log.txt" .gitignore >nul || echo git-log.txt>> .gitignore
findstr /x /c:"wiki-log.txt" .gitignore >nul || echo wiki-log.txt>> .gitignore
git rm -q --cached --ignore-unmatch git-log.txt wiki-log.txt >> "%LOG%" 2>&1
git add -A >> "%LOG%" 2>&1
echo ---- FILE NEL COMMIT ---- >> "%LOG%"
git status --short >> "%LOG%" 2>&1
git commit -q -m "File di registro fuori dal repository" >> "%LOG%" 2>&1
echo COMMIT_EXIT=%ERRORLEVEL% >> "%LOG%"
echo ---- AGGIORNAMENTO DA GITHUB ---- >> "%LOG%"
git pull --rebase --autostash >> "%LOG%" 2>&1
echo PULL_EXIT=%ERRORLEVEL% >> "%LOG%"
echo ---- PUSH ---- >> "%LOG%"
git push >> "%LOG%" 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> "%LOG%"
git log --oneline -6 >> "%LOG%" 2>&1
git status --short >> "%LOG%" 2>&1
git stash list >> "%LOG%" 2>&1
copy /y "%LOG%" git-log.txt >nul
findstr /B "PUSH_EXIT=0" git-log.txt >nul && (echo Fatto: modifiche inviate a GitHub.) || (echo Invio NON riuscito: dettagli in git-log.txt.)
pause
