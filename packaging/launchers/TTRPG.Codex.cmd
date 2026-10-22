@echo off
rem Starts TTRPG.Codex with the .NET runtime that ships beside it, then opens it in the browser.
rem Data lives in %LOCALAPPDATA%\TTRPG.Codex, so reinstalling or upgrading never touches your campaigns.
rem   set CODEX_URL=http://0.0.0.0:5080   lets players on your network join
rem   set CODEX_NO_BROWSER=1              skips opening a browser
setlocal
set "APP_DIR=%~dp0"
if not defined CODEX_URL set "CODEX_URL=http://localhost:5080"
set "DOTNET_ROOT=%APP_DIR%runtime"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
if not defined ASPNETCORE_ENVIRONMENT set "ASPNETCORE_ENVIRONMENT=Production"
if not defined ASPNETCORE_URLS set "ASPNETCORE_URLS=%CODEX_URL%"
if not defined Codex__DataDirectory set "Codex__DataDirectory=%LOCALAPPDATA%\TTRPG.Codex\RavenData"
if not exist "%Codex__DataDirectory%" mkdir "%Codex__DataDirectory%"
rem Your own systems and packs live here, so an upgrade never touches them.
if not exist "%LOCALAPPDATA%\TTRPG.Codex\content" mkdir "%LOCALAPPDATA%\TTRPG.Codex\content"
if not defined Codex__ContentDirectories__0 set "Codex__ContentDirectories__0=%LOCALAPPDATA%\TTRPG.Codex\content"

cd /d "%APP_DIR%"
title TTRPG.Codex
echo TTRPG.Codex is starting at %CODEX_URL% (data: %Codex__DataDirectory%). Close this window to stop it.

if not defined CODEX_NO_BROWSER (
    start "" /min powershell -NoProfile -WindowStyle Hidden -Command "Start-Sleep -Seconds 5; Start-Process ('%CODEX_URL%' -replace '//(0\.0\.0\.0|\*|\+)', '//localhost')"
)

"%DOTNET_ROOT%\dotnet.exe" "%APP_DIR%Codex.Web.dll" %*
