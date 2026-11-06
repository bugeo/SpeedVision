# Prepara le dipendenze del prototipo web (non fanno parte dei sorgenti):
#  - ONNX Runtime Web (licenza MIT) in src\TrafficSpeedAnalyzer.Web\wwwroot\lib\ort
#  - modello YOLOX-S (Apache-2.0) in src\TrafficSpeedAnalyzer.Web\wwwroot\models,
#    copiato da quello già scaricato dall'app desktop oppure scaricato e verificato con SHA-256.
param(
    [string]$OrtVersion = '1.22.0',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Set-Location (Join-Path $PSScriptRoot '..')

$web = 'src\TrafficSpeedAnalyzer.Web\wwwroot'
$ortDir = Join-Path $web 'lib\ort'
$modelsDir = Join-Path $web 'models'
$tmp = Join-Path $env:TEMP 'tsa-web-deps'
New-Item -ItemType Directory -Force -Path $tmp, $ortDir, $modelsDir | Out-Null

# ---------------------------------------------------------------- ONNX Runtime Web
$versionFile = Join-Path $ortDir 'VERSION.txt'
$current = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '' }
if ($Force -or $current -ne $OrtVersion) {
    Write-Host "== ONNX Runtime Web $OrtVersion ==" -ForegroundColor Cyan
    $tgz = Join-Path $tmp "onnxruntime-web-$OrtVersion.tgz"
    Invoke-WebRequest "https://registry.npmjs.org/onnxruntime-web/-/onnxruntime-web-$OrtVersion.tgz" -OutFile $tgz -UseBasicParsing
    $extract = Join-Path $tmp "ort-$OrtVersion"
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    New-Item -ItemType Directory -Path $extract | Out-Null
    tar -xzf $tgz -C $extract
    if ($LASTEXITCODE -ne 0) { throw 'Estrazione di ONNX Runtime Web non riuscita (serve tar.exe, incluso in Windows 10/11).' }
    Get-ChildItem $ortDir -File | Remove-Item -Force
    $dist = Join-Path $extract 'package\dist'
    Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.mjs', '.wasm' } | Copy-Item -Destination $ortDir
    Copy-Item (Join-Path $extract 'package\LICENSE') (Join-Path $ortDir 'LICENSE.txt') -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $ortDir 'ort.webgpu.min.mjs'))) { throw "ort.webgpu.min.mjs non trovato nella versione $OrtVersion." }
    Set-Content $versionFile $OrtVersion
    Write-Host "Copiato in $ortDir" -ForegroundColor Green
} else {
    Write-Host "ONNX Runtime Web $OrtVersion già presente." -ForegroundColor Green
}

# ---------------------------------------------------------------- modello YOLOX-S
$model = 'yolox_s.onnx'
$sha = 'c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063'
$target = Join-Path $modelsDir $model

function Test-Model([string]$path) {
    (Test-Path $path) -and ((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sha)
}

if (Test-Model $target) {
    Write-Host "Modello $model già presente." -ForegroundColor Green
} else {
    $desktop = Join-Path $env:LOCALAPPDATA "TrafficSpeedAnalyzer\models\$model"
    if (Test-Model $desktop) {
        Copy-Item $desktop $target -Force
        Write-Host "Modello copiato dall'app desktop ($desktop)." -ForegroundColor Green
    } else {
        Write-Host "== Download di $model (35 MB) ==" -ForegroundColor Cyan
        $part = "$target.part"
        Invoke-WebRequest "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/$model" -OutFile $part -UseBasicParsing
        if (-not (Test-Model $part)) { Remove-Item $part -Force; throw 'Il file scaricato non corrisponde al modello atteso (SHA-256).' }
        Move-Item $part $target -Force
        Write-Host 'Modello scaricato e verificato.' -ForegroundColor Green
    }
}
