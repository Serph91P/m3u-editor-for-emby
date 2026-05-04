#!/usr/bin/env bash
# Inject the semantic-release computed version into the csproj.
# Called by @semantic-release/exec prepareCmd.
set -euo pipefail

VERSION="${1:?usage: inject-version.sh <version>}"
CSPROJ="Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj"

if [[ ! -f "$CSPROJ" ]]; then
  echo "ERROR: $CSPROJ not found (cwd=$(pwd))" >&2
  exit 1
fi

# Replace the <Version>...</Version> tag (csproj has exactly one).
sed -i -E "s|<Version>[^<]*</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"

echo "[inject-version] $CSPROJ -> <Version>${VERSION}</Version>"
grep -E "<Version>" "$CSPROJ" || true
