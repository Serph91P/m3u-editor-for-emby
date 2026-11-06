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
dotnet build --no-restore -c Release "$PROJECT" \
  -p:Deterministic=true -p:PathMap="${PWD}=/_/src" \
  -p:SourceRevisionId="$(git rev-parse HEAD)"

DLL_SRC="Emby.M3uEditor.Plugin/bin/Release/${TFM}/${DLL_NAME}"
if [[ ! -f "$DLL_SRC" ]]; then
  echo "ERROR: built DLL not found at $DLL_SRC" >&2
  exit 1
fi

cp "$DLL_SRC" "$OUT_DIR/$DLL_NAME"

ZIP_NAME="m3u-editor-for-emby-${VERSION}.zip"
# Fixed ZIP metadata and normalized compiler paths make a clean-source retry byte-identical.
python3 - "$OUT_DIR/$ZIP_NAME" "$OUT_DIR/$DLL_NAME" "$DLL_NAME" <<'PY'
import pathlib
import sys
import zipfile
entry = zipfile.ZipInfo(sys.argv[3], date_time=(1980, 1, 1, 0, 0, 0))
entry.compress_type = zipfile.ZIP_DEFLATED
entry.create_system = 3
entry.external_attr = 0o100644 << 16
with zipfile.ZipFile(sys.argv[1], "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    archive.writestr(entry, pathlib.Path(sys.argv[2]).read_bytes(), compresslevel=9)
PY

# Both checksums (MD5 for Emby's plugin manifest, SHA256 for general integrity)
( cd "$OUT_DIR" && sha256sum "$ZIP_NAME" > "m3u-editor-for-emby-${VERSION}.sha256" )
( cd "$OUT_DIR" && md5sum    "$DLL_NAME" > "m3u-editor-for-emby-${VERSION}.md5"    )

echo "[build-artifacts] artifacts:"
ls -la "$OUT_DIR"
