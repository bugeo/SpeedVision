@echo off
rem Aggiorna il workflow di GitHub: anche i test dell'interfaccia Avalonia su Mac (fase 3). Da lanciare una volta.
cd /d "%~dp0.."
copy /y "scripts\build.yml.nuovo" ".github\workflows\build.yml" >nul || (echo Copia non riuscita & pause & exit /b 1)
del "scripts\build.yml.nuovo"
echo Workflow aggiornato: .github\workflows\build.yml
pause
(goto) 2>nul & del "%~f0"
