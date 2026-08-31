using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Api;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedDashboardTests
    {
        [Fact]
        public void BuildManagedDashboardStatus_RequestedPage_IsBoundedAndDeterministic()
        {
            var mappings = Enumerable.Range(0, 30)
                .Reverse()
                .Select(index => new ManagedMappingState
                {
                    MappingUuid = "mapping-" + index.ToString("00"),
                    LibraryName = "Library " + index,
                    CollectionType = "movies",
                    FileCount = index + 1,
                    StrmFileCount = index
                })
                .ToList();
            var config = new PluginConfiguration
            {
                ManagedPublishingEnabled = true,
                ManagedMappingsJson = JsonSerializer.Serialize(mappings)
            };

            var dashboard = M3uEditorApi.BuildManagedDashboardStatus(
                config,
                new ManagedJobStatus { State = "idle" },
                2,
                10);

            Assert.Equal(30, dashboard.TotalMappings);
            Assert.Equal(10, dashboard.Mappings.Count);
            Assert.Equal("mapping-10", dashboard.Mappings[0].MappingUuid);
            Assert.Equal("mapping-19", dashboard.Mappings[9].MappingUuid);
            Assert.True(dashboard.HasMore);
            Assert.Equal(mappings.Sum(mapping => mapping.FileCount), dashboard.TotalFiles);
            Assert.Equal(mappings.Sum(mapping => mapping.StrmFileCount), dashboard.TotalStrmFiles);
        }

        [Fact]
        public void DashboardResponse_RetainedLegacyMapping_DoesNotExposeOutputPath()
        {
            var legacyPath = Path.Combine(
                Path.GetTempPath(),
                "retained-legacy-path-literal",
                "movies");
            var config = new PluginConfiguration
            {
                ManagedMappingsJson = JsonSerializer.Serialize(new List<ManagedMappingState>
                {
                    new ManagedMappingState
                    {
                        MappingUuid = "mapping-visible-uuid",
                        LibraryName = "Visible library name",
                        CollectionType = "movies",
                        OutputPath = legacyPath,
                        ActiveRevision = "active-visible-revision",
                        PreviousRevision = "previous-visible-revision",
                        Success = true,
                        Duplicate = true,
                        FileCount = 19,
                        StrmFileCount = 17,
                        SeriesCount = 3,
                        SeasonCount = 5,
                        Added = 7,
                        Changed = 11,
                        Removed = 13,
                        OmittedVersions = 2,
                        Error = "visible-status-error"
                    }
                })
            };
            var response = new DashboardResult
            {
                History = new List<SyncHistoryEntry>(),
                LibraryStats = new LibraryStats(),
                ManagedPublishing = M3uEditorApi.BuildManagedDashboardStatus(
                    config,
                    new ManagedJobStatus { State = "idle" },
                    1,
                    10)
            };

            var responseJson = JsonSerializer.Serialize(response);

            Assert.DoesNotContain(legacyPath, responseJson);
            Assert.DoesNotContain("OutputPath", responseJson);
            Assert.IsNotType<ManagedMappingState>(Assert.Single(response.ManagedPublishing.Mappings));
            Assert.Contains("mapping-visible-uuid", responseJson);
            Assert.Contains("Visible library name", responseJson);
            Assert.Contains("movies", responseJson);
            Assert.Contains("active-visible-revision", responseJson);
            Assert.Contains("previous-visible-revision", responseJson);
            Assert.Contains("visible-status-error", responseJson);
            Assert.Contains("\"Success\":true", responseJson);
            Assert.Contains("\"Duplicate\":true", responseJson);
            Assert.Contains("\"FileCount\":19", responseJson);
            Assert.Contains("\"StrmFileCount\":17", responseJson);
            Assert.Contains("\"SeriesCount\":3", responseJson);
            Assert.Contains("\"SeasonCount\":5", responseJson);
            Assert.Contains("\"Added\":7", responseJson);
            Assert.Contains("\"Changed\":11", responseJson);
            Assert.Contains("\"Removed\":13", responseJson);
            Assert.Contains("\"OmittedVersions\":2", responseJson);
        }

        [Fact]
        public void BuildManagedLibraryStats_UsesOwnedStateCountsOnly()
        {
            var mappings = new List<ManagedMappingState>
            {
                new ManagedMappingState
                {
                    CollectionType = "movies",
                    StrmFileCount = 12
                },
                new ManagedMappingState
                {
                    CollectionType = "tvshows",
                    StrmFileCount = 34,
                    SeriesCount = 5,
                    SeasonCount = 8
                }
            };

            var stats = M3uEditorApi.BuildManagedLibraryStats(mappings);

            Assert.Equal(12, stats.MovieCount);
            Assert.Equal(34, stats.EpisodeCount);
            Assert.Equal(5, stats.SeriesCount);
            Assert.Equal(8, stats.SeasonCount);
        }

        [Fact]
        public void BuildManagedDashboardStatus_OversizedPage_IsCappedAtTwentyFiveMappings()
        {
            var mappings = Enumerable.Range(0, 40)
                .Select(index => new ManagedMappingState { MappingUuid = index.ToString("00") })
                .ToList();
            var config = new PluginConfiguration
            {
                ManagedMappingsJson = JsonSerializer.Serialize(mappings)
            };

            var dashboard = M3uEditorApi.BuildManagedDashboardStatus(
                config,
                new ManagedJobStatus { State = "idle" },
                1,
                1000);

            Assert.Equal(25, dashboard.PageSize);
            Assert.Equal(25, dashboard.Mappings.Count);
            Assert.True(dashboard.HasMore);
        }

        [Fact]
        public void BuildManagedDashboardStatus_OversizedPageNumber_IsClampedToLastPage()
        {
            var mappings = Enumerable.Range(0, 30)
                .Select(index => new ManagedMappingState { MappingUuid = index.ToString("00") })
                .ToList();
            var config = new PluginConfiguration
            {
                ManagedMappingsJson = JsonSerializer.Serialize(mappings)
            };

            var dashboard = M3uEditorApi.BuildManagedDashboardStatus(
                config,
                new ManagedJobStatus { State = "idle" },
                int.MaxValue,
                25);

            Assert.Equal(2, dashboard.Page);
            Assert.Equal(5, dashboard.Mappings.Count);
            Assert.False(dashboard.HasMore);
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(-1, true, false)]
        [InlineData(7, false, false)]
        [InlineData(7, true, true)]
        public void BuildManagedDashboardStatus_RequiresCompletedManagedSetup(
            int integrationId,
            bool setupReady,
            bool expectedValid)
        {
            var dashboard = M3uEditorApi.BuildManagedDashboardStatus(
                new PluginConfiguration
                {
                    ManagedPublishingIntegrationId = integrationId,
                    ManagedSetupReady = setupReady,
                    ManagedApprovedOutputRoots = Path.Combine(Path.GetTempPath(), "managed-dashboard-approved")
                },
                new ManagedJobStatus { State = "idle" },
                1,
                10);

            Assert.Equal(integrationId, dashboard.IntegrationId);
            Assert.Equal(expectedValid, dashboard.ConfigurationValid);
        }
    }
}
