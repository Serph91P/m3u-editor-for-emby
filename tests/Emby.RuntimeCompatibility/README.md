# Offline Emby runtime and Live TV lifecycle regression

Run from the repository root:

```sh
# Requires .NET 8.0.28 exactly; the SDK build still uses the installed SDK.
EMBY_RUNTIME_DOTNET_HOST=/path/to/dotnet bash scripts/test-emby-runtime-compatibility.sh
```

The script downloads official SDK/server/released-plugin artifacts from the URLs
in `artifacts.lock`, verifies SHA-256 before extraction and checks the Controller
DLL hashes. The 4.10.2.0 DEB hash matches its official GitHub release asset digest.
No proprietary assemblies are checked into this repository.

## Callback contract checked against Emby 4.10.2.0

The official server assemblies show this call path:

- `MediaSourceManager.OpenLiveStreamInternal` forwards `LiveStreamRequest.PlaySessionId`
  as the consumer ID without rejecting null or empty values.
- `LiveTvMediaSourceProvider.OpenMediaSource` forwards that ID through
  `LiveTvManager.GetChannelStream` to `EmbyTV.GetChannelStreamWithDirectStreamProvider`.
- `EmbyTV` calls `AddConsumer` once per successful open (also when reusing a
  shareable stream). It does not guarantee unique consumer IDs.
- The native `Emby.LiveTV.TunerHosts.LiveStream` increments/decrements a count per
  callback, not an idempotent set. The plugin therefore preserves multiple
  registrations for the same ID, including null/empty, but releases only a
  matching registration. IDs are not normalized or collapsed. A repeated removal
  after all registrations for that ID are gone has no effect on another ID.
- `MediaSourceManager.CloseLiveStream` invokes `RemoveConsumer`, retains the
  registry entry while the count is positive, and removes/closes it on the last
  stop. Server disposal has a separate explicit close-all path.

### PlaybackInfo replacement is not another open

The same official 4.10.2.0 assemblies contain a separate path:

- `MediaInfoService.Post(GetPostedPlaybackInfo)` with an existing `LiveStreamId`
  reads that registry source and returns a fresh `PlaySessionId`. It does **not**
  reopen the source or call `AddConsumer`.
- With `IsPlayback=true` and `CurrentPlaySessionId`, it removes the old native
  play session and registers its replacement against the unchanged live stream.
- `SessionManager.OnPlaybackStopped` passes the final native play-session ID to
  `CloseLiveStream`. That ID does not own the original open's registration.

`LiveTvPlaybackEntryPoint` correlates the selected per-open source and native
session, then releases that original registration through the supported
consumer-specific `IMediaSourceManager.CloseLiveStream` overload. This is shared
server lifecycle handling for **all clients**, not a Samsung, user-agent or
version workaround. Unknown stops without a native play session cannot acquire
ownership. Active original/replacement sessions and other registrations remain
protected; a completed release cannot be rebound by a delayed callback.
Observed replacement IDs are retained separately from open-consumer memberships
and checked against their own native `PlaySessionInfo.PlayState`. The last-session
`SessionInfo.PlayState` projection alone cannot prove that all replacements stopped.
Native replacement/removal prunes obsolete observed IDs; no timer or extra consumer
registration is introduced.

Negotiation before the first start callback (or a stop delivered before its queued
start) can bind only a single nonempty abandoned registration: it must have no
native original session or other current session using that selected source.
Ambiguous probe, duplicate or legacy-count ownership is not guessed. The 4.8/4.9
close-all-only API never substitutes for consumer-specific cleanup; the adapter
is inactive there and the original legacy counter lifecycle is unchanged.

The legacy writable `ConsumerCount` remains an absolute count override for old
runtimes. It does not grant unmatched callbacks ownership of legacy consumers;
registered callbacks and their count changes are synchronized with that property.
The property still cannot make external read/modify/write sequences atomic.

## Regression boundary

`M3uEditorTunerHostTests` invokes the actual discovery and open methods with
synthetic configuration and metadata. It checks stable discovery, original ID,
URL, headers and metadata, distinct opened IDs, close/reopen and another channel.

`RegistryLifecycleTests` uses the real plugin media-source factory, live-stream
constructor and deferred `Open`, then the official 4.10.2.0
`MediaSourceManager.AddOrUpdateStream`, `GetLiveStreamInfo` and `CloseLiveStream`.
The manager's in-memory state is initialized without starting the server. Only
the normal provider prefixes are derived in the fixture; opened IDs come from
the plugin, never manually suffixed IDs or a replacement registry. The runtime
load contexts are non-collectible because `DispatchProxy` needs non-collectible
interfaces; the command exits after the isolated runtime matrix. Each runtime
also constructs the lifecycle adapter and checks legacy gating and idempotent
Run/Dispose subscriptions.

Assertions cover both stop orders, rapid same-session restart and late old stop,
probe/playback memberships, repeated registrations and excess removals, null/empty
IDs, last-stop cleanup/disposal, reopen after cleanup and different channels.
An HTTP handler rejects any network request and tracks final disposal.

`PlaybackInfoLifecycleTests` invokes the official **POST route handler**, native
session replacement, real `SessionManager.OnPlaybackStopped` and the resulting
queued `PlaybackStopped` callback, with the real registry throughout. Peripheral
authorization/configuration/library services are synthetic stubs, not a running
authenticated HTTP server. The initial registration uses the existing factory,
deferred open, consumer callback and native AddOrUpdateStream fixture; the start
callback is supplied explicitly, or deliberately omitted to test queued order.
The mixed replacement/additional-session regression instead invokes the real
`SessionManager.OnPlaybackStart` and waits for its queued callback: O is replaced
by started R1, another POST without Current registers R2, and stopping R2 must keep
R1's stream and the full tuner limit occupied even though the last-session
projection is empty. Only R1's own final stop releases the registry entry/tuner
slot; both independent peers survive. Callback observers wait for native delivery,
not a fixed sleep.

The generic flow matrix covers a normal stop, one/three renegotiations after
start, three before start, stop without an earlier start callback, and negotiation
without `CurrentPlaySessionId` (which must retain the original native session
until its own stop). Each flow retains independent same-channel and other-channel
viewers, checks duplicate final stops and confirms final registry removal.
Replacement flows also reject old/unknown stops. These
are the shared normal/open-stop, probe/playback and negotiation contracts used by
Web, iOS, Android TV and unknown clients; this is **not** physical device emulation
or evidence that any particular app emits a given sequence.

`TunerCapacityTests` supplies the actual current registry snapshot to the official
`BaseTunerHost.GetChannelStream`: the fourth open fails with `TunerCount=3`, then
another channel opens via the real plugin after final-stop cleanup with the same
limit. Only channel discovery is prefilled; there is no replacement admission
manager, TTL, increased limit or forced close-all. The added ownership unit tests
cover concurrent/delayed claims and ambiguous/foreign native-session ownership.

The same harness can run a baseline plugin DLL as its first argument (the other
six arguments are identical to the script's invocation). The pre-fix normal
stop passes; the first renegotiated final stop fails with
`Renegotiated final stop left original consumer registered`.

These are offline native ABI/route/session/registry checks, not authenticated
HTTP, HLS, native TS transport, browser/iOS/Android TV player, proxy-sharing or
production acceptance. Probe/playback membership tests are not transport probes.
Those user paths remain separate integration/device checks.
`EnableStreamSharing` remains false; upstream sharing is the backend/proxy's
responsibility.
