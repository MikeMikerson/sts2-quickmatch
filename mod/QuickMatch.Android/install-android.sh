#!/bin/sh
# Puts Quick Match, its Android companion and its relay companion (QuickMatchRelay) on a phone
# running the StS2 Launcher, over adb (USB debugging on). Builds the Release versions first (quick
# when nothing changed), so the phone's copies always match the source, then checks each copied
# file's SHA-256 on the phone. It only adds or overwrites files in the launcher's Mods folder and
# never deletes anything on the phone (a QuickMatchRelay.cfg there is kept).
#
#   ./install-android.sh              the only connected phone
#   ./install-android.sh <serial>     one phone of several (see "adb devices")
set -eu

cd "$(dirname "$0")"
adb="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
[ -x "$adb" ] || adb="$(command -v adb || true)"
if [ -z "$adb" ]; then
    echo "adb not found. Install Android platform-tools or set ADB=/path/to/adb." >&2
    exit 1
fi
serial="${1:-}"
root="/storage/emulated/0/StS2Launcher"
mods="$root/Mods"

phone() {
    if [ -n "$serial" ]; then
        "$adb" -s "$serial" "$@"
    else
        "$adb" "$@"
    fi
}

echo "Building QuickMatch, QuickMatchAndroid and QuickMatchRelay (Release)..."
(cd ../QuickMatch && dotnet build -c Release -p:ModsPath= > /dev/null)
dotnet build -c Release -p:ModsPath= > /dev/null
(cd ../QuickMatch.Relay && dotnet build -c Release -p:ModsPath= > /dev/null)

if ! phone shell "[ -d '$mods' ]"; then
    echo "The launcher's Mods folder was not found on the phone: $mods" >&2
    echo "Open the StS2 Launcher once and let it download the game." >&2
    exit 1
fi
version="$(phone shell "cat '$root/GameFiles/release_info.json' 2>/dev/null" | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p')"
echo "Game on the phone: ${version:-unknown} (Quick Match only matches players on the same version)"

# install_mod <mod id> <project folder>
install_mod() {
    id="$1"
    out="$2/bin/Release/net9.0"
    phone shell "mkdir -p '$mods/$id'"
    for file in "$out/$id.dll" "$out/$id.pdb" "$2/$id.json"; do
        name="$(basename "$file")"
        phone push "$file" "$mods/$id/$name" > /dev/null
        want="$(shasum -a 256 "$file" | cut -d' ' -f1)"
        got="$(phone shell "sha256sum '$mods/$id/$name'" | cut -d' ' -f1)"
        if [ "$want" != "$got" ]; then
            echo "The copy of $name on the phone does not match (expected $want, found $got)." >&2
            exit 1
        fi
    done
    echo "Installed $id.dll, .pdb and .json into $mods/$id"
}

# Folder names decide the load order on the phone (Godot lists them sorted), and "QuickMatch" sorts
# before "QuickMatchAndroid" and "QuickMatchRelay", so the dependency is loaded first.
install_mod QuickMatch ../QuickMatch
install_mod QuickMatchAndroid .
install_mod QuickMatchRelay ../QuickMatch.Relay
echo "Restart the game in the launcher to load them."
