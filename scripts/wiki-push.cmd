@echo off
rem Pubblica le pagine della cartella "wiki" nel wiki del progetto su GitHub.
rem Prima volta: su GitHub apri la scheda Wiki e crea la prima pagina, con un testo qualsiasi.
rem L'esito viene scritto in wiki-log.txt nella cartella del progetto.
cd /d "%~dp0.."
set LOG=%CD%\wiki-log.txt
set SRC=%CD%\wiki
set WIKI=%TEMP%\SpeedVision.wiki
echo ==== %DATE% %TIME% ==== > "%LOG%"
if exist "%WIKI%" rmdir /s /q "%WIKI%"
git clone https://github.com/bugeo/SpeedVision.wiki.git "%WIKI%" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo Il wiki su GitHub non esiste ancora: apri la scheda Wiki del repository,
  echo crea la prima pagina con un testo qualsiasi e rilancia questo script.
  echo CLONE non riuscito. >> "%LOG%"
  pause
  exit /b 1
)
rem Copia le pagine; quelle tolte dalla cartella "wiki" vengono tolte anche dal wiki.
robocopy "%SRC%" "%WIKI%" *.md /MIR /XD .git >> "%LOG%" 2>&1
cd /d "%WIKI%"
git config user.name "Marco Staffoli"
git config user.email "bugeo@users.noreply.github.com"
git add -A >> "%LOG%" 2>&1
echo ---- PAGINE MODIFICATE ---- >> "%LOG%"
git status --short >> "%LOG%" 2>&1
git commit -q -m "Aggiornamento del wiki" >> "%LOG%" 2>&1
echo COMMIT_EXIT=%ERRORLEVEL% >> "%LOG%"
git push >> "%LOG%" 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> "%LOG%"
echo Fatto: https://github.com/bugeo/SpeedVision/wiki
pause
