using System;
using System.Collections.Generic;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Unit tests for the channel-list drift handling in <see cref="LiveTvService"/>.
    /// Specifically covers <see cref="LiveTvService.PruneStaleEpgEntries"/> — the
    /// helper invoked when the channel-list hash changes so per-channel EPG
    /// entries for removed StreamIds get evicted.
    /// </summary>
    public class LiveTvDriftTests
    {
        [Fact]
        public void PruneStaleEpgEntries_DropsRemovedStreamIds()
        {
            // StreamId 2 was removed at the provider; the cache entry for it must go,
            // otherwise Emby would keep serving stale EPG for a recycled channel slot.
            var cache = new Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)>
            {
                { 1, (new List<EpgProgram>(), DateTime.UtcNow) },
                { 2, (new List<EpgProgram>(), DateTime.UtcNow) },
                { 3, (new List<EpgProgram>(), DateTime.UtcNow) },
            };
            var live = new HashSet<int> { 1, 3 };

            int pruned = LiveTvService.PruneStaleEpgEntries(cache, live);

            Assert.Equal(1, pruned);
            Assert.True(cache.ContainsKey(1));
            Assert.False(cache.ContainsKey(2));
            Assert.True(cache.ContainsKey(3));
        }

        [Fact]
        public void PruneStaleEpgEntries_KeepsAllWhenNothingDrifted()
        {
            // Perf guard: if the channel set is unchanged, the prune pass must not
            // invalidate fresh EPG entries — otherwise a no-op refresh would still
            // pay the per-channel re-fetch cost.
            var cache = new Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)>
            {
                { 1, (new List<EpgProgram>(), DateTime.UtcNow) },
                { 2, (new List<EpgProgram>(), DateTime.UtcNow) },
            };
            var live = new HashSet<int> { 1, 2 };

            int pruned = LiveTvService.PruneStaleEpgEntries(cache, live);

            Assert.Equal(0, pruned);
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public void PruneStaleEpgEntries_NullCache_NoThrow()
        {
            Assert.Equal(0, LiveTvService.PruneStaleEpgEntries(null, new HashSet<int> { 1 }));
        }

        [Fact]
        public void PruneStaleEpgEntries_EmptyCache_NoThrow()
        {
            var empty = new Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)>();
            Assert.Equal(0, LiveTvService.PruneStaleEpgEntries(empty, new HashSet<int>()));
            Assert.Empty(empty);
        }

        [Fact]
        public void PruneStaleEpgEntries_NullLiveSet_TreatedAsEmpty_DropsAll()
        {
            // If a caller (a future bug) passes a null live set, the safe interpretation
            // is "no channels survive" — better to over-prune than serve stale data.
            var cache = new Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)>
            {
                { 7, (new List<EpgProgram>(), DateTime.UtcNow) },
            };

            int pruned = LiveTvService.PruneStaleEpgEntries(cache, null);

            Assert.Equal(1, pruned);
            Assert.Empty(cache);
        }
    }
}
