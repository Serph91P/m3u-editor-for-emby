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
the plugin, never manually suffixed IDs or a replacement registry. The 4.10.2.0
load context is non-collectible because `DispatchProxy` needs a non-collectible
logger interface; the command exits after the isolated runtime matrix.

Assertions cover both stop orders, rapid same-session restart and late old stop,
probe/playback memberships, repeated registrations and excess removals, null/empty
IDs, last-stop cleanup/disposal, reopen after cleanup and different channels.
An HTTP handler rejects any network request and tracks final disposal.

These are offline ABI/registry checks, not PlaybackInfo, HLS, native TS, browser,
iOS, proxy-sharing or production acceptance. The probe/playback cases replay
consumer lifecycle transitions, not actual player requests. Those user paths
remain separate integration/device checks. `EnableStreamSharing` remains false;
upstream sharing is the backend/proxy's responsibility.
