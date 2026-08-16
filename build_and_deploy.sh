#!/bin/bash
# Build and deploy SkaldAccessibility (+ dev bridge) to the game's BepInEx 5 install.
# The game must not be running (the DLLs are locked while it is).
# Usage: ./build_and_deploy.sh [--no-bridge]

set -e
REPO="$(cd "$(dirname "$0")" && pwd)"
GAME="/c/Program Files (x86)/Steam/steamapps/common/SKALD Against the Black Priory"
PLUGINS="$GAME/BepInEx/plugins"

# HARD GUARD (owner correction 2026-08-16): never deploy over a live session.
# The running game may be the owner's ride — deploying fails on locked DLLs
# anyway, and quitting/launching over it is never this script's call.
if tasklist //FI "IMAGENAME eq SKALD Against the Black Priory.exe" 2>/dev/null | grep -qi "SKALD"; then
  echo "ABORT: the game is running. Close it (or ask the owner to) before deploying."
  exit 1
fi

echo "=== Building mod (Release) ==="
dotnet build "$REPO/src/SkaldAccessibility.csproj" -c Release -v minimal

mkdir -p "$PLUGINS/SkaldAccessibility"
cp "$REPO/src/bin/Release/SkaldAccessibility.dll" "$PLUGINS/SkaldAccessibility/"
echo "Deployed SkaldAccessibility.dll"

if [ "$1" != "--no-bridge" ]; then
  echo "=== Building bridge (Release, dev-only — never ships) ==="
  dotnet build "$REPO/bridge/SkaldBridge.csproj" -c Release -v minimal
  mkdir -p "$PLUGINS/SkaldBridge"
  cp "$REPO/bridge/bin/Release/SkaldBridge.dll" "$PLUGINS/SkaldBridge/"
  echo "Deployed SkaldBridge.dll (127.0.0.1:8332)"
fi

# Speech DLLs beside the exe (Tolk arrives with WP2; the NVDA client serves the
# current direct-P/Invoke backend). Copy-if-missing so game-root state is explicit.
for dll in Tolk.dll nvdaControllerClient64.dll; do
  if [ ! -f "$GAME/$dll" ]; then
    echo "WARNING: $dll missing beside the game exe — speech will be log-only."
  fi
done

echo "=== Deploy complete. Launch via steam://rungameid/1069160 ==="
