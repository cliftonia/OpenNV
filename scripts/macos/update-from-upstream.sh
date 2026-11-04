#!/bin/zsh
# Pull the latest upstream OpenNV, replay the mac-play commits on top,
# rebuild, export the macOS release app and install it into OpenNV.app.
# Usage: scripts/macos/update-from-upstream.sh [--no-push]
set -euo pipefail
REPO="${0:A:h:h:h}"
APP=/Applications/OpenNV.app
GODOT="$APP/Contents/Resources/Godot_mono.app/Contents/MacOS/Godot"
EXPORT_DIR="${TMPDIR:-/tmp}/opennv-export"
cd "$REPO"

[[ $(git branch --show-current) == mac-play ]] || { echo "Switch to the mac-play branch first."; exit 1; }
# Godot rewrites .import defaults on every editor import; they are not work.
git checkout -- '*.import' 2>/dev/null || true
if [[ -n $(git status --porcelain) ]]; then
  echo "Uncommitted changes present; commit or stash them first:"; git status --short; exit 1
fi

before=$(git rev-parse --short upstream/main 2>/dev/null || echo none)
git fetch upstream
after=$(git rev-parse --short upstream/main)
echo "upstream/main: $before -> $after ($(git rev-list --count HEAD..upstream/main) new commits)"
if ! git rebase upstream/main; then
  echo "Rebase stopped on a conflict. Resolve it, then 'git rebase --continue' and rerun this script."
  exit 1
fi

dotnet build runtime/OpenNV.csproj -c Debug -p:Optimize=true -nologo -v q
rm -rf "$EXPORT_DIR"; mkdir -p "$EXPORT_DIR"
"$GODOT" --headless --path runtime --export-release "macOS" "$EXPORT_DIR/OpenNV.app" > "$EXPORT_DIR/export.log" 2>&1 ||
  { echo "Export failed; see $EXPORT_DIR/export.log"; exit 1; }
# Local ad-hoc signature without the hardened runtime: .NET needs JIT.
codesign --force --deep --sign - "$EXPORT_DIR/OpenNV.app"
rm -rf "$APP/Contents/Resources/OpenNV-Release.app"
ditto "$EXPORT_DIR/OpenNV.app" "$APP/Contents/Resources/OpenNV-Release.app"
git checkout -- '*.import' 2>/dev/null || true

[[ ${1:-} == --no-push ]] || git push --force-with-lease origin mac-play
echo "Done: OpenNV.app now runs $(git rev-parse --short HEAD) (upstream $after + mac-play)."
