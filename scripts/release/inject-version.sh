#!/usr/bin/env bash
# Inject the semantic-release computed version into the csproj.
# Called by @semantic-release/exec prepareCmd.
#
# We set BOTH:
#   <Version>            -> drives <AssemblyVersion> + <FileVersion>, but
#                            ".NET strips pre-release suffixes from these"
#                            (e.g. "1.2.1-beta.1" becomes "1.2.1.0").
#   <InformationalVersion> -> preserves the full SemVer string ("1.2.1-beta.1")
#                            and is what the plugin UI displays via
#                            PluginVersionHelper.CurrentVersion.
set -euo pipefail

VERSION="${1:?usage: inject-version.sh <version>}"
CSPROJ="Emby.M3uEditor.Plugin/Emby.M3uEditor.Plugin.csproj"

if [[ ! -f "$CSPROJ" ]]; then
  echo "ERROR: $CSPROJ not found (cwd=$(pwd))" >&2
  exit 1
fi

# AssemblyVersion / FileVersion only accept 4-part numeric versions; strip
# any "-beta.N" / "-rc.N" suffix for the <Version> tag.
NUMERIC_VERSION="${VERSION%%-*}"

# Replace the <Version>...</Version> tag (csproj has exactly one).
sed -i -E "s|<Version>[^<]*</Version>|<Version>${NUMERIC_VERSION}</Version>|" "$CSPROJ"

# Add or update <InformationalVersion> with the full SemVer (incl. pre-release tag).
if grep -q "<InformationalVersion>" "$CSPROJ"; then
  sed -i -E "s|<InformationalVersion>[^<]*</InformationalVersion>|<InformationalVersion>${VERSION}</InformationalVersion>|" "$CSPROJ"
else
  sed -i -E "s|(<Version>[^<]*</Version>)|\1\n    <InformationalVersion>${VERSION}</InformationalVersion>|" "$CSPROJ"
fi

echo "[inject-version] $CSPROJ -> <Version>${NUMERIC_VERSION}</Version>, <InformationalVersion>${VERSION}</InformationalVersion>"
grep -E "<(Informational)?Version>" "$CSPROJ" || true
