using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.M3uEditor.Plugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        private static volatile Plugin _instance;
        private readonly IApplicationHost _applicationHost;
        private readonly IApplicationPaths _applicationPaths;
        private LiveTvService _liveTvService;
        private StrmSyncService _strmSyncService;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            _applicationHost = applicationHost;
            _applicationPaths = applicationPaths;
            _liveTvService = new LiveTvService(logManager.GetLogger("M3uEditor.LiveTv"));
            _strmSyncService = new StrmSyncService(logManager.GetLogger("M3uEditor.StrmSync"));
            _strmSyncService.ManagedOwnerPathProvider = () => DataFolderPath;
            M3uEditorTunerHost.ReconcileConfiguredTunerHost(
                applicationHost,
                Configuration.EnableLiveTv,
                logManager.GetLogger("M3uEditor.Reconcile"));
        }

        public override string Name => "m3u-editor for Emby";

        public override string Description =>
            "Live TV, EPG, VOD, and managed library publishing for Xtream-compatible backends.";

        public override Guid Id => Guid.Parse("b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5");

        public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin not initialized");

        /// <summary>Returns the current instance, or null if the plugin has not been initialised (e.g. during unit tests).</summary>
        internal static Plugin InstanceOrNull => _instance;

        public IApplicationHost ApplicationHost => _applicationHost;

        public new IApplicationPaths ApplicationPaths => _applicationPaths;

        /// <summary>
        /// Creates an HttpClient configured with the plugin's User-Agent setting.
        /// </summary>
        public static HttpClient CreateHttpClient(int timeoutSeconds = 10, string userAgentOverride = null)
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };
            var ua = !string.IsNullOrEmpty(userAgentOverride)
                ? userAgentOverride
                : _instance?.Configuration?.HttpUserAgent;
            if (!string.IsNullOrEmpty(ua))
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
            return client;
        }

        public LiveTvService LiveTvService => _liveTvService;

        public StrmSyncService StrmSyncService => _strmSyncService;

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream("Emby.M3uEditor.Plugin.thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        // The plugin GUID deliberately survived the v1.4.0 rebrand. Keep the
        // configuration filename stable as well, because Emby otherwise derives
        // a new path from Emby.M3uEditor.Plugin.dll and bypasses existing installs.
        public override string ConfigurationFileName => "Emby.Xtream.Plugin.xml";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = GetHtmlPageName(),
                    EmbeddedResourcePath = "Emby.M3uEditor.Plugin.Configuration.Web.config.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    MenuIcon = "live_tv",
                },
                new PluginPageInfo
                {
                    Name = "m3u-editorforEmby",
                    EmbeddedResourcePath = "Emby.M3uEditor.Plugin.Configuration.Web.config.html",
                },
                new PluginPageInfo
                {
                    // Keep the old Admin Plugins entry point available for upgrades.
                    Name = "XtreamTuner",
                    EmbeddedResourcePath = "Emby.M3uEditor.Plugin.Configuration.Web.config.html",
                },
                new PluginPageInfo
                {
                    Name = "xtreamconfig",
                    EmbeddedResourcePath = "Emby.M3uEditor.Plugin.Configuration.Web.config.html",
                },
                new PluginPageInfo
                {
                    Name = GetJsPageName(),
                    EmbeddedResourcePath = "Emby.M3uEditor.Plugin.Configuration.Web.config.js",
                },
                new PluginPageInfo
                {
                    Name = "xtreamconfigjs",
                    EmbeddedResourcePath = "Emby.M3uEditor.Plugin.Configuration.Web.config.js",
                },
            };
        }

        /// <summary>
        /// Returns a stable page name for config.html. Must never change between versions -
        /// if it did, the Emby SPA would navigate to a stale URL after a banner install and
        /// show "error processing request" because the old page name no longer exists in the
        /// new DLL. Emby appends ?v=&lt;ServerVersion&gt; for cache-busting.
        /// </summary>
        private static string GetHtmlPageName()
        {
            return "m3ueditorconfig";
        }

        /// <summary>
        /// Returns a stable JS page name. Emby appends ?v=&lt;ServerVersion&gt; automatically,
        /// which provides sufficient cache-busting across plugin updates.
        /// </summary>
        private static string GetJsPageName()
        {
            return "m3ueditorconfigjs";
        }
    }
}
