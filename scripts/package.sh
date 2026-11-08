#!/usr/bin/env bash
# Builds the installable plugin zip.
#   scripts/package.sh 1.2.0      -> dist/local-prerolls-pro_1.2.0.0.zip and dist/local-prerolls-pro.zip
# Prints the zip's MD5 checksum (Jellyfin checks it when installing from the plugin repository).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(python3 "$ROOT/scripts/version.py" "${1:-1.0.0}")"
OUT="$ROOT/dist"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

dotnet publish "$ROOT/src/Jellyfin.Plugin.LocalPrerollsPro" -c Release -o "$STAGE/publish" --nologo -v q \
  -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"

mkdir -p "$STAGE/plugin" "$OUT"
cp "$STAGE/publish/Jellyfin.Plugin.LocalPrerollsPro.dll" "$STAGE/plugin/"
cp "$ROOT/images/logo.png" "$STAGE/plugin/logo.png"
python3 "$ROOT/scripts/meta.py" "$VERSION" > "$STAGE/plugin/meta.json"

ZIP="$OUT/local-prerolls-pro_$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE/plugin" && python3 -c "
import sys, zipfile, os
with zipfile.ZipFile(sys.argv[1], 'w', zipfile.ZIP_DEFLATED) as z:
    for name in sorted(os.listdir('.')):
        z.write(name)
" "$ZIP")
cp "$ZIP" "$OUT/local-prerolls-pro.zip"   # stable name for the "latest" download link
md5sum "$ZIP" | cut -d' ' -f1 > "$ZIP.md5"
echo "Built $ZIP"
echo "MD5 $(cat "$ZIP.md5")"
