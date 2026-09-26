#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_EVENT_NAME:?GITHUB_EVENT_NAME is required}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
: "${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
: "${GITHUB_REF:?GITHUB_REF is required}"
: "${GITHUB_SHA:?GITHUB_SHA is required}"
: "${RELEASE_BUILD_RESULT:?RELEASE_BUILD_RESULT is required}"
: "${RELEASE_CODEQL_RESULT:?RELEASE_CODEQL_RESULT is required}"

test "$RELEASE_BUILD_RESULT" = success
test "$RELEASE_CODEQL_RESULT" = success

test "$GITHUB_EVENT_NAME" = push
test "$GITHUB_REPOSITORY" = Serph91P/m3u-editor-for-emby
case "$GITHUB_REF_NAME" in main|develop) ;; *) exit 1 ;; esac
test "$GITHUB_REF" = "refs/heads/$GITHUB_REF_NAME"

# Resolve the live origin ref immediately before publishing. Do not permit a
# test-only environment override to bypass the production race check.
git fetch origin "$GITHUB_REF_NAME" >/dev/null
remote_sha="$(git rev-parse "origin/$GITHUB_REF_NAME")"
test "$remote_sha" = "$GITHUB_SHA"
