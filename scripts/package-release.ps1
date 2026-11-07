# Prepara la versione da distribuire di SpeedVision: una cartella da estrarre e avviare, senza installare nulla.
#  - SpeedVision.exe autonomo (contiene già .NET);
#  - FFmpeg nella sottocartella "ffmpeg", che il programma usa per primo;
#  - licenze; tutto compresso in SpeedVision-v<versione>-win-x64.zip.
# Usato dal workflow di GitHub per le release; in locale:  scripts\package-release.ps1 -Version 0.2.0
param(
    [string]$Version = '0.0.0',
    [string]$OutDir = 'out'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest molto più veloce senza barra di avanzamento
Set-Location (Join-Path $PSScriptRoot '..')

# FFmpeg di Gyan Doshi (build «essentials», GPLv3: include vidstab per la stabilizzazione).
# Per aggiornarlo: cambia versione e SHA-256, presi da https://github.com/GyanD/codexffmpeg/releases
$FfmpegVersion = '9.0.2'
$FfmpegSha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegVersion/ffmpeg-$FfmpegVersion-essentials_build.zip"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$app = Join-Path $OutDir 'SpeedVision'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }

Write-Host "Compilazione di SpeedVision $Version…"
dotnet publish src/TrafficSpeedAnalyzer.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $app
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# File per sviluppatori che non servono a chi usa il programma
Get-ChildItem $app -File | Where-Object { $_.Extension -in '.pdb', '.xml', '.lib' } | Remove-Item

# FFmpeg: scaricato una volta (resta in $OutDir) e verificato con SHA-256.
$ffZip = Join-Path $OutDir "ffmpeg-$FfmpegVersion-essentials_build.zip"
if (-not (Test-Path $ffZip)) {
    Write-Host "Download di FFmpeg $FfmpegVersion…"
    Invoke-WebRequest -Uri $FfmpegUrl -OutFile $ffZip
}
$hash = (Get-FileHash $ffZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $FfmpegSha256) {
    Remove-Item $ffZip
    throw "FFmpeg scaricato non valido (SHA-256 $hash, atteso $FfmpegSha256)."
}

$ffTmp = Join-Path $OutDir 'ffmpeg-estratto'
if (Test-Path $ffTmp) { Remove-Item $ffTmp -Recurse -Force }
Expand-Archive -Path $ffZip -DestinationPath $ffTmp
$ffRoot = (Get-ChildItem $ffTmp -Directory | Select-Object -First 1).FullName
$ffDest = Join-Path $app 'ffmpeg'
New-Item -ItemType Directory -Force -Path $ffDest | Out-Null
$ffBin = Join-Path $ffRoot 'bin'
Copy-Item (Join-Path $ffBin 'ffmpeg.exe'), (Join-Path $ffBin 'ffprobe.exe') $ffDest
Copy-Item (Join-Path $ffRoot 'LICENSE') (Join-Path $ffDest 'LICENSE.txt')
Copy-Item (Join-Path $ffRoot 'README.txt') (Join-Path $ffDest 'README.txt')
Set-Content -Path (Join-Path $ffDest 'ORIGINE.txt') -Encoding UTF8 -Value @"
FFmpeg $FfmpegVersion, build «essentials» di Gyan Doshi, distribuito con licenza GPLv3 (vedi LICENSE.txt).
Eseguibili originali, non modificati: $FfmpegUrl
SHA-256 dell'archivio: $FfmpegSha256
Codice sorgente di FFmpeg: https://github.com/FFmpeg/FFmpeg
Configurazione della build e librerie incluse: README.txt e https://www.gyan.dev/ffmpeg/builds/
SpeedVision usa ffmpeg.exe e ffprobe.exe come programmi separati.
"@
Remove-Item $ffTmp -Recurse -Force

Copy-Item LICENSE, THIRD_PARTY_NOTICES.md $app
Set-Content -Path (Join-Path $app 'LEGGIMI.txt') -Encoding UTF8 -Value @"
SpeedVision $Version
====================

Avvio: estrai lo ZIP in una cartella (clic destro, «Estrai tutto»), per esempio Documenti\SpeedVision,
e apri SpeedVision.exe. Non serve installare nulla: .NET e FFmpeg (cartella "ffmpeg") sono già inclusi.

Se Windows mostra «Windows ha protetto il PC», premi «Ulteriori informazioni» e poi «Esegui comunque».

Al primo avvio dell'analisi il programma scarica una sola volta il modello di riconoscimento
dei veicoli (circa 34 MB, da GitHub). I video restano sempre sul PC.

La calibrazione "sulla mappa" usa internet per la mappa e Microsoft Edge WebView2 Runtime
(già presente in Windows 11; per Windows 10: https://go.microsoft.com/fwlink/p/?LinkId=2124703).

veicoli_riferimento.csv: elenco delle auto per la calibrazione con un'auto riconosciuta.
Si può modificare con Excel (separatore ";").

Guida: https://github.com/bugeo/SpeedVision/wiki
Requisiti: Windows 10 o 11 a 64 bit.
Misure non omologate, non utilizzabili per sanzioni. Le immagini esportate possono contenere
targhe e persone: sono dati personali (GDPR).
"@

$zip = Join-Path $OutDir "SpeedVision-v$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Write-Host 'Compressione…'
Compress-Archive -Path $app -DestinationPath $zip
Write-Host "Pronto: $zip" -ForegroundColor Green
