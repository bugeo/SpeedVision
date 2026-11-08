@echo off
rem Fase 1 del piano per Mac: i view model e i servizi comuni sono passati nel progetto
rem src\TrafficSpeedAnalyzer.Presentation. Questo script toglie le vecchie copie dall'app WPF,
rem altrimenti la compilazione trova ogni classe due volte. Va lanciato una volta sola.
cd /d "%~dp0.."
set APP=src\TrafficSpeedAnalyzer.App
if not exist "src\TrafficSpeedAnalyzer.Presentation\ViewModels\MainViewModel.cs" (
  echo Manca src\TrafficSpeedAnalyzer.Presentation: non tolgo nulla.
  pause
  exit /b 1
)
for %%F in (
  "%APP%\ViewModels"
  "%APP%\AppInfo.cs"
  "%APP%\Services\AppLog.cs"
  "%APP%\Services\AppSettings.cs"
  "%APP%\Services\CalibrationFile.cs"
  "%APP%\Services\PassageImageRenderer.cs"
  "%APP%\Services\PdfReport.cs"
) do (
  git rm -r -q --ignore-unmatch %%F >nul 2>&1
  if exist "%%~F\" rmdir /s /q "%%~F"
  if exist "%%~F" del /q "%%~F"
)
echo Fatto: vecchie copie tolte. Ora lancia scripts\ci.cmd.
pause
