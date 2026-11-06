# Compila la soluzione ed esegue tutti i test.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '== Versione SDK .NET ==' -ForegroundColor Cyan
dotnet --version

Write-Host '== FFmpeg ==' -ForegroundColor Cyan
$ff = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ff) { Write-Host $ff.Source } else { Write-Host 'FFmpeg non trovato nel PATH: i test video verranno saltati.' -ForegroundColor Yellow }

Write-Host '== Compilazione ==' -ForegroundColor Cyan
dotnet build SpeedVision.sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '== Test ==' -ForegroundColor Cyan
dotnet test SpeedVision.sln -c Release --no-build
exit $LASTEXITCODE
