#!/usr/bin/env bash
set -euo pipefail

# Package the plugin into a zip ready to drop into Jellyfin's plugins dir.
# Output: build/output/Federation_<version>.zip

cd "$(dirname "$0")/.."

VERSION=$(grep -oP '<Version>\K[^<]+' src/Jellyfin.Plugin.Federation/Jellyfin.Plugin.Federation.csproj)
OUT_DIR="build/output"
STAGE="build/stage/Federation_${VERSION}"

rm -rf "$OUT_DIR" "$STAGE"
mkdir -p "$OUT_DIR" "$STAGE"

dotnet build src/Jellyfin.Plugin.Federation/Jellyfin.Plugin.Federation.csproj -c Release

cp src/Jellyfin.Plugin.Federation/bin/Release/net8.0/Jellyfin.Plugin.Federation.dll "$STAGE/"
cp src/Jellyfin.Plugin.Federation/meta.json "$STAGE/"

(cd build/stage && zip -r "../../$OUT_DIR/Federation_${VERSION}.zip" "Federation_${VERSION}")

echo "Packaged: $OUT_DIR/Federation_${VERSION}.zip"
echo "Drop into: <jellyfin-config>/plugins/Federation_${VERSION}/ (unzipped)"
