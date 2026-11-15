#!/bin/sh
# Downloads the real BepInEx.dll (the reference the check compiles the mod's BepInEx calls against). Not committed.
set -e
cd "$(dirname "$0")"
mkdir -p lib
tmp="$(mktemp -d)"
curl -sL -o "$tmp/bepinex.zip" "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
unzip -o -q "$tmp/bepinex.zip" "BepInEx/core/BepInEx.dll" -d "$tmp/x"
cp "$tmp/x/BepInEx/core/BepInEx.dll" lib/
rm -rf "$tmp"
echo "lib/BepInEx.dll ready"
