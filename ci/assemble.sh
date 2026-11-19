#!/usr/bin/env bash
# Builds the directory the game's host expects, out of what this repository carries.
#
#   run/RimWorld                     apphost (the process directory is what Bootstrap reads)
#   run/RimWorld.dll + 107 friends   the shipped managed closure from dlls/
#   run/RimWorld.runtimeconfig.json  net10.0 framework reference (the port's own host supplies it on Android)
#   run/RimWorld_Data/Resources/...  the texture subset (Unity data dir: Application.dataPath/Resources)
#   run/Data/{Core,Odyssey}/...      real Defs + About + English keyed strings
#   run/Rendering/Shaders/...        GLSL sources ShaderRegistry resolves by file name
#   run/libsteam_api.so              stub so SteamManager.InitIfNeeded() does not Application.Quit()
#   run/libwaylithaudio.so           stub so MiniAudioBackend.Initialize() resolves
set -euo pipefail

R="${1:-$PWD/run}"
REPO="$PWD"

rm -rf "$R"
mkdir -p "$R" "$R/RimWorld_Data"

cp "$REPO"/dlls/*.dll "$R"/
cp -a "$REPO"/game/Data   "$R"/Data
cp -a "$REPO"/game/Rendering "$R"/Rendering
cp -a "$REPO"/game/Resources "$R"/RimWorld_Data/Resources

APPHOST="$REPO"/ci/apphost/bin/Release/net10.0/RimWorld
test -x "$APPHOST" || { echo "apphost missing: build ci/apphost first"; exit 1; }
cp "$APPHOST" "$R"/RimWorld
cp "$REPO"/ci/RimWorld.runtimeconfig.json "$R"/RimWorld.runtimeconfig.json

# System.IO.Hashing is not part of the shared framework; the port ships it next to the game and
# AssetPack.TryOpen() needs it to read bake.manifest. The SDK carries a usable copy.
DOTNET_ROOT_DIR="$(dirname "$(readlink -f "$(command -v dotnet)")")"
HASHING="$(find "$DOTNET_ROOT_DIR/sdk" -maxdepth 2 -name System.IO.Hashing.dll | sort | head -1)"
test -n "$HASHING" || { echo "System.IO.Hashing.dll not found under $DOTNET_ROOT_DIR/sdk"; exit 1; }
cp "$HASHING" "$R"/

gcc -shared -fPIC -O1 -o "$R"/libwaylithaudio.so "$REPO"/natives/waylithaudio_stub.c
gcc -shared -fPIC -O1 -o "$R"/libsteam_api.so      "$REPO"/natives/steam_stub.c

# Deterministic mod list: Core + Odyssey (the unique-weapon defs live in Odyssey).
CFG="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config"
mkdir -p "$CFG"
cp "$REPO"/ci/ModsConfig.xml "$CFG"/ModsConfig.xml

echo "--- assembled $(du -sh "$R" | cut -f1) in $R"
ls "$R" | wc -l
