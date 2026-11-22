@echo off
REM Removes build output. Leaves server.json, logs and the database alone.
setlocal
pushd "%~dp0.."
dotnet clean Gtamp.sln -v quiet
for /d /r src %%d in (bin obj) do @if exist "%%d" rd /s /q "%%d"
for /d /r tests %%d in (bin obj) do @if exist "%%d" rd /s /q "%%d"
echo Build output removed.
popd
