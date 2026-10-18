# Windows counterpart of smoke-test.sh: starts the packaged launcher and checks the app came up on
# the bundled runtime with the SRD system and the embedded database.
#   pwsh packaging/smoke-test.ps1 dist/win-x64/app
param([Parameter(Mandatory = $true)][string]$App)
$ErrorActionPreference = 'Stop'
$log = Join-Path ([IO.Path]::GetTempPath()) 'codex-smoke.log'
$data = Join-Path ([IO.Path]::GetTempPath()) ('codex-smoke-' + [Guid]::NewGuid())
$env:CODEX_NO_BROWSER = '1'
$env:CODEX_URL = 'http://127.0.0.1:5299'
$env:Codex__DataDirectory = $data
$process = Start-Process -FilePath (Join-Path (Resolve-Path $App) 'TTRPG.Codex.cmd') -RedirectStandardOutput $log -NoNewWindow -PassThru

try {
    $up = $false
    for ($i = 1; $i -le 90; $i++) {
        try {
            Invoke-WebRequest -Uri 'http://127.0.0.1:5299/login' -UseBasicParsing -TimeoutSec 5 | Out-Null
            Write-Host "Server responded to /login after ${i}s"
            $up = $true
            break
        } catch { Start-Sleep -Seconds 1 }
        if ($process.HasExited) { break }
    }
    if (-not $up) { Get-Content $log; throw 'Server never responded to /login' }

    Start-Sleep -Seconds 5
    $output = Get-Content $log -Raw
    $problems = @()
    if ($output -notmatch 'Loaded data-driven system srd5e') { $problems += 'The SRD system was not loaded' }
    # A licence that doesn't cover backups is fine (the database answered); anything else is not.
    if ($output -match 'Scheduled backup configuration failed' -and $output -notmatch 'LicenseLimitException') { $problems += 'The embedded database did not start' }
    try { Invoke-WebRequest -Uri 'http://127.0.0.1:5299/app.css' -UseBasicParsing | Out-Null } catch { $problems += 'Static files are not served' }
    if ($problems.Count -gt 0) { Write-Host $output; throw ($problems -join '; ') }
    Write-Host 'Smoke test passed'
} finally {
    & taskkill /T /F /PID $process.Id 2>$null | Out-Null
}
