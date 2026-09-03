@echo off
setlocal
set "ROOT=%~dp0..\.."
set "MAC=%ROOT%\mac"
if "%CONFIG%"=="" set "CONFIG=Debug"
set "ACTION=%~1"
if "%ACTION%"=="" set "ACTION=build"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

where dotnet >nul 2>nul
if errorlevel 1 (
  echo dotnet not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
  exit /b 1
)

if /i "%ACTION%"=="build" (
  dotnet build "%MAC%\BetterTranslator.Mac.sln" -c %CONFIG%
  exit /b %errorlevel%
)
if /i "%ACTION%"=="parity-windows" (
  dotnet build "%MAC%\tools\parity-windows\BetterTranslator.Mac.ParityWindows.csproj" -c %CONFIG%
  if errorlevel 1 exit /b %errorlevel%
  dotnet run --project "%MAC%\tools\parity-windows" -c %CONFIG% --no-build
  echo reference captures: %MAC%\parity-out
  exit /b %errorlevel%
)
if /i "%ACTION%"=="publish" (
  dotnet publish "%MAC%\src\BetterTranslator.Mac.App" -c Release -r osx-arm64 --self-contained true -o "%MAC%\publish\osx-arm64"
  exit /b %errorlevel%
)
if /i "%ACTION%"=="clean" (
  for /d /r "%MAC%" %%d in (bin obj) do if exist "%%d" rd /s /q "%%d"
  if exist "%MAC%\parity-out" rd /s /q "%MAC%\parity-out"
  if exist "%MAC%\publish" rd /s /q "%MAC%\publish"
  exit /b 0
)
echo usage: %~nx0 [build^|parity-windows^|publish^|clean]   (set CONFIG=Debug^|Release)
exit /b 2
