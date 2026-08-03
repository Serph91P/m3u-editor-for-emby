using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Emby.Xtream.Plugin.Api;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
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

            var dashboard = XtreamTunerApi.BuildManagedDashboardStatus(
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
        public void BuildManagedLibraryStats_UsesOwnedStateCountsOnly()
        {
            var dashboard = new ManagedDashboardStatus
            {
                Mappings = new List<ManagedMappingState>
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
                }
            };

            var stats = XtreamTunerApi.BuildManagedLibraryStats(dashboard.Mappings);

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

            var dashboard = XtreamTunerApi.BuildManagedDashboardStatus(
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

            var dashboard = XtreamTunerApi.BuildManagedDashboardStatus(
                config,
                new ManagedJobStatus { State = "idle" },
                int.MaxValue,
                25);

            Assert.Equal(2, dashboard.Page);
            Assert.Equal(5, dashboard.Mappings.Count);
            Assert.False(dashboard.HasMore);
        }
    }
}
