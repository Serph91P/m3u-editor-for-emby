using System;
using MediaBrowser.Model.Plugins;

namespace Emby.M3uEditor.Plugin
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // m3u-editor Xtream-compatible connection
        public string BaseUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string HttpUserAgent { get; set; } = string.Empty;
        public bool EnableDiagnosticsLogging { get; set; }
        // Versioned m3u-editor managed publishing state. Existing Xtream settings remain authoritative.
        public bool ManagedPublishingEnabled { get; set; }
        public int ManagedPublishingApiVersion { get; set; }
        public int ManagedPublishingIntegrationId { get; set; }
        public bool ManagedSetupReady { get; set; }
        public string ManagedSetupLastResult { get; set; } = string.Empty;
        public string ManagedCatalogRevision { get; set; } = string.Empty;
        public string ManagedActiveGeneration { get; set; } = string.Empty;
        public string ManagedPreviousGeneration { get; set; } = string.Empty;
        public string ManagedMappingsJson { get; set; } = string.Empty;
        public string ManagedApprovedOutputRoots { get; set; } = string.Empty;
        public string ManagedDryRunSummary { get; set; } = string.Empty;
        public int ManagedOmittedVersions { get; set; }
        public string ManagedLastError { get; set; } = string.Empty;
        public long ManagedLastSuccessTicks { get; set; }

        // Live TV
        public bool EnableLiveTv { get; set; } = true;
        public string LiveTvOutputFormat { get; set; } = "ts";
        [Obsolete("Use EnableDiagnosticsLogging instead")]
        public bool EnableLiveTvDiagnostics { get; set; }

        // EPG / Guide Data
        public EpgSourceMode EpgSource { get; set; } = EpgSourceMode.XtreamServer;
        public string CustomEpgUrl { get; set; } = string.Empty;
        // Back-compat: migrate EnableEpg (bool) → EpgSource on first load
        [Obsolete("Use EpgSource instead")] public bool EnableEpg { get; set; } = true;
        public int EpgCacheMinutes { get; set; } = 30;
        public int EpgDaysToFetch { get; set; } = 2;
        public int M3UCacheMinutes { get; set; } = 15;

        // Category filtering
        public int[] SelectedLiveCategoryIds { get; set; } = new int[0];
        public bool IncludeAdultChannels { get; set; }

        // Channel name cleaning (Live TV channel display names).
        // Default OFF: channels like "BBC One HD" / "Das Erste HD" are
        // distinct broadcasts with their own EPG IDs; stripping the suffix
        // breaks EPG matching and merges separate channels visually. Users
        // can opt in via the dedicated toggle in the Live TV settings.
        public string ChannelRemoveTerms { get; set; } = string.Empty;
        public bool EnableChannelNameCleaning { get; set; } = false;
        public bool UseM3uLogoForAllChannelImages { get; set; } = false;

        // Cached Live TV categories (JSON array, populated on refresh)
        public string CachedLiveCategories { get; set; } = string.Empty;

        // Update tracking
        public string LastInstalledVersion { get; set; } = string.Empty;
        public bool UseBetaChannel { get; set; }

        // Live TV state (persisted across restarts)
        public string LastChannelListHash { get; set; } = string.Empty;
    }

    public enum EpgSourceMode
    {
        XtreamServer = 0,
        CustomUrl    = 1,
        Disabled     = 2,
    }
}
