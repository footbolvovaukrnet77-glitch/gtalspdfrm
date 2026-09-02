@echo off
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
pushd "%~dp0.."
REM Everything after the script name is passed straight to the bot.
REM   tools\run-bot.bat --task follow
REM   tools\run-bot.bat --count 10 --task patrol
dotnet run --project src\Gtamp.Bot -c Release -- %*
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
