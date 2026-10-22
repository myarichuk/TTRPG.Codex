# Builds a Windows release of TTRPG.Codex: the app, the content packs, a bundled .NET runtime and a
# launcher, as a .zip plus an Inno Setup installer (when iscc is available).
#
#   pwsh packaging/package.ps1 -Version 1.4.0 [-Rid win-x64] [-Out dist]
#
# See package.sh for why the runtime is bundled rather than the app being trimmed or AOT-compiled.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Rid = 'win-x64',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $Out) { $Out = Join-Path $repo 'dist' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = Resolve-Path $Out
$stage = Join-Path $Out $Rid
$app = Join-Path $stage 'app'
$name = "TTRPG.Codex-$Version-$Rid"
$arch = switch ($Rid) { 'win-x64' { 'x64' } 'win-arm64' { 'arm64' } default { throw "Unsupported runtime identifier: $Rid" } }

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $app | Out-Null

Write-Host "==> Publishing the app for $Rid ($Version)"
dotnet publish (Join-Path $repo 'src/Codex.Web/Codex.Web.csproj') -c Release -r $Rid --self-contained false `
    -p:UseAppHost=false -p:PublishReadyToRun=true -p:Version=$Version -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The tray launcher goes in its own folder so its dependencies never mix with the server's. Its
# apphost looks for the bundled runtime one level up (..\runtime), so it needs no installed .NET.
Write-Host "==> Publishing the tray launcher for $Rid"
dotnet publish (Join-Path $repo 'src/Codex.Launcher/Codex.Launcher.csproj') -c Release -r $Rid --self-contained false `
    -p:UseAppHost=true -p:AppHostDotNetSearch=AppRelative '-p:AppHostRelativeDotNet=../runtime' `
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$Version -o (Join-Path $app 'launcher')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (launcher) failed' }

# The embedded RavenDB server ships native libraries for every platform (over 400 MB); it only
# loads its own platform's, so keep that one plus the generic "win" folder.
Write-Host "==> Trimming RavenDB native libraries to $Rid"
Get-ChildItem (Join-Path $app 'RavenDBServer/runtimes') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notin @($Rid, 'win') } |
    Remove-Item -Recurse -Force

Write-Host '==> Adding content packs and the launcher'
Copy-Item -Recurse (Join-Path $repo 'content') (Join-Path $app 'content')
Copy-Item (Join-Path $repo 'packaging/launchers/TTRPG.Codex.cmd') $app
Copy-Item (Join-Path $repo 'LICENSE'), (Join-Path $repo 'LEGAL.md') $app
# The bundled RavenDB server is AGPLv3: its licence text travels with the binaries (see LEGAL.md).
Copy-Item (Join-Path $repo 'licenses/AGPL-3.0.txt') $app

Write-Host '==> Bundling the .NET and ASP.NET Core runtimes'
$installer = Join-Path $stage 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
foreach ($runtime in 'dotnet', 'aspnetcore') {
    & $installer -Channel 10.0 -Runtime $runtime -Architecture $arch -InstallDir (Join-Path $app 'runtime') -NoPath
}
Remove-Item $installer

Write-Host '==> Archiving'
$zip = Join-Path $Out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $candidate = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    if (Test-Path $candidate) { $iscc = $candidate }
}
if ($iscc) {
    Write-Host '==> Building the installer'
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/DOutputDir=$Out" (Join-Path $repo 'packaging/windows/TTRPG.Codex.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
} else {
    Write-Warning 'Inno Setup (iscc) not found: skipping the installer. Install it from https://jrsoftware.org/isinfo.php.'
}

Write-Host "==> Done: $Out"
