@echo off
setlocal
set "ROOT=%~dp0..\.."
set "MAC=%ROOT%\mac"
set "OUT=%MAC%\publish\osx-arm64"
set "APP=%MAC%\publish\BetterTranslator.app"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

where dotnet >nul 2>nul
if errorlevel 1 (
  echo dotnet not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
  exit /b 1
)

dotnet publish "%MAC%\src\BetterTranslator.Mac.App" -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=false -o "%OUT%"
if errorlevel 1 exit /b %errorlevel%

if exist "%APP%" rd /s /q "%APP%"
mkdir "%APP%\Contents\MacOS"
mkdir "%APP%\Contents\Resources"
xcopy /e /i /q /y "%OUT%\*" "%APP%\Contents\MacOS\" >nul
copy /y "%MAC%\tools\Info.plist" "%APP%\Contents\Info.plist" >nul

echo executable: %APP%\Contents\MacOS\BetterTranslator
echo bundle:     %APP%
echo Copy the .app to a Mac, then run once:  chmod +x "BetterTranslator.app/Contents/MacOS/BetterTranslator"
echo (Windows cannot set the Unix execute bit or clear quarantine; the .sh does both.)
exit /b 0
