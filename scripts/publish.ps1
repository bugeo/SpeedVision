# Crea il pacchetto di distribuzione: cartella publish\TrafficSpeedAnalyzer e lo ZIP da consegnare.
# Il programma è autonomo (non serve .NET sul PC di destinazione) e FFmpeg viene incluso se è installato
# su questo PC. Uso, dalla cartella del progetto:
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -FfmpegDir C:\percorso\ffmpeg\bin
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -NoFfmpeg
param(
    # Cartella con ffmpeg.exe e ffprobe.exe; se omessa si cercano nel PATH e nelle installazioni winget.
    [string]$FfmpegDir,
    # Pacchetto senza FFmpeg (chi lo riceve lo installa con winget).
    [switch]$NoFfmpeg
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$root = Get-Location

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '0.0.0' }

$publishRoot = Join-Path $root 'publish'
$out = Join-Path $publishRoot 'TrafficSpeedAnalyzer'
$zip = Join-Path $publishRoot "TrafficSpeedAnalyzer-$version-win-x64.zip"

# Cartella pulita: niente file rimasti da pubblicazioni precedenti.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Compilazione della versione $version..." -ForegroundColor Cyan
dotnet publish src/TrafficSpeedAnalyzer.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# File che non servono per l'uso (simboli di debug, librerie di collegamento, documentazione XML).
Get-ChildItem $out -File | Where-Object { $_.Extension -in '.pdb', '.lib', '.xml' } | Remove-Item -Force

# ───────────── FFmpeg ─────────────

function Resolve-Exe([string]$path) {
    # I collegamenti di winget (WinGet\Links) puntano all'eseguibile vero: si copia quello.
    # LinkTarget in PowerShell 7, Target in Windows PowerShell 5.1.
    $item = Get-Item $path
    $target = if ($item.PSObject.Properties['LinkTarget']) { $item.LinkTarget } else { $item.Target }
    $target = @($target | Where-Object { $_ })[0]
    if ($target) {
        if (-not [System.IO.Path]::IsPathRooted($target)) { $target = Join-Path $item.DirectoryName $target }
        return (Resolve-Path $target).Path
    }
    return $item.FullName
}

function Find-Ffmpeg {
    $dirs = @()
    if ($FfmpegDir) { $dirs += $FfmpegDir; $dirs += (Join-Path $FfmpegDir 'bin') }
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { $dirs += (Split-Path (Resolve-Exe $cmd.Source)) }
    $winget = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages' } else { $null }
    if ($winget -and (Test-Path $winget)) {
        $dirs += Get-ChildItem $winget -Directory -Filter 'Gyan.FFmpeg*' |
            ForEach-Object { Get-ChildItem $_.FullName -Directory -Recurse -Filter 'bin' } |
            ForEach-Object { $_.FullName }
    }
    foreach ($d in $dirs) {
        if ($d -and (Test-Path (Join-Path $d 'ffmpeg.exe')) -and (Test-Path (Join-Path $d 'ffprobe.exe'))) { return $d }
    }
    return $null
}

$ffmpegOut = Join-Path $out 'ffmpeg'
$ffmpegIncluded = $false
if (-not $NoFfmpeg) {
    $src = Find-Ffmpeg
    if ($src) {
        New-Item -ItemType Directory -Force -Path $ffmpegOut | Out-Null
        foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
            Copy-Item (Resolve-Exe (Join-Path $src $exe)) (Join-Path $ffmpegOut $exe) -Force
        }
        # Licenza di FFmpeg (GPL/LGPL): va consegnata insieme ai suoi eseguibili.
        $license = @('LICENSE', 'LICENSE.txt', 'COPYING.GPLv3', 'COPYING.LGPLv2.1') |
            ForEach-Object { Join-Path (Split-Path $src) $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($license) { Copy-Item $license (Join-Path $ffmpegOut 'LICENSE-ffmpeg.txt') -Force }
        else { Write-Warning "Licenza di FFmpeg non trovata accanto a ${src}: aggiungila a mano in ${ffmpegOut}." }
        $ffmpegIncluded = $true
        Write-Host "FFmpeg incluso da $src" -ForegroundColor Green
    }
    else {
        Write-Warning 'FFmpeg non trovato su questo PC: il pacchetto non lo contiene. Installalo con «winget install --id Gyan.FFmpeg -e» e ripeti, oppure indica la cartella con -FfmpegDir.'
    }
}

# ───────────── Istruzioni per chi riceve il pacchetto ─────────────

$ffmpegNote = if ($ffmpegIncluded) {
    'FFmpeg è incluso (cartella "ffmpeg"): non serve installare nulla.'
} else {
    'FFmpeg non è incluso: installalo una volta con «winget install --id Gyan.FFmpeg -e»' + [Environment]::NewLine +
    'oppure copia ffmpeg.exe e ffprobe.exe nella cartella "ffmpeg" accanto al programma.'
}

@"
Traffic Speed Analyzer $version
==============================

Avvio: estrai lo ZIP in una cartella (per esempio Documenti\TrafficSpeedAnalyzer) e apri
TrafficSpeedAnalyzer.exe. Non serve installare .NET.

$ffmpegNote

Al primo avvio dell'analisi il programma scarica una sola volta il modello di riconoscimento
dei veicoli (circa 34 MB, da GitHub). I video restano sempre sul PC.

La calibrazione "sulla mappa" usa internet per la mappa e Microsoft Edge WebView2 Runtime
(già presente in Windows 11; per Windows 10: https://go.microsoft.com/fwlink/p/?LinkId=2124703).

veicoli_riferimento.csv: elenco delle auto per la calibrazione con un'auto riconosciuta.
Si può modificare con Excel (separatore ";").

Requisiti: Windows 10 o 11 a 64 bit.
Misure non omologate, non utilizzabili per sanzioni. Le immagini esportate possono contenere
targhe e persone: sono dati personali (GDPR).
"@ | Set-Content -Path (Join-Path $out 'LEGGIMI.txt') -Encoding UTF8

# ───────────── ZIP ─────────────

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal

$sizeMb = '{0:N1}' -f ((Get-Item $zip).Length / 1MB)
Write-Host ''
Write-Host "Cartella: $out" -ForegroundColor Green
Write-Host "Pacchetto da distribuire: $zip ($sizeMb MB)" -ForegroundColor Green
