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

Keep unrelated changes on separate short-lived branches. Do not carry an
uncommitted change into unrelated work; commit a work-in-progress snapshot or
stash it first.

### Build

Requires .NET SDK 6.0 or newer:

```bash
cd Emby.M3uEditor.Plugin
bash build.sh
```

Output: `Emby.M3uEditor.Plugin/out/Emby.M3uEditor.Plugin.dll`
