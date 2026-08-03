using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Emby.Xtream.Plugin.Api;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Services;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class PluginCompatibilityTests
    {
        [Fact]
        public void PluginBranding_PreservesInstalledIdentity()
        {
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

            Assert.Equal("m3u-editor for Emby", plugin.Name);
            Assert.Equal(new Guid("b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5"), plugin.Id);
            Assert.Equal("Emby.Xtream.Plugin", typeof(Plugin).Assembly.GetName().Name);
            Assert.Equal("Emby.Xtream.Plugin", typeof(Plugin).Namespace);
            Assert.IsAssignableFrom<IHasWebPages>(plugin);
        }

        [Fact]
        public void ServiceExports_PreserveLiveTvAndTunerContracts()
        {
            Assert.True(typeof(LiveTvService).IsPublic);
            Assert.Equal("xtream-tuner", XtreamTunerHost.TunerType);
            Assert.True(typeof(BaseTunerHost).IsAssignableFrom(typeof(XtreamTunerHost)));
            Assert.True(typeof(XtreamTunerHost).IsPublic);
        }

        [Fact]
        public void ConfigurationPages_PreserveStableAliases()
        {
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            var pageNames = plugin.GetPages().Select(page => page.Name).ToArray();

            Assert.Contains("xtreamconfig", pageNames);
            Assert.Contains("xtreamconfigjs", pageNames);
            Assert.Contains("XtreamTuner", pageNames);
            Assert.Contains("m3u-editorforEmby", pageNames);
        }

        [Fact]
        public void Dashboard_UsesNewBrandingAndRetainsGenericXtreamCopy()
        {
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
                "Emby.Xtream.Plugin.Configuration.Web.config.html"))
            using (var reader = new StreamReader(stream))
            {
                var html = reader.ReadToEnd();

                Assert.Contains("data-title=\"m3u-editor for Emby\"", html);
                Assert.Contains("Xtream-compatible", html);
            }
        }

        [Fact]
        public void RoutesAndScheduledTasks_PreserveOldContractsAndRegisterManagedReconcile()
        {
            var routeTypes = typeof(XtreamTunerApi).Assembly.GetTypes()
                .SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), false)
                    .Cast<RouteAttribute>()
                    .Select(route => route.Path))
                .ToArray();

            Assert.Contains("/XtreamTuner/Epg", routeTypes);
            Assert.Contains("/XtreamTuner/LiveTv", routeTypes);
            Assert.Contains("/XtreamTuner/Sync/Movies", routeTypes);
            Assert.Contains("/XtreamTuner/Sync/Series", routeTypes);
            Assert.Contains("/XtreamTuner/Dashboard", routeTypes);
            Assert.Contains("/XtreamTuner/ValidateStrmPath", routeTypes);
            Assert.Contains("/XtreamTuner/Managed/Reconcile", routeTypes);
            Assert.Contains("/XtreamTuner/Managed/Rollback", routeTypes);

            var moviesTask = (SyncMoviesTask)RuntimeHelpers.GetUninitializedObject(typeof(SyncMoviesTask));
            var seriesTask = (SyncSeriesTask)RuntimeHelpers.GetUninitializedObject(typeof(SyncSeriesTask));
            var managedTask = (ManagedCatalogTask)RuntimeHelpers.GetUninitializedObject(typeof(ManagedCatalogTask));
            Assert.Equal("XtreamTunerSyncMovies", moviesTask.Key);
            Assert.Equal("XtreamTunerSyncSeries", seriesTask.Key);
            Assert.Equal("XtreamTunerManagedReconcile", managedTask.Key);
        }

        [Fact]
        public void ManagedDashboardAndActions_RequireEmbyAuthentication()
        {
            Assert.NotEmpty(typeof(GetDashboard).GetCustomAttributes(typeof(AuthenticatedAttribute), true));
            Assert.NotEmpty(typeof(ReconcileManagedCatalog).GetCustomAttributes(typeof(AuthenticatedAttribute), true));
            Assert.NotEmpty(typeof(RollbackManagedCatalog).GetCustomAttributes(typeof(AuthenticatedAttribute), true));
            Assert.Empty(typeof(GetM3UPlaylist).GetCustomAttributes(typeof(AuthenticatedAttribute), true));

            Assert.Equal(
                typeof(object),
                typeof(XtreamTunerApi).GetMethod("Post", new[] { typeof(ReconcileManagedCatalog) }).ReturnType);
            Assert.Equal(
                typeof(object),
                typeof(XtreamTunerApi).GetMethod("Post", new[] { typeof(RollbackManagedCatalog) }).ReturnType);
        }

        [Fact]
        public void Dashboard_ManagedPublishingControls_UseOldRouteNamespaceAndConfirmRollback()
        {
            var assembly = typeof(Plugin).Assembly;
            string html;
            string javascript;
            using (var stream = assembly.GetManifestResourceStream("Emby.Xtream.Plugin.Configuration.Web.config.html"))
            using (var reader = new StreamReader(stream))
            {
                html = reader.ReadToEnd();
            }

            using (var stream = assembly.GetManifestResourceStream("Emby.Xtream.Plugin.Configuration.Web.config.js"))
            using (var reader = new StreamReader(stream))
            {
                javascript = reader.ReadToEnd();
            }

            Assert.Contains("managedPublishingCard", html);
            Assert.Contains("managedPublishingMappings", html);
            Assert.Contains("btnManagedReconcile", html);
            Assert.Contains("btnManagedRollback", html);
            Assert.Contains("btnManagedPreviousPage", html);
            Assert.Contains("btnManagedNextPage", html);
            Assert.Contains("XtreamTuner/Managed/Reconcile", javascript);
            Assert.Contains("XtreamTuner/Managed/Rollback", javascript);
            Assert.Contains("ManagedPage", javascript);
            Assert.Contains("managed.Job", javascript);
            Assert.Contains("Managed publishing status unavailable", javascript);
            Assert.Contains("confirm(", javascript);
        }
    }
}
