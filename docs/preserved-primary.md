# Optional Enricher primary protection

The current m3u-editor programme cache owns the programme and its image URLs. The EPG Enricher alone decides whether to preserve an existing primary image. Emby consumes a validated, optional projection; it does not query TMDB, read a server database, or infer preservation from a still/backdrop role.

## Compatibility and normal use

This transport requires the generic playlist-query hook from [Core PR1829](https://github.com/m3ue/m3u-editor/pull/1829) and `preserved_primary_v1` from [Enricher PR71](https://github.com/Serph91P/m3u-editor-epg-enricher/pull/71). The tested server candidates are Core `bb0c19a3da30cade88e41ed4c2e57017c581512c` and Enricher `c595a39b09ce815465a31a8128272639ba5e3e8c`. These candidate tests do not imply that the hook has merged into upstream `dev` or that a live installation has been updated.

Use the existing Xtream server URL and playlist credentials. No new Emby setting, API key, guide mode, or version-string detection is needed. Only **Xtream Server** guide mode queries the Enricher. **Custom URL** and **Disabled** issue zero projection requests, and no playlist credentials are sent to a custom guide host by this feature.

1. Run the usual EPG sync and Enricher workflow in m3u-editor. The Enricher's existing overwrite settings determine preservation; Emby cannot override them.
2. In Emby, use **Dashboard → Live TV → Guide Data** to refresh the guide. A configuration change or explicit cache invalidation also discards projection state.
3. A deliberately retained primary stays Emby's `ImageUrl`; poster, backdrop and episode still remain separate. Unknown or contradictory dimensions are `0/0`, never borrowed from a different poster URL.

A missing, old, disabled or untrusted Enricher, unsupported hook, HTTP failure, timeout or invalid/incomplete response leaves the normal guide usable. An unavailable capability is remembered only within the current guide scope; the next normal cache refresh can discover a newly available Enricher. Old JSON `preserved_primary_url` and XMLTV processing instructions remain readable. A valid new entry cannot replace a conflicting legacy marker; a generic conflict warning contains no URLs or credentials.

If an updated DLL is not loaded, check Emby's plugin list and startup log for the loaded version and load errors. Replacing a DLL without restarting Emby does not replace an assembly already loaded in its process. Have the operator perform the normal approved plugin update/restart, confirm the loaded version, then refresh the guide. Do not bypass Emby's runtime compatibility checks or repeatedly copy DLLs into a running process.

## Wire and cache boundaries

Requests are form-encoded **POST** to `{BaseUrl}/player_api.php`, with existing `username`/`password` in the body only, `action=plugin_query`, `plugin_id=epg-enricher`, `query=preserved_primary_v1`, and a JSON `payload`. The payload includes selected positive stream IDs, the actual guide window's UNIX start/stop, `format=xmltv|json`, and an optional opaque cursor. Neither credentials nor response URLs are logged by this client.

Stream groups contain at most 100 IDs; windows are adjacent slices of at most 14 days. Each group/window gets one start request, followed only by its cursor pages. Pages must contain at most 256 entries and 512 KiB including the envelope. Encoded requests are limited to 128 KiB. Every required metadata field must be present, even if explicitly null or zero. Envelope version/plugin/query, stream/window scope, stable snapshot ID/total, unique rows, cursor progression and exact final count are checked. One failure discards the entire new projection, including previously fetched batches. A programme crossing adjacent windows may repeat between snapshots; only identical repeats are coalesced.

Concurrent channel readers share one successful or unavailable projection per configuration/guide scope, window and format. Loading occurs outside the per-channel cache state lock. Cancelling a waiting reader does not cancel the active loader. Cancelling the loader propagates cancellation and does not cache a partial result; a remaining reader can retry. Both `_cacheGeneration` and immutable guide-scope identity fence publication, including cache expiry and in-flight credential changes. Only protected entries are retained as extra metadata, not another full guide.

Overlay matching uses exact stream ID, exported start/stop, decoded title, and all available artwork evidence. XMLTV additionally compares subtitle and the **first normal icon**. Retaining that icon is evidence only, not automatic protection. JSON normally has neither subtitle nor the original primary icon: those query fields must be null; the server-validated `primary.url` supplies the otherwise unavailable scalar. JSON titles use the existing guide Base64 decoder, XMLTV titles are already text. IDs/URLs are not normalized, proxy URLs are not decoded, and no time-shift/zone correction is performed in Emby: the server returns final wire timestamps. Contradictory candidate tuples abstain. Successful overlays copy programme instances so streams sharing XMLTV objects cannot overwrite each other's artwork or metadata.

## Verification and fixture provenance

`Emby.M3uEditor.Plugin.Tests/Fixtures/preserved-primary-v1.json` is the byte-identical native producer fixture from Enricher `c595a39` (SHA256 `1258443e7c2ae6a180ad270918da3d3e2c9e81a5a4196f9d900ddc07e57ff726`). It contains actual synthetic Core XMLTV/JSON exports and Enricher projection entries, without legacy markers. Its producer is `tests/native/PreservedPrimaryExportTest.php`, using the real authenticated controller → manager → plugin chain. It can be regenerated with `ENRICHER_CONTRACT_OUTPUT=<file>` and the Enricher's `scripts/test-native.sh --filter=PreservedPrimaryExport`, against the isolated reviewed Core host.

`preserved-primary-variants-v1.json` additionally captures eight real native `PreservedPrimaryGeometryTest` scenarios: direct/proxy, +2/-3 hour shifts across midnight, standard/custom/merged/alias/custom-alias output scopes. Generated in an isolated source archive of that same Enricher commit, with an output-only addition after the existing export assertions: validate/trust the synthetic fixture plugin with the native manager, issue both real authenticated POST queries using its assigned synthetic PlaylistAuth, assert `data.total=1`, and save the actual XMLTV, JSON and response envelopes. No parent worktree or producer production file was changed. Opaque snapshot IDs are fixture data, not secrets or identity keys. The producer run passed 9 tests / 248 assertions; the original fixture regenerated byte-identically. Consumer expectations independently assert known image roles, geometry, exact proxy URLs and raw-time-plus-hours timestamps rather than copying expected values from the query.

Native consumer tests exercise the real parser/deserializer → client → overlay → `BuildProgramInfo` path, failure/paging/cancellation/generation boundaries and 101 streams × 15 days with 257 protected records (four disjoint query starts plus one cursor request). This is bounded synthetic evidence, not a claim about 220,000 real programmes or a provider benchmark.

Run the repository checks:

```sh
dotnet restore Emby.M3uEditor.Plugin.Tests/Emby.M3uEditor.Plugin.Tests.csproj
dotnet build -c Release Emby.M3uEditor.Plugin/Emby.M3uEditor.Plugin.csproj
dotnet test -c Release Emby.M3uEditor.Plugin.Tests/Emby.M3uEditor.Plugin.Tests.csproj
bash scripts/test-emby-runtime-compatibility.sh
python3 -m unittest discover -s scripts/release/tests
git diff --check
```

The runtime script requires the documented .NET 8.0.28 host and checksum-pinned Emby binaries. Release safeguards require Node/pnpm and reporter dependencies from the existing CI lockfiles. Hosted CI runs the native .NET tests, runtime compatibility and release safeguards with those declared toolchains; no tests are opt-in or skipped for this feature. Linux native ABI tests are not Windows/Android/macOS execution or a visual/live Emby acceptance. Combined live installation, manifest permissions/re-trust, guide refresh and visual acceptance remain a separate owner-operated step requiring the appropriate deployment approval.
