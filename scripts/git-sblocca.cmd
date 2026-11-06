@echo off
rem Sblocca il repository dopo un aggiornamento rimasto a meta' e invia tutto a GitHub.
rem Si copia nella cartella temporanea e lavora da li', perche' git riscrive i file di scripts.
if not "%~1"=="--temp" (
  copy /y "%~f0" "%TEMP%\sv-git-sblocca.cmd" >nul
  "%TEMP%\sv-git-sblocca.cmd" --temp "%~dp0.."
)
cd /d "%~2"
set LOG=%TEMP%\speedvision-git.txt
echo ==== %DATE% %TIME% SBLOCCO ==== > "%LOG%"
rem Copia di sicurezza delle pagine del wiki: annullare l'aggiornamento le toglierebbe dalla cartella.
if exist wiki robocopy wiki "%TEMP%\sv-wiki-copia" /E /NFL /NDL /NJH /NJS >nul
if exist .git\rebase-merge\head-name git rebase --abort >> "%LOG%" 2>&1
if exist .git\rebase-apply git rebase --abort >> "%LOG%" 2>&1
if exist .git\rebase-merge if not exist .git\rebase-merge\head-name rmdir /s /q .git\rebase-merge
if exist "%TEMP%\sv-wiki-copia" robocopy "%TEMP%\sv-wiki-copia" wiki /E /NFL /NDL /NJH /NJS >nul
if exist scripts\git-push.new move /y scripts\git-push.new scripts\git-push.cmd >> "%LOG%" 2>&1
echo ---- STATO ---- >> "%LOG%"
git branch --show-current >> "%LOG%" 2>&1
git log --oneline -6 >> "%LOG%" 2>&1
git rm -r -q --cached --ignore-unmatch git-log.txt wiki-log.txt ci-log.txt >> "%LOG%" 2>&1
git add -A >> "%LOG%" 2>&1
echo ---- FILE NEL COMMIT ---- >> "%LOG%"
git status --short >> "%LOG%" 2>&1
git commit -q -m "Script di invio corretto" >> "%LOG%" 2>&1
echo COMMIT_EXIT=%ERRORLEVEL% >> "%LOG%"
echo ---- AGGIORNAMENTO DA GITHUB ---- >> "%LOG%"
git pull --no-rebase --no-edit >> "%LOG%" 2>&1
echo PULL_EXIT=%ERRORLEVEL% >> "%LOG%"
echo ---- PUSH ---- >> "%LOG%"
git push >> "%LOG%" 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> "%LOG%"
git log --oneline -8 >> "%LOG%" 2>&1
git status --short >> "%LOG%" 2>&1
copy /y "%LOG%" git-log.txt >nul
findstr /B "PUSH_EXIT=0" git-log.txt >nul && (echo Fatto: modifiche inviate a GitHub.) || (echo Invio NON riuscito: dettagli in git-log.txt.)
pause
exit
