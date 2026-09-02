@echo off
REM Lays out the files a player copies into their GTA V directory.
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
pushd "%~dp0.."
if "%~1"=="" (set CONFIG=Release) else (set CONFIG=%~1)

dotnet build Gtamp.sln -c %CONFIG% -v quiet
if errorlevel 1 goto :end

set OUT=dist\client
if exist "%OUT%" rd /s /q "%OUT%"
mkdir "%OUT%\scripts"
mkdir "%OUT%\Gtamp\Adapters"
mkdir "%OUT%\RagePluginHook-plugins"

copy /y "src\Gtamp.Client.Shv\bin\%CONFIG%\net48\Gtamp.Client.Shv.dll"  "%OUT%\scripts\" >nul
copy /y "src\Gtamp.Client.Shv\bin\%CONFIG%\net48\Gtamp.Client.Core.dll" "%OUT%\scripts\" >nul
copy /y "src\Gtamp.Client.Shv\bin\%CONFIG%\net48\Gtamp.Shared.dll"      "%OUT%\scripts\" >nul

if exist "src\Gtamp.Adapters.Rph\bin\%CONFIG%\net48\Gtamp.Adapters.Rph.dll" copy /y "src\Gtamp.Adapters.Rph\bin\%CONFIG%\net48\Gtamp.Adapters.Rph.dll" "%OUT%\Gtamp\Adapters\" >nul
if exist "src\Gtamp.Adapters.Lspdfr\bin\%CONFIG%\net48\Gtamp.Adapters.Lspdfr.dll" copy /y "src\Gtamp.Adapters.Lspdfr\bin\%CONFIG%\net48\Gtamp.Adapters.Lspdfr.dll" "%OUT%\Gtamp\Adapters\" >nul

REM Loaded by RAGE Plugin Hook rather than ScriptHookVDotNet, so it belongs in RPH's
REM own plugins folder together with the shared assembly it uses.
if exist "src\Gtamp.RphBridge\bin\%CONFIG%\net48\Gtamp.RphBridge.dll" (
  copy /y "src\Gtamp.RphBridge\bin\%CONFIG%\net48\Gtamp.RphBridge.dll" "%OUT%\RagePluginHook-plugins\" >nul
  copy /y "src\Gtamp.RphBridge\bin\%CONFIG%\net48\Gtamp.Shared.dll" "%OUT%\RagePluginHook-plugins\" >nul
)

echo Client staged in %OUT%
:end
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
