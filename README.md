<p align="center">
  <img src="logo.svg" width="180" alt="m3u-editor for Emby" />
</p>

<h1 align="center">m3u-editor for Emby</h1>

<p align="center">
  The Emby Server companion for <a href="https://github.com/m3ue/m3u-editor">m3u-editor</a>, with managed library publishing, Live TV, and EPG integration.
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

This plugin connects Emby Server directly to the Xtream API output produced by
[m3u-editor](https://github.com/m3ue/m3u-editor). It imports Live TV and EPG
data, consumes m3u-editor stream probe metadata, and exposes managed publishing
when the backend advertises the required versioned capability.

The plugin intentionally targets m3u-editor's Xtream-compatible interface. It
does not provide a generic Xtream-provider or Dispatcharr compatibility path.

## Features

### Managed Publishing

Managed publishing keeps catalog ownership and setup in m3u-editor while the
plugin safely applies the advertised state to Emby-accessible library folders.

- Versioned capability and setup detection
- Admin-approved, confined local output roots
- Library mappings, preferred sources, and failover variants
- Atomic generations with active and previous revisions
- Result callbacks and automatic restoration after callback failure
- Manual reconcile and rollback actions
- Optional Emby library refresh after a successful generation change

The plugin does not assume that every m3u-editor release supports managed
publishing. The dashboard reports setup readiness and leaves publishing actions
disabled until m3u-editor advertises a valid configuration.

### Live TV and EPG

- Managed Emby tuner registration
- Direct m3u-editor Xtream channel and stream URLs
- MPEG-TS or HLS output selection
- XMLTV from the Xtream output or a custom URL
- Configurable EPG and M3U cache durations
- Live TV category and adult-channel filtering
- Channel logo refresh and optional logo-slot normalization
- Optional channel-name cleaning

### Probe Metadata

When m3u-editor includes codec, resolution, bitrate, and related data in
`stream_stats`, the plugin passes it to Emby. Covered channels can skip Emby's
initial FFprobe pass; channels without metadata use Emby's normal analysis.
The settings page includes a probe coverage check.

### Operations

- Managed publishing status, reconcile, rollback, and pagination
- Sanitized plugin log download
- Stable and beta update channels
- In-place update installation and Emby restart prompt

## Installation

The first installation is manual because the plugin is not yet available in
Emby's plugin catalog. Later versions can be installed from the plugin's update
banner.

1. Download `Emby.M3uEditor.Plugin.dll` or the versioned ZIP from the
   [latest release](https://github.com/Serph91P/m3u-editor-for-emby/releases/latest).
2. Stop Emby Server.
3. Locate the `plugins` directory below the
   [Emby Server Data Folder](https://emby.media/support/articles/Server-Data-Folder.html).
4. Rename any existing `Emby.M3uEditor.Plugin.dll` instead of overwriting a
   loaded DLL.
5. Extract the archive if needed and place the single plugin DLL in the
   `plugins` directory.
6. Start Emby Server.

For Docker installations the destination is commonly `/config/plugins/`. On
Linux and NAS platforms, match the ownership and permissions of adjacent plugin
DLLs.

### Build From Source

Requires .NET SDK 6.0 or newer:

```bash
git clone https://github.com/Serph91P/m3u-editor-for-emby.git
cd m3u-editor-for-emby/Emby.M3uEditor.Plugin
bash build.sh
```

The DLL is written to
`Emby.M3uEditor.Plugin/out/Emby.M3uEditor.Plugin.dll`.

## Setup

### Connect m3u-editor

1. In m3u-editor, create a **Playlist Auth** and assign the desired playlist.
2. Open the playlist's Xtream API information.
3. Copy the base URL without `/player_api.php` or a trailing slash, plus the
   Playlist Auth username and password.
4. In Emby, open **Server Dashboard > Plugins > My Plugins > m3u-editor for
   Emby > Settings**.
5. Enter the connection details under **m3u-editor Connection**.
6. Select **Test Connection**, confirm authentication succeeds, and save.

Use HTTPS when available. Plain HTTP is accepted only for a confined private or
internal m3u-editor origin; public HTTP origins are rejected.

### Configure Live TV

1. Open the **Live TV** tab and enable Live TV.
2. Select MPEG-TS or HLS.
3. Configure the EPG source, guide window, and cache durations.
4. Refresh categories and select the channel groups to include. An empty
   selection includes every category.
5. Save the settings.
6. Verify the automatically registered **m3u-editor for Emby** tuner under
   Emby's Live TV settings. Do not add a second M3U tuner for the same output.

Use **Refresh Channel & EPG Cache** after upstream changes. Use **Force Reload
Channel Icons** only as a recovery action, then refresh Emby's guide.

### Enable Managed Publishing

Complete publishing setup in m3u-editor. When the plugin dashboard reports
**Managed setup ready**:

1. Confirm that every advertised local root is approved and does not escape
   the configured confinement boundary.
2. Open **Managed Publishing** and review the setup, catalog revision, dry-run
   summary, and mappings.
3. Select **Reconcile Now** to apply the current catalog.
4. Add resulting folders as Emby libraries when needed.
5. Use **Rollback Previous Generation** only to restore the prior plugin-owned
   generation for the selected mapping.

## Configuration Reference

| Setting | Default | Description |
|---|---|---|
| **Stream Format** | MPEG-TS | Live TV container format (`ts` or `m3u8`) |
| **EPG Source** | m3u-editor | Automatic m3u-editor Xtream XMLTV, custom XMLTV URL, or disabled |
| **EPG Cache** | 30 min | EPG response cache duration (5 to 1440 min) |
| **EPG Days** | 2 | Guide window (1 to 14 days) |
| **M3U Cache** | 15 min | Generated channel playlist cache duration (1 to 1440 min) |
| **Live TV Categories** | All | Included m3u-editor channel groups |
| **Adult Channels** | Off | Include channels marked as adult content |
| **Diagnostics Logging** | Off | Additional managed publishing and Live TV diagnostics |
| **Beta Channel** | Off | Receive pre-release plugin updates |

## m3ue Ecosystem

- [m3u-editor](https://github.com/m3ue/m3u-editor): playlist, EPG, probing, and
  publishing management
- [m3u-proxy](https://github.com/m3ue/m3u-proxy): streaming proxy and failover
  service for the wider ecosystem
- [m3u-tv](https://github.com/m3ue/m3u-tv): cross-platform TV client
- [m3u-editor documentation](https://github.com/m3ue/m3u-editor-docs-v2): setup
  and feature documentation

## License

The plugin source is licensed under the MIT License. The project logo is from
[`m3ue/m3u-editor`](https://github.com/m3ue/m3u-editor/blob/master/public/logo.svg)
and is licensed under
[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).
