#!/usr/bin/env bash
# Build the plugin DLL and produce release artifacts (zip + checksums).
# Called by @semantic-release/exec prepareCmd after inject-version.sh.
set -euo pipefail

VERSION="${1:?usage: build-artifacts.sh <version>}"
PROJECT="Emby.M3uEditor.Plugin/Emby.M3uEditor.Plugin.csproj"
TFM="netstandard2.0"
OUT_DIR="artifacts"
DLL_NAME="Emby.M3uEditor.Plugin.dll"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "[build-artifacts] dotnet restore"
dotnet restore "$PROJECT"

echo "[build-artifacts] dotnet build -c Release"
dotnet build --no-restore -c Release "$PROJECT"

DLL_SRC="Emby.M3uEditor.Plugin/bin/Release/${TFM}/${DLL_NAME}"
if [[ ! -f "$DLL_SRC" ]]; then
  echo "ERROR: built DLL not found at $DLL_SRC" >&2
  exit 1
fi

cp "$DLL_SRC" "$OUT_DIR/$DLL_NAME"

ZIP_NAME="m3u-editor-for-emby-${VERSION}.zip"
( cd "$OUT_DIR" && zip -q "$ZIP_NAME" "$DLL_NAME" )

# Both checksums (MD5 for Emby's plugin manifest, SHA256 for general integrity)
( cd "$OUT_DIR" && sha256sum "$ZIP_NAME" > "m3u-editor-for-emby-${VERSION}.sha256" )
( cd "$OUT_DIR" && md5sum    "$DLL_NAME" > "m3u-editor-for-emby-${VERSION}.md5"    )

echo "[build-artifacts] artifacts:"
ls -la "$OUT_DIR"
