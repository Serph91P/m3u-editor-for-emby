<p align="center">
  <img src="logo.svg" width="180" alt="m3u-editor for Emby" />
</p>

<h1 align="center">m3u-editor for Emby</h1>

<p align="center">
  An Emby Server plugin that turns any Xtream-compatible IPTV service into a full Live TV, Movies, and Series library - with EPG, metadata matching, and a built-in dashboard.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Emby-4.8%2B-52B54B?style=flat-square&logo=emby" alt="Emby 4.8+" />
  <img src="https://img.shields.io/badge/.NET-Standard%202.0-512BD4?style=flat-square" alt=".NET Standard 2.0" />
  <img src="https://img.shields.io/badge/License-MIT-blue?style=flat-square" alt="MIT License" />
</p>

---

## Features

### Live TV & EPG

Full Live TV integration with Emby's native TV guide.

- **M3U playlist generation** with channel metadata, logos, and EPG channel IDs
- **XMLTV electronic program guide** with configurable fetch window (1-14 days)
- **Category-based filtering** - select which channel groups to include
- **Stream format selection** - MPEG-TS or HLS (M3U8)
- **Adult content filtering** - opt-in toggle for adult-flagged channels
- **Automatic caching** - M3U (15 min) and EPG (30 min) with thread-safe invalidation

### VOD Movie Library

Sync on-demand movies as STRM files that Emby treats as a native movie library.

- **STRM file generation** - one file per movie, Emby handles metadata and artwork
- **Folder organization modes:**
  - **Single folder** - all movies in one `Movies/` directory
  - **Multiple folders** - define your own folders and assign categories to each
- **TMDB metadata matching** - appends `[tmdbid=123]` to folder names for instant Emby identification
- **TMDB fallback lookup** - queries Emby's metadata providers when the Xtream source lacks a TMDB ID
- **Category selection** - pick specific VOD categories to sync, or sync all
- **Danger zone** - one-click delete of all synced movie content

### TV Series Library

Full series support with proper season/episode structure.

- **Season/Episode STRM files** - `Show Name/Season 01/Show Name - S01E01 - Episode Title.strm`
- **Series detail fetching** - pulls episode lists per series from the Xtream API
- **TVDb / TMDB ID folder naming** - `Show Name [tvdbid=81189]` for reliable metadata matching
- **Manual ID overrides** - force a specific TVDb ID for shows that don't auto-match
- **Metadata fallback lookup** - searches Emby's providers when no ID is available
- **Same folder modes as movies** - Single Folder or Multiple Folders

### Smart Sync Engine

Efficient sync that doesn't re-download what you already have.

- **Smart skip** - skips writing STRM files that already exist on disk
- **Configurable parallelism** - 1-10 concurrent operations (default 3)
- **Orphan cleanup** - automatically removes STRM files for content no longer in the source
- **Cross-listing deduplication** - movies/series appearing in multiple categories are synced once
- **Content name cleaning** - strips provider prefix tags (e.g. `|UK|`, `|FR|`) and custom terms from titles
- **Real-time progress** - Phase, Total, Completed, Skipped, Failed counters polled every 500ms

### Dispatcharr Integration

Optional integration with [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) for IPTV stream management.

- **Stream proxy routing** - routes Live TV through Dispatcharr's proxy for connection management
- **Pre-populated media info** - fetches codec, resolution, and bitrate from Dispatcharr's stream stats (requires [Streamflow](https://github.com/krinkuto11/streamflow) configured in Dispatcharr to generate per-channel metadata)
- **FFprobe bypass** - skips Emby's stream analysis when stats are available (faster channel switching)
- **JWT authentication** - automatic token refresh with retry and exponential backoff
- **Graceful fallback** - reverts to direct Xtream URLs if Dispatcharr is unavailable

### Built-in Dashboard

A configuration UI embedded in Emby's plugin settings with five tabs.

- **Dashboard** - last sync status, sync history (last 10), library stats, live progress bar
- **Settings** - server connection, sync tuning, name cleaning, metadata matching
- **Movies** - enable/disable, folder mode, category selection with search, sync button
- **Series** - same layout as movies with series-specific options
- **Live TV** - stream format, EPG settings, catch-up, Dispatcharr, category filtering

---

## Installation

The first installation must be completed manually because the plugin is not yet
available inside Emby. After the first installation and restart, use the
plugin's built-in updater for ordinary updates.

The manual procedure below follows Emby's
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

### Configure the Plugin

1. Open Emby's web UI
2. Open **Server Dashboard > Plugins > My Plugins**
3. Open the menu for **m3u-editor for Emby** and select **Settings**
4. Enter your Xtream server details:
   - **Server URL** - e.g. `http://your-provider:port`
   - **Username** and **Password**
5. Click **Test Connection** to verify
6. Click **Save**

### Set Up Live TV

1. Switch to the **Live TV** tab
2. Choose your **Stream Format** (MPEG-TS recommended)
3. Click **Refresh Categories** to load channel groups
4. Select the categories you want
5. Configure **EPG** settings (days to fetch, cache duration)
6. Click **Save**
7. Go to **Server Dashboard > Live TV** and verify the automatically registered
   **m3u-editor for Emby** tuner. The plugin manages this tuner; do not add a
   separate M3U tuner.

### Set Up Movies (Optional)

1. Switch to the **Movies** tab
2. Check **Enable VOD Movies**
3. Click **Refresh Categories** to load VOD categories
4. Choose a **Folder Organization** mode:
   - **Single Folder** - select categories, all movies go to `Movies/`
   - **Multiple Folders** - add folders, name them, and assign categories
5. Click **Sync Movies Now**
6. In Emby, add a new **Movies** library pointing to the STRM output path (default: `/config/m3u-editor-for-emby/Movies`)

### Set Up Series (Optional)

1. Switch to the **Series** tab
2. Check **Enable Series / TV Shows**
3. Same workflow as Movies - refresh categories, select, choose folder mode
4. Click **Sync Series Now**
5. In Emby, add a new **TV Shows** library pointing to `/config/m3u-editor-for-emby/Shows`

### Dispatcharr Integration (Optional)

If you use [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) for stream management:

1. Go to **Live TV** tab > **Dispatcharr** section
2. Check **Enable Dispatcharr**
3. Enter Dispatcharr URL, username, and password
4. Click **Test Dispatcharr** to verify
5. Save - Live TV streams will now route through Dispatcharr's proxy

> **Note:** For the plugin to receive codec/resolution metadata and skip FFprobe, [Streamflow](https://github.com/krinkuto11/streamflow) must be enabled and configured in Dispatcharr. Without Streamflow generating per-channel stream stats, the plugin falls back to standard stream handling.

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
| **EPG Cache** | 30 min | How long to cache EPG data (5-1440 min) |
| **EPG Days** | 2 | Days of guide data to fetch (1-14) |
| **M3U Cache** | 15 min | How long to cache channel playlists (1-1440 min) |
| **STRM Library Path** | `/config/m3u-editor-for-emby` | Where STRM files are written. Existing configured paths are preserved during upgrades. |
| **Smart Skip** | On | Skip existing STRM files during sync |
| **Sync Parallelism** | 3 | Concurrent operations during sync (1-10) |
| **Cleanup Orphans** | Off | Remove STRM files not in source |
| **TMDB Folder Naming** | Off | Append `[tmdbid=X]` to movie/series folders |
| **Fallback Lookup** | Off | Query Emby's metadata providers for missing IDs |
| **Name Cleaning** | Off | Strip prefix tags and custom terms from titles |

---

## License

The plugin source is licensed under the MIT License.

The project logo is from
[`m3ue/m3u-editor`'s `public/logo.svg`](https://github.com/m3ue/m3u-editor/blob/master/public/logo.svg)
and is licensed under
[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).
