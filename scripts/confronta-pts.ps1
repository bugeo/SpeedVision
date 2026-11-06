# Confronta i timestamp dei fotogrammi letti dal browser (pulsante "Scarica l'elenco dei pts")
# con quelli di FFprobe. Uso:
#   powershell -File scripts\confronta-pts.ps1 C:\video\IMG_1234.MOV C:\Download\IMG_1234_pts-browser.txt
param(
    [Parameter(Mandatory)] [string]$Video,
    [Parameter(Mandatory)] [string]$BrowserPts
)
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture

$ff = & ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 $Video
if ($LASTEXITCODE -ne 0) { throw 'ffprobe non riuscito.' }
$expected = $ff | ForEach-Object { $_.Trim().TrimEnd(',') } | Where-Object { $_ -and $_ -ne 'N/A' } |
    ForEach-Object { [double]::Parse($_, $inv) } | Sort-Object
$actual = Get-Content $BrowserPts | Where-Object { $_.Trim() } | ForEach-Object { [double]::Parse($_.Trim(), $inv) }

Write-Host "FFprobe: $($expected.Count) fotogrammi; browser: $($actual.Count) fotogrammi"
$n = [Math]::Min($expected.Count, $actual.Count)
$max = 0.0; $worst = -1
for ($i = 0; $i -lt $n; $i++) {
    $d = [Math]::Abs($expected[$i] - $actual[$i])
    if ($d -gt $max) { $max = $d; $worst = $i }
}
Write-Host ("Scarto massimo: {0:0.0} µs (fotogramma {1})" -f ($max * 1e6), $worst)
if ($expected.Count -eq $actual.Count -and $max -lt 2e-6) {
    Write-Host 'OK: i timestamp coincidono.' -ForegroundColor Green
    exit 0
}
Write-Host 'DIVERSI: controlla il file (edit list, frame rate variabile).' -ForegroundColor Red
exit 1
