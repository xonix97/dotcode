# dotcode installer - Windows PowerShell
# Usage:  irm https://raw.githubusercontent.com/xonix97/dotcode/main/install.ps1 | iex
$ErrorActionPreference = "Stop"

$Repo       = "xonix97/dotcode"
$InstallDir = if ($env:DOTCODE_HOME) { $env:DOTCODE_HOME } else { Join-Path $env:USERPROFILE ".dotcode" }
$BinDir     = if ($env:DOTCODE_BIN)  { $env:DOTCODE_BIN }  else { Join-Path $env:USERPROFILE ".dotcode\bin" }

function Write-Ok($m)  { Write-Host "  [OK] $m" -ForegroundColor Green }
function Write-Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

Write-Host "dotcode installer (Windows)" -ForegroundColor White

# --- .NET 9 SDK --------------------------------------------------------------
Write-Step "Checking .NET 9 SDK"
$needDotnet = $true
try {
    $v = (& dotnet --version 2>$null)
    if ($v -and [int]($v.Split('.')[0]) -ge 9) { $needDotnet = $false; Write-Ok "dotnet $v found" }
} catch { }
if ($needDotnet) {
    Write-Host "  installing .NET 9 SDK (winget, then chocolatey, then dotnet-install fallback)..."
    $installed = $false
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        try { winget install Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements | Out-Null; $installed = $true } catch { }
    }
    if (-not $installed -and (Get-Command choco -ErrorAction SilentlyContinue)) {
        try { choco install dotnet-sdk -y | Out-Null; $installed = $true } catch { }
    }
    if (-not $installed) {
        $script = Join-Path $env:TEMP "dotnet-install.ps1"
        Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $script
        & $script -Channel 9.0 -InstallDir (Join-Path $env:USERPROFILE ".dotnet") | Out-Null
        $env:PATH = (Join-Path $env:USERPROFILE ".dotnet") + ";" + $env:PATH
        # make PATH permanent for the user
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        if ($userPath -notlike "*$env:USERPROFILE\.dotnet*") {
            [Environment]::SetEnvironmentVariable("Path", (Join-Path $env:USERPROFILE ".dotnet") + ";" + $userPath, "User")
        }
    }
    # refresh PATH in this session
    $env:PATH = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
    Write-Ok ".NET 9 SDK installed"
}

# --- source ------------------------------------------------------------------
Write-Step "Fetching dotcode source"
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
New-Item -ItemType Directory -Force -Path (Split-Path $InstallDir) | Out-Null
git clone --depth 1 "https://github.com/$Repo.git" $InstallDir 2>$null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $InstallDir)) {
    # no git? download tarball
    $zip = Join-Path $env:TEMP "dotcode.zip"
    Invoke-WebRequest "https://github.com/$Repo/archive/refs/heads/main.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath $env:TEMP -Force
    Move-Item (Join-Path $env:TEMP "dotcode-main") $InstallDir
}
Write-Ok "source in $InstallDir"

# --- build -------------------------------------------------------------------
Write-Step "Building (first build downloads packages)"
Push-Location $InstallDir
try {
    dotnet build DotCode.sln -v q --nologo 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed - run 'cd $InstallDir; dotnet build DotCode.sln' to see why" }
} finally { Pop-Location }
Write-Ok "built"

# --- launcher ----------------------------------------------------------------
Write-Step "Installing launcher"
New-Item -ItemType Directory -Force -Path $BinDir | Out-Null

$runPs1 = Join-Path $InstallDir "run.ps1"
@"
# dotcode launcher
`$ErrorActionPreference = "Continue"
`$dir = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$env:ASPNETCORE_ENVIRONMENT = "Development"
`$jobs = @()
`$p1 = Start-Process dotnet -ArgumentList "run","--project","`$dir\src\DotCode.Server\DotCode.Server.csproj","--no-build","--no-launch-profile" -PassThru -WindowStyle Hidden
`$jobs += `$p1
Write-Host "  starting agent server :4096..."
for (`$i = 0; `$i -lt 40; `$i++) {
    try { Invoke-WebRequest "http://127.0.0.1:4096/global/health" -UseBasicParsing -TimeoutSec 1 | Out-Null; break } catch { Start-Sleep -Milliseconds 500 }
}
`$p2 = Start-Process dotnet -ArgumentList "run","--project","`$dir\src\DotCode.Web\DotCode.Web\DotCode.Web.csproj","--no-build","--no-launch-profile","--urls","http://localhost:5131" -PassThru -WindowStyle Hidden
`$jobs += `$p2
Write-Host "  starting web UI :5131..."
for (`$i = 0; `$i -lt 40; `$i++) {
    try { Invoke-WebRequest "http://localhost:5131/" -UseBasicParsing -TimeoutSec 1 | Out-Null; break } catch { Start-Sleep -Milliseconds 500 }
}
Write-Host ""
Write-Host "  dotcode is running:  http://localhost:5131   (close this window or Ctrl+C to stop)" -ForegroundColor Green
try { Start-Process "http://localhost:5131" } catch { }
try { Wait-Process -Id `$p1.Id -ErrorAction SilentlyContinue } catch { }
try { Stop-Process -Id `$p2.Id -ErrorAction SilentlyContinue } catch { }
"@ | Set-Content -Path $runPs1 -Encoding UTF8

$cmd = Join-Path $BinDir "dotcode.cmd"
@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "$InstallDir\run.ps1" %*
"@ | Set-Content -Path $cmd -Encoding ASCII

$ps1Shim = Join-Path $BinDir "dotcode.ps1"
@"
& "$InstallDir\run.ps1" @args
"@ | Set-Content -Path $ps1Shim -Encoding UTF8

Write-Ok "launcher: $cmd"

# --- PATH --------------------------------------------------------------------
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$BinDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$BinDir;$userPath", "User")
    Write-Host "  added $BinDir to user PATH - open a NEW terminal for 'dotcode' to work"
}

Write-Host ""
Write-Host "Installed. Run:  dotcode" -ForegroundColor White
Write-Host "  then open http://localhost:5131"
