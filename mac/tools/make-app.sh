#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAC="$ROOT/mac"
OUT="$MAC/publish/osx-arm64"
APP="$MAC/publish/BetterTranslator.app"

if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="$HOME/.dotnet"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet publish "$MAC/src/BetterTranslator.Mac.App" -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=false -o "$OUT"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT"/. "$APP/Contents/MacOS/"
cp "$MAC/tools/Info.plist" "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/BetterTranslator"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

echo "executable: $APP/Contents/MacOS/BetterTranslator"
echo "bundle:     $APP"
echo "run:        open \"$APP\"    (unsigned; if Gatekeeper refuses, right-click the app and choose Open)"
