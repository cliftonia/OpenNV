#!/bin/zsh
# Copy your own Fallout: New Vegas installation (and optional mods) into the
# installed app's Documents folder. Nothing leaves your machine and device.
# Usage: DEVICE=<udid> scripts/ios/push-data.sh <FalloutNV folder> [mod folder ...]
#   The FalloutNV folder is the game root containing Data/ and FalloutNV.exe.
#   Each mod folder is copied to Documents/Mods/<folder name>; list them in
#   launch-args.txt (see launch-args.example.txt) to load them.
set -euo pipefail
REPO="${0:A:h:h:h}"
: "${DEVICE:?Set DEVICE to the target device UDID}"
GAME="${1:?Pass the FalloutNV game folder}"; shift
BUNDLE=$(awk '/^name="iOS"/{ios=1} ios && /application\/bundle_identifier=/{gsub(/.*="|"/,""); print; exit}' "$REPO/runtime/export_presets.cfg")

copy() {
  echo "copy $1 -> $2"
  xcrun devicectl device copy to --device "$DEVICE" --domain-type appDataContainer \
    --domain-identifier "$BUNDLE" --source "$1" --destination "$2" >/dev/null
}

[[ -d "$GAME/Data" ]] || { echo "$GAME has no Data folder"; exit 1; }
for file in "$GAME"/*.exe(N) "$GAME"/*.ini(N); do copy "$file" "Documents/FalloutNV/${file:t}"; done
copy "$GAME/Data" Documents/FalloutNV/Data
for mod in "$@"; do copy "$mod" "Documents/Mods/${mod:t}"; done
ARGS="${LAUNCH_ARGS:-$REPO/scripts/ios/launch-args.example.txt}"
copy "$ARGS" Documents/OpenNV/launch-args.txt
echo "Done. Launch OpenNV on the device."
