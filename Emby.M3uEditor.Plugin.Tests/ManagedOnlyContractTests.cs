using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedOnlyContractTests
    {
        [Fact]
        public void ApiRoutes_ExposeOnlyLiveTvManagedPublishingAndPluginMaintenance()
        {
            var actual = typeof(Plugin).Assembly.GetTypes()
                .SelectMany(type => type
                    .GetCustomAttributes(typeof(RouteAttribute), false)
                    .Cast<RouteAttribute>())
                .Select(route => route.Path)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var expected = new[]
            {
                "/M3uEditor/Categories/Live",
                "/M3uEditor/CheckUpdate",
                "/M3uEditor/Dashboard",
                "/M3uEditor/Epg",
                "/M3uEditor/InstallUpdate",
                "/M3uEditor/LiveTv",
                "/M3uEditor/Logs",
                "/M3uEditor/Managed/Reconcile",
                "/M3uEditor/Managed/Rollback",
                "/M3uEditor/Managed/Setup/V1",
                "/M3uEditor/ProbeDataCoverage",
                "/M3uEditor/RefreshCache",
                "/M3uEditor/RefreshChannelIcons",
                "/M3uEditor/RestartEmby",
                "/M3uEditor/TestConnection",
            }.OrderBy(path => path, StringComparer.Ordinal).ToArray();

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ScheduledTasks_ContainOnlyManagedCatalogReconciliation()
        {
            var actual = typeof(Plugin).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(IScheduledTask).IsAssignableFrom(type))
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { typeof(ManagedCatalogTask).FullName }, actual);
        }

        [Fact]
        public void ProductionAssembly_ContainsNoDispatcharrOrLegacyPublisherSurface()
        {
            var assembly = typeof(Plugin).Assembly;
            var types = assembly.GetTypes();
            var removedTypes = new[]
            {
                "Emby.M3uEditor.Plugin.Client.DispatcharrClient",
                "Emby.M3uEditor.Plugin.Client.Models.DispatcharrChannel",
                "Emby.M3uEditor.Plugin.Client.Models.DispatcharrVodMovieDetail",
                "Emby.M3uEditor.Plugin.Client.Models.VodStreamInfo",
                "Emby.M3uEditor.Plugin.Client.Models.SeriesInfo",
                "Emby.M3uEditor.Plugin.Client.Models.SeriesDetailInfo",
                "Emby.M3uEditor.Plugin.Service.SyncMoviesTask",
                "Emby.M3uEditor.Plugin.Service.SyncSeriesTask",
                "Emby.M3uEditor.Plugin.Service.SyncProgress",
                "Emby.M3uEditor.Plugin.Service.SyncHistoryEntry",
                "Emby.M3uEditor.Plugin.Service.FailedSyncItem",
                "Emby.M3uEditor.Plugin.Service.FolderMappingParser",
                "Emby.M3uEditor.Plugin.Service.ContentNameCleaner",
                "Emby.M3uEditor.Plugin.Service.TmdbLookupService",
                "Emby.M3uEditor.Plugin.Service.NfoWriter",
            };

            Assert.All(removedTypes, name => Assert.Null(assembly.GetType(name, false)));
            Assert.DoesNotContain(types, type =>
                (type.FullName ?? type.Name).IndexOf("Dispatcharr", StringComparison.OrdinalIgnoreCase) >= 0);

            var methodNames = typeof(StrmSyncService)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic)
                .Select(method => method.Name)
                .ToArray();
            Assert.DoesNotContain("SyncMoviesAsync", methodNames);
            Assert.DoesNotContain("SyncSeriesAsync", methodNames);
            Assert.DoesNotContain("RetryFailedAsync", methodNames);
            Assert.Contains("ReconcileManagedAsync", methodNames);
            Assert.Contains("PublishManagedMappingAsync", methodNames);
            Assert.Contains("RollbackManagedMappingAsync", methodNames);
        }

        [Fact]
        public void Configuration_ContainsNoDispatcharrOrLegacyWriterSettings()
        {
            var removedNames = new[]
            {
                "DetectedBackendType", "DetectedBackendName", "LastBackendDetectionTicks",
                "EnableDispatcharr", "DispatcharrUrl", "DispatcharrUser", "DispatcharrPass",
                "DispatcharrFallbackToXtream", "ForceAudioTranscode",
                "SelectedDispatcharrProfileIds", "CachedDispatcharrProfiles",
                "SyncMovies", "SyncSeries", "StrmLibraryPath",
                "SelectedVodCategoryIds", "SelectedSeriesCategoryIds",
                "MovieFolderMode", "MovieFolderMappings", "SeriesFolderMode", "SeriesFolderMappings",
                "EnableContentNameCleaning", "ContentRemoveTerms", "EnableTmdbFolderNaming",
                "EnableTmdbFallbackLookup", "EnableSeriesIdFolderNaming", "EnableSeriesMetadataLookup",
                "TvdbFolderIdOverrides", "EnableNfoFiles", "CachedVodCategories",
                "CachedSeriesCategories", "SmartSkipExisting", "SyncParallelism", "CleanupOrphans",
                "OrphanSafetyThreshold", "AutoSyncEnabled", "AutoSyncMode", "AutoSyncIntervalHours",
                "AutoSyncDailyTime", "LastMovieSyncTimestamp", "LastSeriesSyncTimestamp",
                "StrmNamingVersion", "SyncHistoryJson", "SeriesEpisodeHashesJson",
            };

            Assert.All(removedNames, name =>
                Assert.Null(typeof(PluginConfiguration).GetProperty(name, BindingFlags.Instance | BindingFlags.Public)));
        }

        [Fact]
        public void EmbeddedDashboard_ContainsOnlyManagedPublishingAndLiveTvControls()
        {
            var html = ReadResource("Emby.M3uEditor.Plugin.Configuration.Web.config.html");
            var javascript = ReadResource("Emby.M3uEditor.Plugin.Configuration.Web.config.js");
            var removedMarkers = new[]
            {
                "Dispatcharr", "data-tab=\"movies\"", "data-tab=\"series\"",
                "tabMovies", "tabSeries", "txtStrmLibraryPath", "chkSyncMovies", "chkSyncSeries",
                "btnSyncMovies", "btnSyncSeries", "btnDeleteMovies", "btnDeleteSeries",
                "btnRetryFailed", "btnDashboardSyncAll", "chkAutoSyncEnabled",
                "M3uEditor/Sync/Movies", "M3uEditor/Sync/Series", "M3uEditor/TestTmdbLookup",
            };

            Assert.All(removedMarkers, marker =>
            {
                Assert.DoesNotContain(marker, html, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(marker, javascript, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Contains("managedPublishingCard", html, StringComparison.Ordinal);
            Assert.Contains("btnManagedReconcile", html, StringComparison.Ordinal);
            Assert.Contains("M3uEditor/Managed/Reconcile", javascript, StringComparison.Ordinal);
            Assert.Contains("stream_stats", html, StringComparison.Ordinal);
            Assert.Contains("M3uEditor/ProbeDataCoverage", javascript, StringComparison.Ordinal);
        }

        [Fact]
        public void PreviousConfiguration_DeserializesWithoutReactivatingRemovedWriters()
        {
            const string xml = "<PluginConfiguration>" +
                "<BaseUrl>http://editor.example</BaseUrl>" +
                "<Username>active-user</Username>" +
                "<EnableLiveTv>true</EnableLiveTv>" +
                "<ManagedPublishingIntegrationId>17</ManagedPublishingIntegrationId>" +
                "<ManagedApprovedOutputRoots>/managed</ManagedApprovedOutputRoots>" +
                "<EnableDispatcharr>true</EnableDispatcharr>" +
                "<SyncMovies>true</SyncMovies>" +
                "<SyncSeries>true</SyncSeries>" +
                "<StrmLibraryPath>/legacy</StrmLibraryPath>" +
                "</PluginConfiguration>";
            var serializer = new XmlSerializer(typeof(PluginConfiguration));

            PluginConfiguration config;
            using (var reader = new StringReader(xml))
            {
                config = (PluginConfiguration)serializer.Deserialize(reader);
            }

            Assert.Equal("http://editor.example", config.BaseUrl);
            Assert.Equal("active-user", config.Username);
            Assert.True(config.EnableLiveTv);
            Assert.Equal(17, config.ManagedPublishingIntegrationId);
            Assert.Equal("/managed", config.ManagedApprovedOutputRoots);
            Assert.Null(typeof(PluginConfiguration).GetProperty("SyncMovies"));
            Assert.Null(typeof(PluginConfiguration).GetProperty("SyncSeries"));

            string persisted;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, config);
                persisted = writer.ToString();
            }

            Assert.DoesNotContain("Dispatcharr", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SyncMovies", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("SyncSeries", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("StrmLibraryPath", persisted, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(5, 3, "m3u-editor")]
        [InlineData(5, 0, "none")]
        public void ProbeCoverage_ReportsOnlyM3uEditorOrNone(int total, int withStats, string source)
        {
            var result = Api.M3uEditorApi.BuildProbeDataCoverageResult(total, withStats, false);

            Assert.True(result.Success);
            Assert.Equal(source, result.Source);
            Assert.Equal(withStats, result.ChannelsWithProbeData);
        }

        private static string ReadResource(string name)
        {
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
