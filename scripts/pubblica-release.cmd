@echo off
rem Pubblica una nuova versione di SpeedVision su GitHub (pagina Releases).
rem Crea l'etichetta di versione (es. v0.2.0) sull'ultimo commit inviato e la invia a GitHub: la compilazione
rem online prepara lo zip per Windows e il .dmg per Mac e li allega alla release (circa 20-30 minuti).
rem Uso: doppio clic (chiede la versione) oppure  scripts\pubblica-release.cmd 0.2.0
rem Il registro viene scritto in release-log.txt.
setlocal
cd /d "%~dp0.."
set LOG=%TEMP%\speedvision-release.txt
set VER=%~1
if "%VER%"=="" set /p VER=Numero di versione (es. 0.2.0): 
if "%VER%"=="" goto nessuna
if /i "%VER:~0,1%"=="v" set VER=%VER:~1%
echo %VER%| findstr /R /X "[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" >nul || goto formato
set TAG=v%VER%

echo ==== %DATE% %TIME% ==== > "%LOG%"
echo ---- CONTROLLO MODIFICHE NON INVIATE ---- >> "%LOG%"
git status --porcelain --untracked-files=no > "%TEMP%\speedvision-status.txt" 2>> "%LOG%"
for %%A in ("%TEMP%\speedvision-status.txt") do if %%~zA GTR 0 goto modifiche
git fetch --tags origin >> "%LOG%" 2>&1
git rev-parse -q --verify "refs/tags/%TAG%" >nul 2>&1 && goto esiste
git pull --no-rebase --no-edit >> "%LOG%" 2>&1
echo PULL_EXIT=%ERRORLEVEL% >> "%LOG%"
git push >> "%LOG%" 2>&1
echo ---- ETICHETTA %TAG% ---- >> "%LOG%"
git tag -a %TAG% -m "SpeedVision %VER%" >> "%LOG%" 2>&1
echo TAG_EXIT=%ERRORLEVEL% >> "%LOG%"
git push origin %TAG% >> "%LOG%" 2>&1
echo PUSH_EXIT=%ERRORLEVEL% >> "%LOG%"
git log --oneline -3 >> "%LOG%" 2>&1
copy /y "%LOG%" release-log.txt >nul
findstr /B "PUSH_EXIT=0" release-log.txt >nul || goto fallito
echo.
echo Fatto: etichetta %TAG% inviata a GitHub.
echo Tra circa 20-30 minuti nella pagina Releases ci saranno:
echo   SpeedVision-v%VER%-win-x64.zip     (Windows)
echo   SpeedVision-v%VER%-mac-arm64.dmg   (Mac con chip Apple)
echo Avanzamento: https://github.com/bugeo/SpeedVision/actions
start "" "https://github.com/bugeo/SpeedVision/actions"
pause
exit /b 0

:nessuna
echo Nessuna versione indicata: niente da fare.
pause
exit /b 1
:formato
echo La versione deve essere di tre numeri separati da punti, per esempio 0.2.0
pause
exit /b 1
:modifiche
copy /y "%LOG%" release-log.txt >nul
echo Ci sono modifiche non ancora inviate a GitHub. Lancia prima scripts\git-push.cmd,
echo aspetta che la compilazione online sia verde, poi riprova.
type "%TEMP%\speedvision-status.txt"
pause
exit /b 1
:esiste
echo La versione %TAG% esiste gia'. Scegli un numero piu' alto.
pause
exit /b 1
:fallito
echo Invio NON riuscito: dettagli in release-log.txt.
pause
exit /b 1
