@echo off
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
pushd "%~dp0.."
REM Everything after the script name goes straight to the watcher.
REM   tools\run-watcher.bat                    just record, nothing is sent
REM   tools\run-watcher.bat --screenshot       also grab the screen (windowed mode)
REM   tools\run-watcher.bat --rules            what counts as a problem
dotnet run --project src\Gtamp.Watcher -c Release -- %*
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
