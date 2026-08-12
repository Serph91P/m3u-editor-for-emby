using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Emby.M3uEditor.Plugin.Api;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Services;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class PluginCompatibilityTests
    {
        [Fact]
        public void PluginBranding_UsesNewAssemblyIdentityAndPreservesUpgradeGuid()
        {
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

            Assert.Equal("m3u-editor for Emby", plugin.Name);
            Assert.Equal(new Guid("b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5"), plugin.Id);
            Assert.Equal("Emby.M3uEditor.Plugin", typeof(Plugin).Assembly.GetName().Name);
            Assert.Equal("Emby.M3uEditor.Plugin", typeof(Plugin).Namespace);
            Assert.IsAssignableFrom<IHasWebPages>(plugin);
        }

        [Fact]
        public void NewConfiguration_UsesRebrandedOutputPathWithoutChangingPersistedProperty()
        {
            var configuration = new PluginConfiguration();

            Assert.Equal("/config/m3u-editor-for-emby", configuration.StrmLibraryPath);
            Assert.NotNull(typeof(PluginConfiguration).GetProperty("StrmLibraryPath"));
        }

        [Fact]
        public void ServiceExports_UseNewTunerIdentity()
        {
            Assert.True(typeof(LiveTvService).IsPublic);
            Assert.Equal("m3u-editor", M3uEditorTunerHost.TunerType);
            Assert.Equal("M3uEditorTunerHost", typeof(M3uEditorTunerHost).Name);
            Assert.True(typeof(BaseTunerHost).IsAssignableFrom(typeof(M3uEditorTunerHost)));
            Assert.True(typeof(M3uEditorTunerHost).IsPublic);
        }

        [Fact]
        public void UpgradeFromXtreamAssembly_PreservesConfigurationAndDashboardPageAliases()
        {
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            plugin.SetAttributes(
                "/plugins/Emby.M3uEditor.Plugin.dll",
                "/plugins/data/m3u-editor-for-emby",
                new Version(1, 4, 0, 0));
            var pageNames = plugin.GetPages().Select(page => page.Name).ToArray();

            Assert.Equal("Emby.Xtr" + "eam.Plugin.xml", plugin.ConfigurationFileName);
            Assert.Contains("m3ueditorconfig", pageNames);
            Assert.Contains("m3ueditorconfigjs", pageNames);
            Assert.Contains("m3u-editorforEmby", pageNames);
            Assert.Contains("xtr" + "eamconfig", pageNames);
            Assert.Contains("xtr" + "eamconfigjs", pageNames);
            Assert.Contains("Xtr" + "eamTuner", pageNames);
            Assert.Equal(6, pageNames.Length);
        }

        [Fact]
        public void Dashboard_UsesNewBrandingAndRetainsGenericXtreamCopy()
        {
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
                "Emby.M3uEditor.Plugin.Configuration.Web.config.html"))
            using (var reader = new StreamReader(stream))
            {
                var html = reader.ReadToEnd();

                Assert.Contains("data-title=\"m3u-editor for Emby\"", html);
                Assert.Contains("Xtream-compatible", html);
            }
        }

        [Fact]
        public void RoutesAndScheduledTasks_UseNewIdentity()
        {
            var routeTypes = typeof(M3uEditorApi).Assembly.GetTypes()
                .SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), false)
                    .Cast<RouteAttribute>()
                    .Select(route => route.Path))
                .ToArray();

            Assert.Contains("/M3uEditor/Epg", routeTypes);
            Assert.Contains("/M3uEditor/LiveTv", routeTypes);
            Assert.Contains("/M3uEditor/Sync/Movies", routeTypes);
            Assert.Contains("/M3uEditor/Sync/Series", routeTypes);
            Assert.Contains("/M3uEditor/Dashboard", routeTypes);
            Assert.Contains("/M3uEditor/ValidateStrmPath", routeTypes);
            Assert.Contains("/M3uEditor/Managed/Reconcile", routeTypes);
            Assert.Contains("/M3uEditor/Managed/Rollback", routeTypes);

            var moviesTask = (SyncMoviesTask)RuntimeHelpers.GetUninitializedObject(typeof(SyncMoviesTask));
            var seriesTask = (SyncSeriesTask)RuntimeHelpers.GetUninitializedObject(typeof(SyncSeriesTask));
            var managedTask = (ManagedCatalogTask)RuntimeHelpers.GetUninitializedObject(typeof(ManagedCatalogTask));
            Assert.Equal("M3uEditorSyncMovies", moviesTask.Key);
            Assert.Equal("M3uEditorSyncSeries", seriesTask.Key);
            Assert.Equal("M3uEditorManagedReconcile", managedTask.Key);
        }

        [Fact]
        public void ManagedDashboardAndActions_RequireExactAdminRole()
        {
            Assert.Equal("Admin", GetAuthenticationRoles<GetDashboard>());
            Assert.Equal("Admin", GetAuthenticationRoles<ReconcileManagedCatalog>());
            Assert.Equal("Admin", GetAuthenticationRoles<RollbackManagedCatalog>());
            Assert.Empty(typeof(GetM3UPlaylist).GetCustomAttributes(typeof(AuthenticatedAttribute), true));

            Assert.Equal(
                typeof(object),
                typeof(M3uEditorApi).GetMethod("Post", new[] { typeof(ReconcileManagedCatalog) }).ReturnType);
            Assert.Equal(
                typeof(object),
                typeof(M3uEditorApi).GetMethod("Post", new[] { typeof(RollbackManagedCatalog) }).ReturnType);
        }

        private static string GetAuthenticationRoles<TRequest>()
        {
            var attribute = Assert.Single(typeof(TRequest)
                .GetCustomAttributes(typeof(AuthenticatedAttribute), true)
                .Cast<AuthenticatedAttribute>());
            return attribute.Roles;
        }

        [Fact]
        public void Dashboard_ManagedPublishingControls_UseNewRouteNamespaceAndConfirmRollback()
        {
            var assembly = typeof(Plugin).Assembly;
            string html;
            string javascript;
            using (var stream = assembly.GetManifestResourceStream("Emby.M3uEditor.Plugin.Configuration.Web.config.html"))
            using (var reader = new StreamReader(stream))
            {
                html = reader.ReadToEnd();
            }

            using (var stream = assembly.GetManifestResourceStream("Emby.M3uEditor.Plugin.Configuration.Web.config.js"))
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
            Assert.Contains("M3uEditor/Managed/Reconcile", javascript);
            Assert.Contains("M3uEditor/Managed/Rollback", javascript);
            Assert.Contains("ManagedPage", javascript);
            Assert.Contains("managed.Job", javascript);
            Assert.Contains("Managed publishing status unavailable", javascript);
            Assert.Contains("confirm(", javascript);
        }
    }
}
