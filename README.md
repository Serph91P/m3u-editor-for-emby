<p align="center">
  <img src="logo.svg" width="180" alt="m3u-editor for Emby" />
</p>

<h1 align="center">m3u-editor for Emby</h1>

<p align="center">
  The deeply integrated Emby Server companion for <a href="https://github.com/m3ue/m3u-editor">m3u-editor</a>, bringing its Live TV, EPG, movies, series, metadata, and publishing workflows into Emby.
</p>

<p align="center">
  <a href="https://github.com/m3ue/m3u-editor"><strong>m3u-editor</strong></a> |
  <a href="https://github.com/m3ue/m3u-proxy"><strong>m3u-proxy</strong></a> |
  <a href="https://github.com/m3ue/m3u-tv"><strong>m3u-tv</strong></a> |
  <a href="https://github.com/m3ue/m3u-editor-docs-v2"><strong>Documentation</strong></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Emby-4.8%2B-52B54B?style=flat-square&logo=emby" alt="Emby 4.8+" />
  <img src="https://img.shields.io/badge/.NET-Standard%202.0-512BD4?style=flat-square" alt=".NET Standard 2.0" />
  <img src="https://img.shields.io/badge/License-MIT-blue?style=flat-square" alt="MIT License" />
</p>

This plugin connects Emby Server to the Xtream API output produced by
[m3u-editor](https://github.com/m3ue/m3u-editor). It detects m3u-editor,
imports its Live TV and EPG data, builds Emby movie and series libraries, and
uses m3u-editor stream probe metadata when available. Other Xtream-compatible
servers remain supported as a secondary compatibility path.

The linked [m3u-proxy](https://github.com/m3ue/m3u-proxy) and
[m3u-tv](https://github.com/m3ue/m3u-tv) projects are part of the wider m3ue
ecosystem. This Emby plugin does not require or directly consume either one.

---

## m3u-editor Integration

### Xtream API Output

The plugin uses m3u-editor's Xtream-compatible protocol to consume the content
that m3u-editor prepares for clients:

- Live TV channels, groups, stream URLs, and XMLTV EPG data
- Movie and series categories
- VOD movie streams and series episode details
- Provider IDs and metadata used to organize Emby libraries
- Stream probe metadata exposed by m3u-editor in `stream_stats`

The plugin identifies m3u-editor from its URL and Xtream API response markers.
If m3u-editor supplies codec, resolution, bitrate, and related probe data, the
plugin passes that information to Emby and bypasses Emby's FFprobe step for the
covered channels. Channels without probe data continue through Emby's standard
analysis path.

### Managed Publishing Capability

When the configured compatible m3u-editor backend explicitly advertises the
managed library publishing v1 capability, the plugin can reconcile an
m3u-editor-provided catalog directly into Emby library folders. This path
supports:

- m3u-editor library mappings with admin-approved local output roots
- Preferred sources and failover sources for each media variant
- Local STRM and NFO generation
- Revisioned generations with an active and previous revision
- Result callbacks to m3u-editor after each mapping is applied
- Automatic restoration of the prior generation when a callback fails
- Manual rollback to the previous generation
- Optional Emby library refresh after a successful generation change

Managed publishing is capability-gated. It is not assumed to be available in
every public m3u-editor release. When the backend does not advertise the
required v1 actions and features, the plugin keeps using its standard Xtream
library sync path.

## Features

### Live TV and EPG

Bring m3u-editor's channel output into Emby's native Live TV experience.

- **Managed tuner registration** with no separate Emby M3U tuner required
- **M3U playlist generation** with channel metadata, logos, and EPG channel IDs
- **XMLTV electronic program guide** with a configurable 1 to 14 day window
- **Category filtering** for m3u-editor channel groups
- **Stream format selection** for MPEG-TS or HLS (`m3u8`)
- **Adult content filtering** for adult-flagged channels
- **Automatic caching** for M3U and EPG responses

### Movie Library

Sync m3u-editor VOD output as STRM files that Emby treats as a movie library.

- **Single folder mode** for one `Movies/` directory
- **Multiple folder mode** with categories assigned to custom folders
- **TMDB folder naming** such as `[tmdbid=123]` for Emby matching
- **Metadata fallback lookup** through Emby's providers when m3u-editor output
  does not contain a TMDB ID
- **Category selection** for specific m3u-editor VOD groups or all groups
- **Orphan cleanup** for content no longer present in the selected output

### Series Library

Create native Emby season and episode structures from m3u-editor series data.

- **Season and episode STRM files** using Emby-friendly folder layouts
- **Series detail fetching** through the Xtream API protocol
- **TVDb and TMDB folder naming** for reliable metadata matching
- **Manual ID overrides** for series that do not match automatically
- **Single or multiple folder modes** matching the movie workflow

### Sync Engine

- **Smart skip** for unchanged STRM files
- **Configurable parallelism** from 1 to 10 concurrent operations
- **Orphan cleanup** for files removed from the selected m3u-editor output
- **Cross-listing deduplication** across categories
- **Content name cleaning** for provider prefixes and custom terms
- **Real-time progress** with completed, skipped, and failed counters

### Built-in Dashboard

The configuration UI is embedded in Emby's plugin settings.

- **Dashboard** with backend detection, sync history, library statistics, and
  managed publishing status
- **Settings** for the m3u-editor connection, sync behavior, name cleaning, and
  metadata matching
- **Movies and Series** tabs for category selection, folder mapping, and sync
- **Live TV** controls for stream format, EPG, catch-up, and category filtering
- **Managed publishing** controls for approved roots, reconcile, and rollback

### Optional Dispatcharr Integration

[Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) remains an optional,
separate integration for stream proxying and connection management.

- Routes Live TV, movie, and series playback through Dispatcharr when enabled
- Uses Dispatcharr stream statistics for codec, resolution, and bitrate when
  available
- Supports FFprobe bypass when Dispatcharr has per-stream probe data
- Refreshes JWT authentication automatically
- Falls back to the configured m3u-editor Xtream URLs if Dispatcharr is
  unavailable

Dispatcharr requires
[Streamflow](https://github.com/krinkuto11/streamflow) to generate the
per-channel metadata used by its FFprobe bypass path. This is separate from
probe metadata supplied directly by m3u-editor.

---

## Installation

The first installation must be completed manually because the plugin is not yet
available inside Emby. After the first installation and restart, use the
plugin's built-in updater for ordinary updates.

The manual procedure follows Emby's
[official manual plugin installation instructions](https://emby.media/support/articles/Plugins-Duplicate.html#manual-install-of-plugins).

### First Installation

1. Download `Emby.M3uEditor.Plugin.dll` or the versioned ZIP from the
   [latest release](https://github.com/Serph91P/m3u-editor-for-emby/releases/latest).
2. Stop Emby Server.
3. Locate the `plugins` directory directly below the
   [Emby Server Data Folder](https://emby.media/support/articles/Server-Data-Folder.html).
4. If `Emby.M3uEditor.Plugin.dll` already exists, rename it, for example to
   `Emby.M3uEditor.Plugin.dll.old`. Do not overwrite a loaded plugin DLL.
5. If you downloaded an archive, extract it and locate the single
   `Emby.M3uEditor.Plugin.dll` file. No other files are required.
6. Copy the new `Emby.M3uEditor.Plugin.dll` directly into the `plugins`
   directory.
7. Start Emby Server.

On Linux and NAS platforms, you may need to make the DLL ownership and
permissions match the adjacent plugin DLLs.

For Docker installations, the Emby Server Data Folder is commonly mounted at
`/config`, making the destination `/config/plugins/`. Always confirm the data
folder used by your installation.

<details>
<summary><strong>Build from source (alternative)</strong></summary>

Requires .NET SDK 6.0+:

```bash
git clone https://github.com/Serph91P/m3u-editor-for-emby.git
cd m3u-editor-for-emby/Emby.M3uEditor.Plugin
bash build.sh
```

The compiled DLL will be at `Emby.M3uEditor.Plugin/out/Emby.M3uEditor.Plugin.dll`.

</details>

## Connect m3u-editor

### Prepare the Xtream API Output

1. In m3u-editor, create a **Playlist Auth** and assign it to the playlist you
   want Emby to use.
2. Open that playlist's Xtream API information in m3u-editor.
3. Copy the Xtream API base URL and the assigned Playlist Auth username and
   password. Use the base URL without `/player_api.php` and without a trailing
   slash.

The plugin communicates with m3u-editor through the Xtream-compatible protocol.
Playlist Auth credentials restrict the connection to the playlists assigned to
that auth in m3u-editor.

### Configure the Plugin

1. Open Emby's web UI.
2. Open **Server Dashboard > Plugins > My Plugins**.
3. Open the menu for **m3u-editor for Emby** and select **Settings**.
4. Under **Xtream Connection**, enter the m3u-editor details:
   - **Server URL**: the m3u-editor Xtream API base URL
   - **Username**: the Playlist Auth username
   - **Password**: the Playlist Auth password
5. Select **Test Connection** and confirm that m3u-editor is detected.
6. Select **Save**.

Use HTTPS when available. Managed publishing requires a confined HTTPS origin.

### Set Up Live TV

1. Switch to the **Live TV** tab.
2. Choose the stream format. MPEG-TS is recommended.
3. Select **Refresh Categories** to load m3u-editor channel groups.
4. Select the categories to include.
5. Configure the EPG window and cache duration.
6. Select **Save**.
7. Go to **Server Dashboard > Live TV** and verify the automatically registered
   **m3u-editor for Emby** tuner. The plugin manages this tuner, so do not add a
   separate M3U tuner.

### Set Up Movies (Optional)

1. Switch to the **Movies** tab and enable VOD movies.
2. Refresh the m3u-editor VOD categories and select the categories to include.
3. Choose **Single Folder** or define category mappings with **Multiple
   Folders**.
4. Select **Sync Movies Now**.
5. In Emby, add a Movies library pointing to the STRM output path. The default
   movie path is `/config/m3u-editor-for-emby/Movies`.

### Set Up Series (Optional)

1. Switch to the **Series** tab and enable series.
2. Refresh the m3u-editor series categories and select the categories to
   include.
3. Choose the folder organization mode and select **Sync Series Now**.
4. In Emby, add a TV Shows library pointing to
   `/config/m3u-editor-for-emby/Shows` by default.

### Managed Publishing (When Available)

If the dashboard reports a compatible managed publishing v1 backend:

1. Add each local path that m3u-editor may manage to **Approved managed output
   roots**.
2. Make sure a managed root does not overlap an enabled standard movie or
   series sync root.
3. Select **Reconcile Now** to apply the advertised m3u-editor mappings.
4. Add the resulting folders as Emby libraries if they are not already
   configured.
5. Use **Rollback Previous Generation** only when you need to restore the prior
   managed revision.

If the capability is not advertised, continue with the standard movie and
series setup above.

### Dispatcharr Integration (Optional)

1. Open the **Live TV** tab and expand **Dispatcharr Integration**.
2. Enable Dispatcharr.
3. Enter the Dispatcharr URL, username, and password.
4. Select **Test Dispatcharr** and save the configuration.

Live TV, movie, and series streams will route through Dispatcharr while it is
available. The primary content connection remains the m3u-editor Xtream API
output configured above.

### Updating the Plugin

For ordinary updates after the first installation, open **m3u-editor for Emby**
in Emby's plugin settings. When the dashboard shows an update banner, select
**Update Now**, then restart Emby Server when prompted. The built-in updater
preserves the loaded DLL path and existing configuration, including configured
STRM output paths.

Use manual replacement only when the built-in updater is unavailable or fails:

1. Download the latest release.
2. Stop Emby Server.
3. In the `plugins` directory directly below the Emby Server Data Folder,
   rename the installed DLL to `Emby.M3uEditor.Plugin.dll.old` instead of
   overwriting it.
4. If the download is an archive, extract it and locate the single
   `Emby.M3uEditor.Plugin.dll`.
5. Copy the new `Emby.M3uEditor.Plugin.dll` directly into that directory.
6. Start Emby Server.

On Linux and NAS platforms, make ownership and permissions match the adjacent
plugin DLLs if necessary.

---

## Configuration Reference

| Setting | Default | Description |
|---|---|---|
| **Stream Format** | MPEG-TS | Live TV container format (`ts` or `m3u8`) |
| **EPG Cache** | 30 min | How long to cache m3u-editor EPG data (5 to 1440 min) |
| **EPG Days** | 2 | Days of guide data to fetch (1 to 14) |
| **M3U Cache** | 15 min | How long to cache generated channel playlists (1 to 1440 min) |
| **STRM Library Path** | `/config/m3u-editor-for-emby` | Standard movie and series STRM output root. Existing paths are preserved during upgrades. |
| **Smart Skip** | On | Skip unchanged STRM files during standard sync |
| **Sync Parallelism** | 3 | Concurrent operations during standard sync (1 to 10) |
| **Cleanup Orphans** | Off | Remove STRM files no longer present in the selected m3u-editor output |
| **TMDB Folder Naming** | Off | Append provider IDs to movie and series folders |
| **Fallback Lookup** | Off | Query Emby's metadata providers for missing IDs |
| **Name Cleaning** | Off | Strip provider prefixes and custom terms from titles |
| **Approved managed output roots** | Empty | Local roots that a compatible managed publishing backend may write |

---

## m3ue Ecosystem

- [m3u-editor](https://github.com/m3ue/m3u-editor): IPTV playlist, EPG, VOD,
  series, and Xtream API output management
- [m3u-proxy](https://github.com/m3ue/m3u-proxy): streaming proxy and failover
  service used across the wider ecosystem
- [m3u-tv](https://github.com/m3ue/m3u-tv): cross-platform TV client for
  m3u-editor
- [m3u-editor documentation](https://github.com/m3ue/m3u-editor-docs-v2):
  setup and feature documentation for m3u-editor and m3u-proxy

## License

The plugin source is licensed under the MIT License.

The project logo is from
[`m3ue/m3u-editor`'s `public/logo.svg`](https://github.com/m3ue/m3u-editor/blob/master/public/logo.svg)
and is licensed under
[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).
