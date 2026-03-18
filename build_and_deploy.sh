#!/bin/bash
# Build and deploy SkaldAccessibility mod
# Usage: ./build_and_deploy.sh

set -e

export PATH="$HOME/.dotnet:$PATH"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"
GAME_DIR="/c/Program Files (x86)/Steam/steamapps/common/SKALD Against the Black Priory"
PLUGIN_DIR="$GAME_DIR/BepInEx/plugins/SkaldAccessibility"

echo "Building SkaldAccessibility..."
cd "$SRC_DIR"
dotnet build -c Debug --nologo -v q

echo "Deploying to game directory..."
mkdir -p "$PLUGIN_DIR"
cp "$SRC_DIR/bin/Debug/netstandard2.1/SkaldAccessibility.dll" "$PLUGIN_DIR/"

echo "Done. Plugin deployed to: $PLUGIN_DIR"
echo "Launch the game to test."
