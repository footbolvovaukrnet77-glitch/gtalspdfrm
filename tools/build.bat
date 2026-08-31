@echo off
REM Builds every project in the solution, including the net48 GTA V client.
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
pushd "%~dp0.."
if "%~1"=="" (set CONFIG=Debug) else (set CONFIG=%~1)
dotnet build Gtamp.sln -c %CONFIG%
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
