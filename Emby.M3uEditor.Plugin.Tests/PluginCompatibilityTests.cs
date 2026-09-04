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
        public void ServiceExports_UseNewTunerIdentity()
        {
            Assert.True(typeof(LiveTvService).IsPublic);
            Assert.Equal("m3u-editor", M3uEditorTunerHost.TunerType);
            Assert.Equal("M3uEditorTunerHost", typeof(M3uEditorTunerHost).Name);
            Assert.True(typeof(BaseTunerHost).IsAssignableFrom(typeof(M3uEditorTunerHost)));
            Assert.True(typeof(M3uEditorTunerHost).IsPublic);
        }

        [Fact]
        public void UpgradeFromXtreamAssembly_PreservesAliasesAndRevisesCachedDashboardResources()
        {
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            plugin.SetAttributes(
                "/plugins/Emby.M3uEditor.Plugin.dll",
                "/plugins/data/m3u-editor-for-emby",
                new Version(1, 4, 0, 0));
            var pages = plugin.GetPages().ToArray();
            var pageNames = pages.Select(page => page.Name).ToArray();
            var mainPage = Assert.Single(pages.Where(page => page.EnableInMainMenu));

            Assert.Equal("Emby.Xtr" + "eam.Plugin.xml", plugin.ConfigurationFileName);
            Assert.Equal("m3ueditorconfigr3", mainPage.Name);
            Assert.Contains("m3ueditorconfigjsr3", pageNames);
            Assert.Contains("m3ueditorconfigr2", pageNames);
            Assert.Contains("m3ueditorconfigjsr2", pageNames);
            Assert.Contains("m3ueditorconfig", pageNames);
            Assert.Contains("m3ueditorconfigjs", pageNames);
            Assert.Contains("m3u-editorforEmby", pageNames);
            Assert.Contains("xtr" + "eamconfig", pageNames);
            Assert.Contains("xtr" + "eamconfigjs", pageNames);
            Assert.Contains("Xtr" + "eamTuner", pageNames);
            Assert.Equal(10, pageNames.Length);

            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
                "Emby.M3uEditor.Plugin.Configuration.Web.config.html"))
            using (var reader = new StreamReader(stream))
            {
                Assert.Contains("data-controller=\"__plugin/m3ueditorconfigjsr3\"", reader.ReadToEnd());
            }
        }

        [Fact]
        public void Dashboard_UsesNewBrandingAndM3uEditorCopy()
        {
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
                "Emby.M3uEditor.Plugin.Configuration.Web.config.html"))
            using (var reader = new StreamReader(stream))
            {
                var html = reader.ReadToEnd();

                Assert.Contains("data-title=\"m3u-editor for Emby\"", html);
                Assert.Contains("m3u-editor", html);
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
            Assert.Contains("/M3uEditor/Dashboard", routeTypes);
            Assert.Contains("/M3uEditor/Managed/Reconcile", routeTypes);
            Assert.Contains("/M3uEditor/Managed/Rollback", routeTypes);

            var managedTask = (ManagedCatalogTask)RuntimeHelpers.GetUninitializedObject(typeof(ManagedCatalogTask));
            Assert.Equal("M3uEditorManagedReconcile", managedTask.Key);
        }

        [Fact]
        public void AdministrativeRoutes_RequireExactAdminRole()
        {
            Assert.Equal("Admin", GetAuthenticationRoles<GetLiveCategories>());
            Assert.Equal("Admin", GetAuthenticationRoles<RefreshCache>());
            Assert.Equal("Admin", GetAuthenticationRoles<RefreshChannelIcons>());
            Assert.Equal("Admin", GetAuthenticationRoles<GetDashboard>());
            Assert.Equal("Admin", GetAuthenticationRoles<ReconcileManagedCatalog>());
            Assert.Equal("Admin", GetAuthenticationRoles<RollbackManagedCatalog>());
            Assert.Equal("Admin", GetAuthenticationRoles<TestXtreamConnection>());
            Assert.Equal("Admin", GetAuthenticationRoles<CheckProbeDataCoverage>());
            Assert.Equal("Admin", GetAuthenticationRoles<CheckForUpdate>());
            Assert.Equal("Admin", GetAuthenticationRoles<GetSanitizedLogs>());
            Assert.Equal("Admin", GetAuthenticationRoles<InstallUpdate>());
            Assert.Equal("Admin", GetAuthenticationRoles<RestartEmby>());
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
            Assert.Contains("data-managed-view=\"overview\"", html);
            Assert.Contains("data-managed-view=\"movies\"", html);
            Assert.Contains("data-managed-view=\"series\"", html);
            Assert.Contains("aria-pressed=\"true\"", html);
            Assert.Contains("aria-pressed=\"false\"", html);
            Assert.DoesNotContain("role=\"tablist\"", html);
            Assert.DoesNotContain("role=\"tab\"", html);
            Assert.Contains("managedMovieMappings", html);
            Assert.Contains("managedSeriesMappings", html);
            Assert.Contains("btnManagedReconcile", html);
            Assert.Contains("btnManagedRollback", html);
            Assert.Contains("btnManagedPreviousPage", html);
            Assert.Contains("btnManagedNextPage", html);
            Assert.Contains("M3uEditor/Managed/Reconcile", javascript);
            Assert.Contains("M3uEditor/Managed/Rollback", javascript);
            Assert.Contains("ManagedPage", javascript);
            Assert.Contains("mapping.SourceGroups", javascript);
            Assert.Contains("mapping.LibraryNameTruncated", javascript);
            Assert.Contains("switchManagedView", javascript);
            Assert.Contains("renderManagedMapping", javascript);
            Assert.Contains("if (movieMappingsElement)", javascript);
            Assert.Contains("if (seriesMappingsElement)", javascript);
            Assert.Contains("setAttribute('aria-pressed'", javascript);
            Assert.DoesNotContain("setAttribute('aria-selected'", javascript);
            Assert.Contains("managed.Job", javascript);
            Assert.Contains("Managed publishing status unavailable", javascript);
            Assert.Contains("confirm(", javascript);
        }

        [Fact]
        public void Dashboard_ManagedPublishingControls_LiveOnDedicatedTabWithCompactDashboardNavigation()
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

            var dashboardStart = html.IndexOf("tabPanel tabDashboard", StringComparison.Ordinal);
            var managedTabStart = html.IndexOf("tabPanel tabManagedPublishing", StringComparison.Ordinal);
            var managedCardStart = html.IndexOf("managedPublishingCard", StringComparison.Ordinal);
            Assert.True(dashboardStart >= 0);
            Assert.True(managedTabStart > dashboardStart);
            Assert.True(managedCardStart > managedTabStart);
            Assert.Contains("data-tab=\"managedPublishing\"", html);
            Assert.Contains(".configTabs {", html);
            Assert.Contains("overflow-x: auto", html);
            Assert.Contains(".tabManagedPublishing .verticalSection", html);
            Assert.Contains("managedPublishingSummary", html.Substring(dashboardStart, managedTabStart - dashboardStart));
            Assert.Contains("btnOpenManagedPublishing", html.Substring(dashboardStart, managedTabStart - dashboardStart));
            Assert.DoesNotContain("btnManagedReconcile", html.Substring(dashboardStart, managedTabStart - dashboardStart));
            Assert.Contains("Managed by m3u-editor", html.Substring(managedTabStart));
            Assert.DoesNotContain("txtManagedPublishingIntegrationId", html);
            Assert.DoesNotContain("txtManagedApprovedOutputRoots", html);
            Assert.Contains("managedPublishing: '.tabManagedPublishing'", javascript);
            Assert.Contains("managed.SetupReady", javascript);
            Assert.DoesNotContain("config.ManagedPublishingIntegrationId =", javascript);
            Assert.DoesNotContain("config.ManagedApprovedOutputRoots =", javascript);
            Assert.Contains("switchTab(view, 'managedPublishing')", javascript);
            Assert.Contains("view.querySelector('.managedPublishingSummaryStatus')", javascript);
            Assert.Contains("reconcileButton.disabled = jobRunning || !available;", javascript);
        }
    }
}
