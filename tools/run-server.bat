@echo off
REM Builds and starts the server. Arguments pass through, e.g. run-server.bat --port 27020
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
pushd "%~dp0.."
dotnet build src\Gtamp.Server\Gtamp.Server.csproj -c Debug -v quiet
if errorlevel 1 goto :end
dotnet run --project src\Gtamp.Server\Gtamp.Server.csproj -c Debug --no-build -- %*
:end
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
