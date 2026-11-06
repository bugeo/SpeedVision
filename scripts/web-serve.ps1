# Server locale minimo per il prototipo web (solo su questo PC: http://localhost).
# Invia le intestazioni COOP/COEP che abilitano più thread per il modello su CPU, come farà
# il sito pubblicato (vedi wwwroot\_headers). Ctrl+C per fermarlo.
param(
    [string]$Root = 'publish\web\wwwroot',
    [int]$Port = 5080,
    [switch]$Open
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$rootFull = (Resolve-Path $Root).Path.TrimEnd('\')

$types = @{
    '.html' = 'text/html; charset=utf-8'; '.js' = 'text/javascript'; '.mjs' = 'text/javascript'
    '.css' = 'text/css'; '.json' = 'application/json'; '.wasm' = 'application/wasm'
    '.png' = 'image/png'; '.ico' = 'image/x-icon'; '.svg' = 'image/svg+xml'; '.mp4' = 'video/mp4'
    '.dll' = 'application/octet-stream'; '.dat' = 'application/octet-stream'; '.onnx' = 'application/octet-stream'
    '.pdb' = 'application/octet-stream'; '.webcil' = 'application/octet-stream'; '.txt' = 'text/plain; charset=utf-8'
}

$listener = New-Object System.Net.HttpListener
$prefix = "http://localhost:$Port/"
$listener.Prefixes.Add($prefix)
$listener.Start()
Write-Host "Traffic Speed Analyzer (web) su $prefix  —  cartella $rootFull" -ForegroundColor Green
Write-Host 'Usa Chrome o Edge. Ctrl+C per fermare.'
if ($Open) { Start-Process $prefix }

try {
    while ($listener.IsListening) {
        $task = $listener.GetContextAsync()
        while (-not $task.AsyncWaitHandle.WaitOne(250)) { }
        $ctx = $task.Result
        $res = $ctx.Response
        try {
            $path = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath)
            if ($path.EndsWith('/')) { $path += 'index.html' }
            $file = [IO.Path]::GetFullPath((Join-Path $rootFull ($path.TrimStart('/') -replace '/', '\')))
            if (-not $file.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
                # Percorsi sconosciuti senza estensione: pagina principale (applicazione a pagina singola).
                if ([IO.Path]::GetExtension($path) -eq '') { $file = Join-Path $rootFull 'index.html' } else { $file = $null }
            }
            $res.Headers['Cross-Origin-Opener-Policy'] = 'same-origin'
            $res.Headers['Cross-Origin-Embedder-Policy'] = 'require-corp'
            $res.Headers['Cache-Control'] = 'no-cache'
            if ($null -eq $file) {
                $res.StatusCode = 404
            } else {
                $ext = [IO.Path]::GetExtension($file).ToLowerInvariant()
                $res.ContentType = if ($types.ContainsKey($ext)) { $types[$ext] } else { 'application/octet-stream' }
                $bytes = [IO.File]::ReadAllBytes($file)
                $res.ContentLength64 = $bytes.Length
                $res.OutputStream.Write($bytes, 0, $bytes.Length)
            }
        } catch {
            Write-Host $_.Exception.Message -ForegroundColor Yellow
        } finally {
            $res.Close()
        }
    }
} finally {
    $listener.Stop()
}
