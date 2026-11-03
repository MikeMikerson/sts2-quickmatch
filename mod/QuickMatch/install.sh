#!/bin/sh
# Puts Quick Match and its relay companion (QuickMatchRelay, ../QuickMatch.Relay) back into
# the game's mods folder, for example after a Steam update or "Verify integrity of game files"
# removed them. Builds the Release versions first (quick when nothing changed), so the installed
# copies always match the source. A QuickMatchRelay.cfg already in the mods folder is kept.
#
#   ./install.sh                 install into the default macOS Steam location
#   ./install.sh /path/to/mods   install into another mods folder
set -eu

cd "$(dirname "$0")"
app="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app"
mods="${1:-$app/Contents/MacOS/mods}"
out="bin/Release/net9.0"

echo "Building Release..."
dotnet build -c Release -p:ModsPath= > /dev/null
(cd ../QuickMatch.Relay && dotnet build -c Release -p:ModsPath= > /dev/null)

if [ ! -d "$(dirname "$mods")" ]; then
    echo "Game folder not found: $(dirname "$mods")" >&2
    echo "Pass the mods folder as an argument." >&2
    exit 1
fi

mkdir -p "$mods/QuickMatch"
cp "$out/QuickMatch.dll" "$out/QuickMatch.pdb" QuickMatch.json "$mods/QuickMatch/"
echo "Installed QuickMatch.dll, .pdb and .json into $mods/QuickMatch"

relay="../QuickMatch.Relay"
mkdir -p "$mods/QuickMatchRelay"
cp "$relay/$out/QuickMatchRelay.dll" "$relay/$out/QuickMatchRelay.pdb" "$relay/QuickMatchRelay.json" "$mods/QuickMatchRelay/"
echo "Installed QuickMatchRelay.dll, .pdb and .json into $mods/QuickMatchRelay"
