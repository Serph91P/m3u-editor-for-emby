# Restart-safe GitHub releases

CI runs **`node scripts/release/release.mjs`** for both first publication and recovery.
Do not retry publication with the native semantic-release CLI or delete an orphan tag.
`.releaserc.json` defaults that CLI to dry-run: it remains the version/notes/floor,
prepare-command and asset-definition contract, not a second publication pipeline.
The controller calls the locked semantic-release JS API with `dryRun: true` in a
disposable local Git snapshot. Only that snapshot omits a candidate tag; production
refs and `GITHUB_SHA` are never rewritten. No dependencies are patched.

## Required authority and source

The existing trusted `push`-only `main`/`develop` job, successful Build & Test and
CodeQL dependencies, origin identity and live branch/SHA guards remain mandatory.
`GITHUB_TOKEN` supplies REST and ephemeral URL-scoped Git authentication; checkout
credentials stay unpersisted. No token is placed in a Git URL, file or command-line
argument. Git redirects are refused. The release job explicitly has `actions: read`
for historical workflow/run/job verification as well as its existing write rights.

An interrupted ancestor's artifacts are built from its exact tag source in a clean
checkout, not the newer controller commit. Its source must have a completed same-repo,
non-fork **push** CI run with successful exact-SHA **Build & Test** and **CodeQL
Security Analysis** jobs. The old Release job (and therefore the overall run) may
have failed. Current controller CI gates are never impersonated for an old source.
Unprovable source history or unsupported historical configuration stops publication.

## Recovery boundary and states

For each native channel, the highest published, source-reachable version with
matching tag identity, native channel notes and a fully verified immutable bundle is
the recovery frontier. Its assets are checked before any successor tag, notes or
release write, even when a newer releasable commit exists; incomplete or conflicting
published assets stop the run and are never repaired. The full authenticated release
list is paginated, including drafts. Missing older releases
below that frontier are superseded historical gaps and remain untouched. Without a
frontier, all matching candidates are considered. A tag-only or notes-present
candidate **above** the frontier is reconciled before any new version; multiple
unresolved candidates stop without writes. There are no version-specific exceptions.

Notes use semantic-release v25's `refs/notes/semantic-release-<tag>` format. The
prerelease identifier `beta` is not a channel: native develop notes contain
`{"channels":["develop"]}`, stable notes `{"channels":[null]}`. Existing notes,
tags, release bodies and uploaded bytes are never overwritten or deleted.

The controller resumes these states:

1. Native new release plan: build and verify, then push the exact tag.
2. Tag without notes/release: reconstruct the same native version/source/channel,
   add only missing channel notes, independently read them back from the remote.
3. Notes without a release: create a draft and reconcile its assets.
4. Draft with correct partial uploads: verify existing assets, upload only missing
   ones, then publish and verify final state. An accepted write with a lost response
   (including HTTP 422) is resolved by readback, not blind duplication.
5. Fully published: verify all four assets and return success without release
   writes. Historical native release dates/bodies and branch target metadata are
   preserved. Success still permits the existing `update-manifests` workflow call
   with the **controller** `source_sha`; the generator reads published releases.
   Default `manifest.json` / `manifest-beta.json` paths are unchanged.

DLL/ZIP/SHA256/MD5 form one bundle. New builds normalize compiler paths and ZIP
metadata. An original non-reproducible ZIP is retained after checking its single DLL
payload and sidecars. A path-dependent original DLL is compared to the exact-source
rebuild without loading it: PE/CLR machine, runtime, flags, section permissions and
loader settings, managed metadata (including version/source), method bodies, static
data and resources must match. Debug-driven layout changes are accepted only with a
valid image size and the standard Roslyn AnyCPU CLR import/jump/relocation layout;
other native directories are refused. MVID/PDB differences are normalized, never
machine or runtime identity. Unsupported native/static layouts fail closed. Missing sidecars are
computed from the retained original bytes, not from a replacement ZIP.

Conflicting source/version/channel/notes, duplicate or corrupt assets, empty/starter
uploads and incomplete published releases stop safely. No automatic asset cleanup.
403, malformed responses, persistent 500s and timeouts never mean “not found”. Reads
have at most three attempts, with a 15-second request deadline. Creation sends **at
most one POST per invocation**. A successful response supplies only an ID hint:
its exact authenticated `GET /releases/{id}` must match the expected identity
before asset writes. The full paginated list still detects duplicates/conflicts;
a stale empty list does not override an independently verified exact ID.
Without a response ID (500, disconnect, timeout or 422), only up to three bounded
list readbacks may establish creation. Missing exact IDs are likewise read back
at most three times. If visibility cannot be proved, the invocation fails with
creation uncertainty and no uploads, rather than sending another create POST.
Upload/publish writes retain their existing three-attempt readback paths.
Source authority is rechecked before publication writes.

A later trusted push/re-run uses this same entrypoint and first reconciles visible
releases. There is no persistent creation journal or globally atomic POST
idempotency: after an ambiguous create, restarting while the server still hides
that draft in the list can create a duplicate. Establish visibility before retrying
an uncertain run; visible duplicate/conflicting drafts fail closed and are never
automatically selected, renamed or deleted. No dispatch/release-event workaround
or new account is needed.

## Offline verification

```sh
npx --yes pnpm@10.34.9 install --frozen-lockfile --ignore-scripts
npx --yes pnpm@10.34.9 run audit:release
npm --prefix tests/e2e ci --ignore-scripts
python3 -m unittest discover -s scripts/release/tests
# Focused native Git/HTTP/controller/build matrix:
node --test scripts/release/tests/release-recovery.mjs
```

The unittest driver gives the complete native Node matrix a bounded 600-second
budget for hosted-runner variation and real builds. This is not a release deadline:
HTTP requests still expire after 15 seconds, with at most three read attempts;
per-case deadlines, assertions and all source/CI/version/asset gates are unchanged.

Tests use disposable bare Git remotes, real native planning/builds, synthetic HTTP
Git/GitHub servers and synthetic credentials. They verify durable refs/assets and
zero-write conflict boundaries. They do not publish to GitHub, update live manifests,
merge, deploy or substitute for the owner's final live-release acceptance.
