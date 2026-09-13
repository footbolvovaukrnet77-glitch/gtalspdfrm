@echo off
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
pushd "%~dp0.."
dotnet test tests\Gtamp.Tests\Gtamp.Tests.csproj %*
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
