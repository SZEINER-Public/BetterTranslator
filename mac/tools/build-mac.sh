#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAC="$ROOT/mac"
CONFIG="${CONFIG:-Debug}"
ACTION="${1:-build}"

if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then
    export PATH="$HOME/.dotnet:$PATH"
    export DOTNET_ROOT="$HOME/.dotnet"
  else
    echo "dotnet not found. Install the .NET 10 SDK or run: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir \"\$HOME/.dotnet\"" >&2
    exit 1
  fi
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

case "$ACTION" in
  build)
    dotnet build "$MAC/BetterTranslator.Mac.sln" -c "$CONFIG"
    echo "app: $MAC/src/BetterTranslator.Mac.App/bin/$CONFIG/net10.0/osx-arm64/BetterTranslator"
    ;;
  parity)
    dotnet build "$MAC/tools/parity/BetterTranslator.Mac.Parity.csproj" -c "$CONFIG"
    (cd "$MAC/tools/parity/bin/$CONFIG/net10.0/osx-arm64" && dotnet BetterTranslator.Mac.Parity.dll)
    echo "captures: $MAC/parity-out    report: $MAC/reports/parity.md"
    ;;
  run)
    dotnet run --project "$MAC/src/BetterTranslator.Mac.App" -c "$CONFIG"
    ;;
  publish|bundle)
    "$MAC/tools/make-app.sh"
    ;;
  clean)
    find "$MAC" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
    rm -rf "$MAC/parity-out" "$MAC/publish"
    ;;
  *)
    echo "usage: $0 [build|parity|run|publish|clean]   (CONFIG=Debug|Release)" >&2
    exit 2
    ;;
esac
