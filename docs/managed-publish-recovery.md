# Interrupted managed publication and rollback

Managed operations write `.m3u-editor-for-emby/transition.json` atomically before creating staged output or moving generation files. The v1 record binds the mapping, canonical output root, operation identity, named staging/backup directories and generation manifests. Existing valid v1 publication journals retain their original shape and recovery behavior.

Before the next publish (including reconcile) or rollback, under the same output-root gate:

- `preparing` validates the unchanged active/previous pair, then removes only the journal's validated staged files. This includes initial publication, partially staged generations and complete staging before the publication commit journal phase.
- `publishing`/`restoring` restore the former active and previous generations. A fully committed active manifest keeps the new generation and finishes validated cleanup.
- `rollback-moving`/`rollback-restoring` restore the pair that existed before rollback. `rollback-swapping`, persisted after both file-move loops, completes the requested rollback's directory and manifest swap. The fresh caller then performs its own requested operation against that coherent pair; a new rollback is a new operation, not a duplicate of the interrupted request.
- Recovery phases are persisted before their corresponding file changes. A further process termination during recovery or cleanup can be retried.
- Mapping/root/path/manifest mismatches, changed file hashes, unknown files/directories and symbolic links in recovery evidence fail closed. Evidence remains for diagnosis; do not repair metadata manually to resume publishing.
- Cleanup deletes only manifest-listed, hash-validated files and then empty owned directories. The journal is removed last. A `staging-*` name alone never grants deletion rights; old unjournaled or foreign metadata remains unclaimed.

## Partial preparation writes

While preparing, one small `preparing-file-<operation-id>.json` intent records the current output's path and content before its temporary file is opened. The content must match the already journaled next manifest's hash and length (at most the unchanged 1 MiB per-file budget). Only that exact temporary path may contain an incomplete byte prefix of the validated content; completed staged files must match their full hashes. Recovery validates all evidence before deleting anything. The intent is replaced per output, not the entire generation manifest, and is removed before entering `publishing`.

The main transition journal contains hashes and file identities, not playback URLs. The temporary preparation intent contains one file's actual content and may therefore contain its playback URL, like the staged STRM itself. Keep the entire metadata directory private to the configured plugin; it is local durable ownership evidence, not authentication against a process that can rewrite all plugin metadata.

Caught exceptions use the same recovery path. Existing rollback compensation can restore known, validated files while preserving a foreign scratch sentinel without deleting it. Fresh-process recovery remains strict and does not grant that exception-compensation allowance to unknown restart state. Failed compensation returns failure and retains recovery evidence rather than reporting an invalid generation as healthy.

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

The `HardKill_*` tests use separate owned child processes running the real publication/rollback entry points. Each child emits a checkpoint with file inventories, lengths and SHA-256 hashes, then waits. The parent inspects the checkpoint and kills that child (SIGKILL/exit 137 on Linux), so no catch/finally compensation can run. All filesystem probes forbid HTTP; reconcile's older synthetic HTTP control is separate. Set `MANAGED_CRASH_EVIDENCE_DIR` to an owned external directory and `MANAGED_CRASH_SOURCE` to the tested source revision to retain JSON checkpoint, termination, restart exit/error and post-restart file/hash evidence.

The permanent matrix covers:

- Initial and repeated preparation interruption after complete staging, before the second staged rename, and during an actual 64 KiB partial write of a larger NFO; retries preserve the old pair and leave no accumulating owned staging copies.
- The second `rollback-active` and `rollback-previous` hooks, after at least one real file move; later file-completion, directory-swap and both manifest-swap boundaries.
- Interruptions while restoring rollback files and while completing rollback swaps; repeated fresh restart and identical-revision replay.
- Populated and empty generations, unchanged v1 publication journal shape, ordinary exceptions and callback compensation.
- Wrong root/mapping/manifest evidence, changed staged/partial/scratch bytes, foreign content, symlinks, modified preparation intents, and identical-content generations with a missing backup copy. Rejection preserves both files and directories, including unrelated sentinels.

Fixtures are synthetic and verify public/previous inventories, byte lengths and SHA-256 hashes independently of operation return codes. These Linux process-termination checks exercise the named boundaries, not installed Emby or Windows, arbitrary interrupted metadata writes, physical power loss or filesystem fsync guarantees. Publication and rollback retain the existing requirement for exclusive plugin access to a configured output root.
