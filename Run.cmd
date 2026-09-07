@echo off
setlocal
cd /d "%~dp0"
set "OEPS_DOTNET=dotnet"
if exist "%~dp0..\raw-material-sticker\.tools\dotnet\dotnet.exe" set "OEPS_DOTNET=%~dp0..\raw-material-sticker\.tools\dotnet\dotnet.exe"
if exist "%~dp0.tools\dotnet\dotnet.exe" set "OEPS_DOTNET=%~dp0.tools\dotnet\dotnet.exe"
set "DOTNET_CLI_HOME=%~dp0.local\dotnet"
set "NUGET_PACKAGES=%~dp0.local\nuget"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
"%OEPS_DOTNET%" build src\Oeps.KicadProductionFiles.App -c Release --nologo
if errorlevel 1 goto failed
"%OEPS_DOTNET%" "src\Oeps.KicadProductionFiles.App\bin\Release\net10.0-windows\Oeps.KicadProductionFiles.App.dll" %*
if not errorlevel 1 exit /b 0
:failed
echo OEPS KiCad Production Files could not start. See the error above.
pause
exit /b 1
