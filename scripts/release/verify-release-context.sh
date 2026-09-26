#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_EVENT_NAME:?GITHUB_EVENT_NAME is required}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
: "${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
: "${GITHUB_REF:?GITHUB_REF is required}"
: "${GITHUB_SHA:?GITHUB_SHA is required}"

test "$GITHUB_EVENT_NAME" = push
test "$GITHUB_REPOSITORY" = Serph91P/m3u-editor-for-emby
case "$GITHUB_REF_NAME" in main|develop) ;; *) exit 1 ;; esac
test "$GITHUB_REF" = "refs/heads/$GITHUB_REF_NAME"

# The workflow fetches the branch immediately before this script. Tests may
# provide RELEASE_REMOTE_SHA; production resolves the live origin ref.
remote_sha="${RELEASE_REMOTE_SHA:-$(git fetch origin "$GITHUB_REF_NAME" >/dev/null && git rev-parse "origin/$GITHUB_REF_NAME")}"
test "$remote_sha" = "$GITHUB_SHA"