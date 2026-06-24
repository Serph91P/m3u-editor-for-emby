using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// RuntimeHelpers.GetUninitializedObject + reflection (same pattern as
    /// LiveTvCacheTests). This is fine because OnChannelListChanged() and the
    /// stats single-flight only touch instance fields.
    /// </summary>
    public class XtreamTunerHostTests
    {
        private static XtreamTunerHost MakeBareHost()
        {
            // Skip the constructor; we are only exercising in-memory cache state.
            // RuntimeHelpers.GetUninitializedObject replaces the obsolete
            // FormatterServices.GetUninitializedObject (SYSLIB0050).
            var host = (XtreamTunerHost)RuntimeHelpers.GetUninitializedObject(typeof(XtreamTunerHost));
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

        // ── ClearCaches: wipes volatile state, preserves stabilizer maps ────────

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

        [Fact]
        public void ClearCaches_PreservesStabilizerMaps()
        {
            // Regression: ClearCaches used to wipe these lookup tables, which left a
            // transient empty-map window during the next channel scan. If that scan
            // ran without Dispatcharr (disabled or fetch failure caught) the
            // TunerChannelId fell back from the stable Gracenote station ID to the
            // raw stream ID — Emby treated channels as new and dropped logos.
            var host = MakeBareHost();
            SetField(host, "_channelUuidMap", new Dictionary<int, string> { { 1, "uuid-1" } });
            SetField(host, "_tvgIdMap", new Dictionary<int, string> { { 1, "tvg.1" } });
            SetField(host, "_stationIdMap", new Dictionary<int, string> { { 1, "12345" } });
            SetField(host, "_channelNumberMap", new Dictionary<int, double> { { 1, 5.0 } });
            SetField(host, "_tunerChannelIdToStreamId", new Dictionary<string, int> { { "12345", 1 } });

            host.ClearCaches();

            Assert.Equal("uuid-1", GetField<Dictionary<int, string>>(host, "_channelUuidMap")[1]);
            Assert.Equal("tvg.1",  GetField<Dictionary<int, string>>(host, "_tvgIdMap")[1]);
            Assert.Equal("12345",  GetField<Dictionary<int, string>>(host, "_stationIdMap")[1]);
            Assert.Equal(5.0,      GetField<Dictionary<int, double>>(host, "_channelNumberMap")[1]);
            Assert.Equal(1,        GetField<Dictionary<string, int>>(host, "_tunerChannelIdToStreamId")["12345"]);
        }

        [Fact]
        public void ClearCaches_ResetsDispatcharrDataLoadedFlag()
        {
            var host = MakeBareHost();
            SetField(host, "_dispatcharrDataLoaded", true);

            host.ClearCaches();

            Assert.False(GetField<bool>(host, "_dispatcharrDataLoaded"));
        }

        // ── Listing-provider detach must not wipe logos by default ─────────────

        [Fact]
        public void ShouldClearWrongChannelArtworkAfterDetach_DefaultFlowPreservesArtwork()
        {
            // Regression: a normal Live TV guide refresh can auto-detach listing
            // providers. That path must not clear Emby's ImageInfos for every
            // Xtream channel, otherwise backend-provided logos fall back to EPG art.
            Assert.False(XtreamTunerHost.ShouldClearWrongChannelArtworkAfterDetach(false, true));
        }

        [Fact]
        public void ShouldClearWrongChannelArtworkAfterDetach_RequiresExplicitRecoveryFlagAndConfigChange()
        {
            Assert.True(XtreamTunerHost.ShouldClearWrongChannelArtworkAfterDetach(true, true));
            Assert.False(XtreamTunerHost.ShouldClearWrongChannelArtworkAfterDetach(true, false));
        }

        // ── Channel artwork variants ───────────────────────────────────────────

        [Fact]
        public void ApplyChannelLogoVariants_DisabledLeavesLightLogosEmpty()
        {
            var info = new ChannelInfo();

            XtreamTunerHost.ApplyChannelLogoVariants(info, "https://example.com/logo.png", false);

            Assert.Equal("https://example.com/logo.png", info.ImageUrl);
            Assert.Null(info.LightLogoImageUrl);
            Assert.Null(info.LightColorLogoImageUrl);
        }

        [Fact]
        public void ApplyChannelLogoVariants_EnabledCopiesPrimaryLogoToAllLogoVariants()
        {
            var info = new ChannelInfo();

            XtreamTunerHost.ApplyChannelLogoVariants(info, "https://example.com/logo.png", true);

            Assert.Equal("https://example.com/logo.png", info.ImageUrl);
            Assert.Equal("https://example.com/logo.png", info.LightLogoImageUrl);
            Assert.Equal("https://example.com/logo.png", info.LightColorLogoImageUrl);
        }

        [Fact]
        public void ApplyChannelLogoVariants_EmptyLogoClearsAllLogoUrls()
        {
            var info = new ChannelInfo
            {
                ImageUrl = "https://example.com/old.png",
                LightLogoImageUrl = "https://example.com/old-light.png",
                LightColorLogoImageUrl = "https://example.com/old-color.png",
            };

            XtreamTunerHost.ApplyChannelLogoVariants(info, null, true);

            Assert.Null(info.ImageUrl);
            Assert.Null(info.LightLogoImageUrl);
            Assert.Null(info.LightColorLogoImageUrl);
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
