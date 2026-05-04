using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for <see cref="XtreamTunerHost"/>.
    ///
    /// We can't run the full constructor (needs IServerApplicationHost + a live
    /// MediaBrowser host environment), so the cache-state tests use
    /// FormatterServices.GetUninitializedObject + reflection — same pattern as
    /// LiveTvCacheTests. This is fine because OnChannelListChanged() and the
    /// stats single-flight only touch instance fields.
    /// </summary>
    public class XtreamTunerHostTests
    {
        private static XtreamTunerHost MakeBareHost()
        {
            // Skip the constructor — we are only exercising in-memory cache state.
            var host = (XtreamTunerHost)FormatterServices.GetUninitializedObject(typeof(XtreamTunerHost));
            // _ensureStatsLock is readonly; FormatterServices doesn't run field initializers,
            // so we have to set it manually for any test that exercises EnsureStatsLoadedAsync.
            // Tests that only call OnChannelListChanged don't need it.
            return host;
        }

        // ── OnChannelListChanged: drift-invalidation contract ──────────────────

        [Fact]
        public void OnChannelListChanged_ClearsCachedChannels()
        {
            var host = MakeBareHost();
            SetField(host, "_cachedChannels", new List<ChannelInfo> { new ChannelInfo { Name = "x" } });
            SetField(host, "_cacheTime", DateTime.UtcNow);

            host.OnChannelListChanged();

            Assert.Equal(0, host.CachedChannelCount);
        }

        [Fact]
        public void OnChannelListChanged_ResetsCacheTime()
        {
            var host = MakeBareHost();
            SetField(host, "_cachedChannels", new List<ChannelInfo> { new ChannelInfo { Name = "x" } });
            SetField(host, "_cacheTime", DateTime.UtcNow);

            host.OnChannelListChanged();

            Assert.Equal(DateTime.MinValue, GetField<DateTime>(host, "_cacheTime"));
        }

        [Fact]
        public void OnChannelListChanged_PreservesStreamStats()
        {
            // Critical: clearing _streamStats here would temporarily break EPG/station-id
            // mapping for in-flight playback. Only the channel cache should drop.
            var host = MakeBareHost();
            var stats = new Dictionary<int, StreamStatsInfo>
            {
                { 42, default(StreamStatsInfo) },
            };
            SetField(host, "_streamStats", stats);
            SetField(host, "_cachedChannels", new List<ChannelInfo>());

            host.OnChannelListChanged();

            var after = GetField<Dictionary<int, StreamStatsInfo>>(host, "_streamStats");
            Assert.NotNull(after);
            Assert.True(after.ContainsKey(42));
        }

        [Fact]
        public void OnChannelListChanged_PreservesTvgIdMap()
        {
            var host = MakeBareHost();
            var tvg = new Dictionary<int, string> { { 1, "bbc1" } };
            SetField(host, "_tvgIdMap", tvg);

            host.OnChannelListChanged();

            var after = GetField<Dictionary<int, string>>(host, "_tvgIdMap");
            Assert.Equal("bbc1", after[1]);
        }

        [Fact]
        public void OnChannelListChanged_PreservesStationIdMap()
        {
            var host = MakeBareHost();
            var st = new Dictionary<int, string> { { 1, "12345" } };
            SetField(host, "_stationIdMap", st);

            host.OnChannelListChanged();

            var after = GetField<Dictionary<int, string>>(host, "_stationIdMap");
            Assert.Equal("12345", after[1]);
        }

        // ── ClearCaches: hard reset (still wipes Dispatcharr maps) ─────────────

        [Fact]
        public void ClearCaches_DropsStreamStats()
        {
            var host = MakeBareHost();
            SetField(host, "_streamStats", new Dictionary<int, StreamStatsInfo>
            {
                { 1, default(StreamStatsInfo) },
            });

            host.ClearCaches();

            Assert.Empty(GetField<Dictionary<int, StreamStatsInfo>>(host, "_streamStats"));
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static void SetField(object obj, string name, object value)
        {
            var fi = obj.GetType().GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            fi.SetValue(obj, value);
        }

        private static T GetField<T>(object obj, string name)
        {
            var fi = obj.GetType().GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            return (T)fi.GetValue(obj);
        }
    }
}
