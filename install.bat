@echo off
REM dotcode installer - Windows CMD
REM Usage:
REM   curl -fsSL https://raw.githubusercontent.com/xonix97/dotcode/main/install.bat -o dotcode-install.bat && dotcode-install.bat
setlocal enabledelayedexpansion
title dotcode installer

set "REPO=xonix97/dotcode"
if defined DOTCODE_HOME (set "INSTALL_DIR=%DOTCODE_HOME%") else (set "INSTALL_DIR=%USERPROFILE%\.dotcode")
if defined DOTCODE_BIN  (set "BIN_DIR=%DOTCODE_BIN%")   else (set "BIN_DIR=%USERPROFILE%\.dotcode\bin")

echo.
echo == dotcode installer (Windows)

REM --- check dotnet -----------------------------------------------------------
echo [1/5] Checking .NET 9 SDK...
set DOTNET_OK=0
where dotnet >nul 2>nul
if %errorlevel%==0 (
    for /f "tokens=1 delims=. " %%v in ('dotnet --version 2^>nul') do (
        if %%v GEQ 9 set DOTNET_OK=1
    )
)
if %DOTNET_OK%==1 (
    echo   [OK] dotnet found
) else (
    echo   installing .NET 9 SDK via winget...
    where winget >nul 2>nul
    if !errorlevel!==0 (
        winget install Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements
    ) else (
        echo   winget not found - downloading dotnet-install script...
        powershell -NoProfile -Command "irm https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1; & $env:TEMP\dotnet-install.ps1 -Channel 9.0 -InstallDir $env:USERPROFILE\.dotnet"
        set "PATH=%USERPROFILE%\.dotnet;%PATH%"
        powershell -NoProfile -Command "[Environment]::SetEnvironmentVariable('Path', '%USERPROFILE%\.dotnet;' + [Environment]::GetEnvironmentVariable('Path','User'), 'User')"
    )
    echo   [OK] .NET 9 SDK installed - you may need to open a NEW terminal afterwards
)

REM --- clone source -----------------------------------------------------------
echo [2/5] Fetching dotcode source...
if exist "%INSTALL_DIR%" rmdir /s /q "%INSTALL_DIR%"
git clone --depth 1 "https://github.com/%REPO%.git" "%INSTALL_DIR%" >nul 2>nul
if not exist "%INSTALL_DIR%\DotCode.sln" (
    echo   git not available or failed - downloading zip...
    powershell -NoProfile -Command "irm https://github.com/%REPO%/archive/refs/heads/main.zip -OutFile $env:TEMP\dotcode.zip; Expand-Archive $env:TEMP\dotcode.zip $env:TEMP -Force"
    if exist "%TEMP%\dotcode-main" move "%TEMP%\dotcode-main" "%INSTALL_DIR%" >nul
)
if not exist "%INSTALL_DIR%\DotCode.sln" (
    echo   [FAIL] could not fetch source.
    pause
    exit /b 1
)
echo   [OK] source in %INSTALL_DIR%

REM --- build ------------------------------------------------------------------
echo [3/5] Building (first build downloads packages - takes a minute)...
pushd "%INSTALL_DIR%"
dotnet build DotCode.sln -v q --nologo >nul 2>nul
if not %errorlevel%==0 (
    echo   [FAIL] build failed. Run to see why:
    echo     cd "%INSTALL_DIR%" ^&^& dotnet build DotCode.sln
    popd
    pause
    exit /b 1
)
popd
echo   [OK] built

REM --- launcher ---------------------------------------------------------------
echo [4/5] Installing launcher...
if not exist "%BIN_DIR%" mkdir "%BIN_DIR%"

> "%INSTALL_DIR%\run.cmd" (
    @echo @echo off
    @echo setlocal
    @echo set "DIR=%%~dp0"
    @echo set ASPNETCORE_ENVIRONMENT=Development
    @echo start "dotcode-server" /min cmd /c "dotnet run --project ""%%DIR%%src\DotCode.Server\DotCode.Server.csproj"" --no-build --no-launch-profile"
    @echo echo   starting agent server :4096...
    @echo powershell -NoProfile -Command "for($i=0;$i -lt 40;$i++){ try{ Invoke-WebRequest 'http://127.0.0.1:4096/global/health' -UseBasicParsing -TimeoutSec 1 ^| Out-Null; break }catch{ Start-Sleep -Milliseconds 500 } }"
    @echo start "dotcode-web" /min cmd /c "dotnet run --project ""%%DIR%%src\DotCode.Web\DotCode.Web\DotCode.Web.csproj"" --no-build --no-launch-profile --urls http://localhost:5131"
    @echo echo   starting web UI :5131...
    @echo powershell -NoProfile -Command "for($i=0;$i -lt 40;$i++){ try{ Invoke-WebRequest 'http://localhost:5131/' -UseBasicParsing -TimeoutSec 1 ^| Out-Null; break }catch{ Start-Sleep -Milliseconds 500 } }"
    @echo start "" "http://localhost:5131"
    @echo echo.
    @echo echo   dotcode is running:  http://localhost:5131
    @echo echo   Close the two minimized 'dotcode-server'/'dotcode-web' windows to stop.
    @echo echo   Or run:  taskkill /IM DotCode.Server.exe /F  ^&  taskkill /IM DotCode.Web.exe /F
)

> "%BIN_DIR%\dotcode.cmd" (
    @echo @echo off
    @echo call "%INSTALL_DIR%\run.cmd" %%*
)

echo   [OK] launcher: %BIN_DIR%\dotcode.cmd

REM --- PATH -------------------------------------------------------------------
echo [5/5] Adding to PATH...
powershell -NoProfile -Command "$p=[Environment]::GetEnvironmentVariable('Path','User'); if($p -notlike '*%BIN_DIR:*=%*'){ [Environment]::SetEnvironmentVariable('Path','%BIN_DIR%;'+$p,'User'); Write-Host '   added to user PATH - open a NEW terminal' } else { Write-Host '   already on PATH' }"

echo.
echo Installed. Run:  dotcode
echo   then open http://localhost:5131
pause
endlocal
