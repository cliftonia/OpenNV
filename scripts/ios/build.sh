#!/bin/zsh
# Export the iOS Xcode project and build a signed app for one device.
# Usage: DEVICE=<udid> [TEAM=<team id>] scripts/ios/build.sh [--install]
#   DEVICE   device UDID (xcrun devicectl list devices)
#   TEAM     Apple development team (defaults to the iOS export preset's)
#   GODOT    Godot 4.8-dev7 mono binary (defaults to the macOS OpenNV.app copy)
#   OUT      work directory (defaults to $TMPDIR/opennv-ios)
#   SKIP_EXPORT=1 reuses the previous Xcode project.
set -euo pipefail
REPO="${0:A:h:h:h}"
: "${DEVICE:?Set DEVICE to the target device UDID}"
TEAM="${TEAM:-$(sed -n 's/^application\/app_store_team_id="\(.*\)"/\1/p' "$REPO/runtime/export_presets.cfg")}"
GODOT="${GODOT:-/Applications/OpenNV.app/Contents/Resources/Godot_mono.app/Contents/MacOS/Godot}"
OUT="${OUT:-${TMPDIR:-/tmp}/opennv-ios}"
mkdir -p "$OUT"

if [[ -z "${SKIP_EXPORT:-}" ]]; then
  rm -rf "$OUT/xcode"; mkdir -p "$OUT/xcode"
  # NativeAOT needs JSON directives for every saved type; regenerate them from current code.
  dotnet build "$REPO/runtime/OpenNV.csproj" -v q -nologo >/dev/null
  dotnet run -c Release --project "$REPO/scripts/ios/JsonAotDirectives" -- \
    "$REPO/runtime/.godot/mono/temp/bin/Debug/OpenNV.dll" "$REPO/runtime/ios/OpenNV.Json.rd.xml" | tail -1
  "$GODOT" --headless --path "$REPO/runtime" --export-release "iOS" "$OUT/xcode/OpenNV.xcodeproj" > "$OUT/export.log" 2>&1 ||
    { echo "Export failed; see $OUT/export.log"; exit 1; }
  [[ -d "$OUT/xcode/OpenNV/dylibs/ExportRelease" ]] ||
    { echo "Export produced no compiled C#; check the Godot msbuild log."; exit 1; }
fi

xcodebuild -project "$OUT/xcode/OpenNV.xcodeproj" -scheme OpenNV -configuration Release \
  -destination "id=$DEVICE" -derivedDataPath "$OUT/dd" \
  DEVELOPMENT_TEAM="$TEAM" CODE_SIGN_STYLE=Automatic CODE_SIGN_IDENTITY="Apple Development" \
  -allowProvisioningUpdates -allowProvisioningDeviceRegistration build > "$OUT/xcodebuild.log" 2>&1 ||
  { echo "Xcode build failed:"; grep -E "error:" "$OUT/xcodebuild.log" | head -5; exit 1; }
APP="$OUT/dd/Build/Products/Release-iphoneos/OpenNV.app"
echo "Built $APP"

if [[ "${1:-}" == --install ]]; then
  xcrun devicectl device install app --device "$DEVICE" "$APP" | grep -E "App installed|ERROR"
fi
