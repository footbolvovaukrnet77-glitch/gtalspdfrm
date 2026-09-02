@echo off
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
pushd "%~dp0.."
if "%~1"=="" (set CONFIG=Debug) else (set CONFIG=%~1)
dotnet clean Gtamp.sln -c %CONFIG% -v quiet
dotnet build Gtamp.sln -c %CONFIG% --no-incremental
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
