# Interrupted managed publication

Managed publication writes `.m3u-editor-for-emby/transition.json` atomically before moving any active or previous generation files. The record binds the mapping, canonical output root, staging/backup directory names and all three generation manifests. It contains hashes and file identities, not playback URLs.

Before the next publish (including reconcile) or rollback, under the same output-root gate:

- An interrupted pre-commit publication restores the former active and previous generations.
- A fully committed active manifest keeps the new generation and finishes validated cleanup.
- Recovery phases are persisted before their corresponding file changes. A further process exit during recovery or cleanup can be retried.
- Mapping/root/path/manifest mismatches, changed file hashes, unknown files/directories and symbolic links in recovery evidence fail closed. Evidence remains for diagnosis; do not repair metadata manually to resume publishing.
- Cleanup deletes only manifest-listed, hash-validated files and then empty owned directories. The journal is removed last. Roots without a journal retain the existing behavior.

The plugin metadata directory is local durable ownership evidence, not an authenticated boundary against another process with permission to rewrite all plugin metadata. Exclusive access by the configured plugin remains required.

## Regression checks

```sh
dotnet test Emby.M3uEditor.Plugin.Tests/Emby.M3uEditor.Plugin.Tests.csproj -c Release --filter FullyQualifiedName~ManagedCrashRecoveryTests
```

The test assembly is also the child-process harness. For an **empty disposable directory** supplied as `$FIXTURE` after building the test project:

```sh
HARNESS=Emby.M3uEditor.Plugin.Tests/bin/Release/net10.0/Emby.M3uEditor.Plugin.Tests.dll
dotnet "$HARNESS" setup "$FIXTURE"
dotnet "$HARNESS" crash "$FIXTURE" after-quarantine # expected exit 73
dotnet "$HARNESS" reconcile "$FIXTURE"
dotnet "$HARNESS" rollback "$FIXTURE"
```

Fixtures publish synthetic A/B/C generations, include added/removed paths and an unrelated sentinel, and verify public/previous file inventories, byte lengths and SHA-256 hashes. Tests also cover per-file quarantine/publish, previous backup, manifest commit, repeated restart, recovery interruption, corrupt metadata and foreign/reparse paths. Reconcile uses the real service with a synthetic HTTP handler, not a live editor or Emby server.

These are process-exit tests on Linux, not Windows certification or a power-loss/fsync guarantee. Existing abrupt termination during rollback itself is outside this publish-recovery change.
