# Contributing to m3u-editor for Emby

## Architecture

### Emby DI and SimpleInjector

Emby's `ApplicationHost.CreateInstanceSafe` scans the plugin assembly and
auto-registers public classes whose constructors match known DI types. These
classes can be instantiated before the `Plugin` constructor runs.

Consequently, `Plugin.Instance` may be null and plugin configuration paths may
not be initialized during service construction. Never access
`Plugin.Instance`, `Plugin.Instance.Configuration`, or configuration-backed
paths from a service constructor. Defer that work to runtime methods.

### Persistent Configuration

Emby serializes `PluginConfiguration` to XML. Use it only for durable settings
and state that must survive restarts. Keep transient connection, probe, cache,
and managed-action state in the service responsible for it.

### Direct Live TV Path

The tuner consumes the configured Xtream-compatible m3u-editor output directly.
Changes to channel identity must preserve a stable mapping between Emby's tuner
channel ID and the upstream stream ID so guide and playback requests resolve the
same channel.

When m3u-editor supplies `stream_stats`, pass the available media metadata to
Emby and bypass redundant probing only for covered streams. Streams without
complete metadata must retain Emby's normal probe path.

### Managed Publishing

Managed publishing is capability-gated and owned by m3u-editor. Preserve these
boundaries when changing the integration:

- Treat setup and advertised catalogs as versioned backend contracts.
- Write only beneath approved, confined local roots.
- Apply a complete generation atomically; never mutate an active generation in
  place.
- Keep the previous plugin-owned generation available for rollback.
- Report mapping results to m3u-editor and restore the previous generation when
  a required callback fails.
- Never delete files that are not recorded as plugin-owned.

The Emby configuration page is an operations surface for readiness, reconcile,
rollback, logs, updates, and direct Live TV settings. Publishing setup remains
in m3u-editor.

### Empty Guide Grid

If the Emby guide has data but displays no channels, check browser local storage
for a stale `guide-tagids` filter. Clear it from the guide filter UI or run:

```js
localStorage.removeItem('guide-tagids');
```

## Development Workflow

### Stable and beta releases

After a stable promotion from `develop` to `main`, merge `main` back into
`develop` before accepting further beta-release work. This keeps the beta
numeric core ahead of the stable updater version. The release pipeline refuses
a beta whose core is not strictly greater than the latest stable `main` tag;
the failure message directs the required back-sync. There is deliberately no
automation that opens or merges this back-sync PR: it remains a reviewed branch
operation.

Keep unrelated changes on separate short-lived branches. Do not carry an
uncommitted change into unrelated work; commit a work-in-progress snapshot or
stash it first.

### Build

Requires .NET SDK 10.0 and its .NET 10 test host for the mandatory `net10.0` tests.
The helper restores dependencies, runs the real test suite, and publishes only
if tests pass. The plugin still targets `netstandard2.0`; this contributor SDK
requirement does not change the supported Emby host runtime.

```bash
cd Emby.M3uEditor.Plugin
bash build.sh
```

Output: `Emby.M3uEditor.Plugin/out/Emby.M3uEditor.Plugin.dll`

### Release tooling safeguards (repository root)

Use Node.js 22 (CI pins 22.23.2) and pnpm 10.34.9 as selected by
`package.json`'s `packageManager`. CI uses the pinned official pnpm setup
action before Node's pnpm-store cache. The root release tooling uses only
`pnpm-lock.yaml`, not an npm lockfile.

```bash
pnpm install --frozen-lockfile --ignore-scripts
pnpm run audit:release
npm --prefix tests/e2e ci --ignore-scripts
python3 -m unittest discover -s scripts/release/tests
node scripts/release/tests/release-toolchain-smoke.mjs
```

The separate `tests/e2e` npm project supplies the offline TypeScript reporter
runner; these fixture tests do not contact Emby or run browser benchmarks.
Release safeguards also build fresh disposable linked Git worktrees and verify
that an injected real test failure prevents DLL publication. The release loader
smoke invokes no prepare or publish hooks. See
[`scripts/release/SECURITY.md`](scripts/release/SECURITY.md) for audit policy.

Successful CI releases explicitly call the reusable manifest workflow with the
same source SHA. Manual manifest dispatch and human release events remain
available; token-generated release events alone are not a continuation.
