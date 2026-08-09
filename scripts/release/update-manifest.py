#!/usr/bin/env python3
"""
Generate Emby plugin repository manifests (stable + beta) from GitHub releases.

Output files (written to the directory passed as --out):
    manifest.json       -> stable releases only (no '-beta.' in the tag)
    manifest-beta.json  -> all releases including prereleases

Emby manifest schema (per plugin entry):
    {
      "guid": "<plugin-guid>",
      "name": "...",
      "description": "...",
      "overview": "...",
      "owner": "...",
      "category": "...",
      "imageUrl": "...",
      "versions": [
        {
          "version": "1.0.23.0",                # 4-segment version
          "changelog": "...",
          "targetAbi": "4.8.0.0",
          "sourceUrl": "https://.../Emby.M3uEditor.Plugin.dll",
          "checksum": "<md5>",
          "timestamp": "2026-05-04T12:34:56Z"
        }
      ]
    }

Versioning map:
    1.0.23           -> 1.0.23.0  (stable)
    1.0.24-beta.3    -> 1.0.24.3  (beta channel only)
    1.0.24-beta      -> 1.0.24.1  (defensive default)
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import urllib.request
from pathlib import Path
from typing import Any

# ---------- Plugin metadata (must match Plugin.cs) ----------

PLUGIN_GUID = "b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5"
PLUGIN_NAME = "m3u-editor for Emby"
PLUGIN_DESCRIPTION = "Live TV, EPG, VOD, and managed library publishing for Emby"
PLUGIN_OVERVIEW = (
    "Connects Emby to Xtream-compatible backends for Live TV, EPG, VOD, series, "
    "and optional managed m3u-editor library publishing."
)
PLUGIN_OWNER = "Serph91P"
PLUGIN_CATEGORY = "Live TV"
PLUGIN_IMAGE_URL = (
    "https://raw.githubusercontent.com/Serph91P/m3u-editor-for-emby/main/Emby.M3uEditor.Plugin/thumb.png"
)
TARGET_ABI = "4.8.0.0"
DLL_ASSET_NAME = "Emby.M3uEditor.Plugin.dll"

# ---------- Helpers ----------

SEMVER_RE = re.compile(
    r"^v?(?P<major>\d+)\.(?P<minor>\d+)\.(?P<patch>\d+)(?:-(?P<pre>[\w.\-]+))?$"
)


def gh_get(path: str, token: str) -> Any:
    url = f"https://api.github.com{path}"
    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "m3u-editor-for-emby-manifest-generator",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def fetch_releases(repo: str, token: str) -> list[dict]:
    out: list[dict] = []
    page = 1
    while True:
        chunk = gh_get(f"/repos/{repo}/releases?per_page=100&page={page}", token)
        if not chunk:
            break
        out.extend(chunk)
        if len(chunk) < 100:
            break
        page += 1
    return out


def parse_semver(tag: str) -> tuple[int, int, int, str | None] | None:
    m = SEMVER_RE.match(tag.strip())
    if not m:
        return None
    return (
        int(m["major"]),
        int(m["minor"]),
        int(m["patch"]),
        m["pre"],
    )


def to_emby_version(major: int, minor: int, patch: int, pre: str | None) -> str:
    """semver -> 4-segment Emby version. Stable = X.Y.Z.0; beta.N -> X.Y.Z.N."""
    if pre is None:
        return f"{major}.{minor}.{patch}.0"
    n = 1
    m = re.search(r"\.(\d+)$", pre)
    if m:
        n = int(m.group(1))
    return f"{major}.{minor}.{patch}.{n}"


def find_dll_asset(release: dict) -> dict | None:
    for asset in release.get("assets", []):
        if asset.get("name") == DLL_ASSET_NAME:
            return asset
    return None


def md5_of_url(url: str, token: str) -> str:
    """Download an asset (small DLL) and return MD5 hex digest."""
    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/octet-stream",
            "User-Agent": "m3u-editor-for-emby-manifest-generator",
        },
    )
    h = hashlib.md5()
    with urllib.request.urlopen(req, timeout=120) as resp:
        for chunk in iter(lambda: resp.read(64 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def build_version_entry(release: dict, token: str) -> dict | None:
    tag = release.get("tag_name", "")
    parsed = parse_semver(tag)
    if not parsed:
        print(f"  skip {tag}: not semver", file=sys.stderr)
        return None
    major, minor, patch, pre = parsed
    dll_asset = find_dll_asset(release)
    if not dll_asset:
        print(f"  skip {tag}: no {DLL_ASSET_NAME} asset", file=sys.stderr)
        return None

    # Use the api download URL with auth so private/draft work too; for public
    # releases the browser_download_url is fine and is what Emby fetches.
    source_url = dll_asset.get("browser_download_url")
    api_url = dll_asset.get("url")
    if not source_url or not api_url:
        return None

    print(f"  hashing {tag} ...", file=sys.stderr)
    md5 = md5_of_url(api_url, token)

    return {
        "version": to_emby_version(major, minor, patch, pre),
        "changelog": (release.get("body") or "").strip(),
        "targetAbi": TARGET_ABI,
        "sourceUrl": source_url,
        "checksum": md5,
        "timestamp": release.get("published_at") or release.get("created_at"),
        "_semver": tag.lstrip("v"),
        "_prerelease": bool(release.get("prerelease")),
    }


def build_manifest(versions: list[dict]) -> list[dict]:
    # Strip internal underscore keys before serializing.
    clean = []
    for v in versions:
        clean.append({k: val for k, val in v.items() if not k.startswith("_")})
    return [
        {
            "guid": PLUGIN_GUID,
            "name": PLUGIN_NAME,
            "description": PLUGIN_DESCRIPTION,
            "overview": PLUGIN_OVERVIEW,
            "owner": PLUGIN_OWNER,
            "category": PLUGIN_CATEGORY,
            "imageUrl": PLUGIN_IMAGE_URL,
            "versions": clean,
        }
    ]


def partition_versions(entries: list[dict]) -> tuple[list[dict], list[dict]]:
    stable = [entry for entry in entries if not entry["_prerelease"]]
    return stable, list(entries)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "Serph91P/m3u-editor-for-emby"))
    ap.add_argument("--out", default=".", help="output directory")
    args = ap.parse_args()

    token = os.environ.get("GITHUB_TOKEN")
    if not token:
        print("ERROR: GITHUB_TOKEN env var required", file=sys.stderr)
        return 2

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"Fetching releases for {args.repo} ...", file=sys.stderr)
    releases = fetch_releases(args.repo, token)
    # Skip drafts.
    releases = [r for r in releases if not r.get("draft")]
    print(f"  {len(releases)} non-draft releases", file=sys.stderr)

    entries: list[dict] = []
    for r in releases:
        e = build_version_entry(r, token)
        if e:
            entries.append(e)

    # Sort newest first by published_at.
    entries.sort(key=lambda e: e.get("timestamp") or "", reverse=True)

    stable, all_entries = partition_versions(entries)

    stable_path = out_dir / "manifest.json"
    beta_path = out_dir / "manifest-beta.json"

    stable_path.write_text(json.dumps(build_manifest(stable), indent=2) + "\n")
    beta_path.write_text(json.dumps(build_manifest(all_entries), indent=2) + "\n")

    print(f"Wrote {stable_path} ({len(stable)} versions)", file=sys.stderr)
    print(f"Wrote {beta_path} ({len(all_entries)} versions)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
