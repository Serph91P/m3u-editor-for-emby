using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class M3uEditorTunerHostTests
    {
        private static M3uEditorTunerHost MakeBareHost()
        {
            return (M3uEditorTunerHost)RuntimeHelpers.GetUninitializedObject(
                typeof(M3uEditorTunerHost));
        }

        [Fact]
        public void ReconcileTunerHosts_FreshInstallCreatesStableOfficialConfiguration()
        {
            var options = new LiveTvOptions { TunerHosts = null };

            var changed = M3uEditorTunerHost.ReconcileTunerHosts(options, true);

            Assert.True(changed);
            var tuner = Assert.Single(options.TunerHosts);
            Assert.Equal(M3uEditorTunerHost.TunerType, tuner.Type);
            Assert.Equal(M3uEditorTunerHost.StableTunerId, tuner.Id);
            Assert.Equal(1, tuner.TunerCount);
            Assert.False(M3uEditorTunerHost.ReconcileTunerHosts(options, true));
        }

        [Fact]
        public void ReconcileTunerHosts_MigratesStableHostTypeWithoutAddingDuplicate()
        {
            var existing = new TunerHostInfo
            {
                Type = "legacy-tuner",
                Id = M3uEditorTunerHost.StableTunerId,
                TunerCount = 2,
            };
            var options = new LiveTvOptions { TunerHosts = new[] { existing } };

            Assert.True(M3uEditorTunerHost.ReconcileTunerHosts(options, true));
            var migrated = Assert.Single(options.TunerHosts);
            Assert.Same(existing, migrated);
            Assert.Equal(M3uEditorTunerHost.TunerType, migrated.Type);
            Assert.Equal(2, migrated.TunerCount);
        }

        [Fact]
        public void ReconcileTunerHosts_DisabledRemovesOnlyPluginTuners()
        {
            var unrelated = new TunerHostInfo { Type = "native-tuner", Id = "native" };
            var plugin = new TunerHostInfo { Type = M3uEditorTunerHost.TunerType, Id = "plugin" };
            var options = new LiveTvOptions { TunerHosts = new[] { unrelated, plugin } };

            Assert.True(M3uEditorTunerHost.ReconcileTunerHosts(options, false));
            Assert.Single(options.TunerHosts);
            Assert.Same(unrelated, options.TunerHosts[0]);
        }

        [Fact]
        public void OnChannelListChanged_ClearsChannelsButPreservesBackendStats()
        {
            var host = MakeBareHost();
            SetField(host, "_cachedChannels", new List<ChannelInfo> { new ChannelInfo { Name = "x" } });
            SetField(host, "_cacheTime", DateTime.UtcNow);
            SetField(host, "_streamStats", new Dictionary<int, StreamStatsInfo>
            {
                { 42, new StreamStatsInfo { VideoCodec = "h264" } },
            });

            host.OnChannelListChanged();

            Assert.Equal(0, host.CachedChannelCount);
            Assert.Equal(DateTime.MinValue, GetField<DateTime>(host, "_cacheTime"));
            Assert.True(GetField<Dictionary<int, StreamStatsInfo>>(host, "_streamStats").ContainsKey(42));
        }

        [Fact]
        public void ClearCaches_DropsChannelsAndBackendStats()
        {
            var host = MakeBareHost();
            SetField(host, "_cachedChannels", new List<ChannelInfo> { new ChannelInfo() });
            SetField(host, "_streamStats", new Dictionary<int, StreamStatsInfo>
            {
                { 1, new StreamStatsInfo() },
            });
            SetField(host, "<BackendStreamStatsCount>k__BackingField", 1);

            host.ClearCaches();

            Assert.Equal(0, host.CachedChannelCount);
            Assert.Empty(GetField<Dictionary<int, StreamStatsInfo>>(host, "_streamStats"));
            Assert.Equal(0, host.BackendStreamStatsCount);
        }

        [Fact]
        public void CollectBackendStreamStats_KeepsOnlyM3uEditorPayloadStats()
        {
            var expected = new StreamStatsInfo { VideoCodec = "hevc" };
            var channels = new[]
            {
                new LiveStreamInfo { StreamId = 10, StreamStats = expected },
                new LiveStreamInfo { StreamId = 11 },
            };

            var actual = M3uEditorTunerHost.CollectBackendStreamStats(channels);

            Assert.Single(actual);
            Assert.Same(expected, actual[10]);
        }

        [Fact]
        public void StreamStatsPayload_PopulatesFastSwitchingMediaMetadata()
        {
            const string json = "{\"stream_id\":42,\"stream_stats\":{\"resolution\":\"1920x1080\",\"video_codec\":\"hevc\",\"audio_codec\":\"aac\",\"audio_channels\":\"5.1\",\"sample_rate\":48000,\"source_fps\":50,\"ffmpeg_output_bitrate\":6000,\"audio_bitrate\":192}}";
            var channel = JsonSerializer.Deserialize<LiveStreamInfo>(json);
            var stats = M3uEditorTunerHost.CollectBackendStreamStats(new[] { channel })[42];

            var source = MakeBareHost().CreateMediaSourceInfo(
                channel.StreamId,
                "http://m3u-editor/live/user/pass/42.ts",
                stats);

            Assert.False(source.SupportsProbing);
            Assert.Equal(0, source.AnalyzeDurationMs);
            Assert.Equal(2, source.MediaStreams.Count);
            var video = source.MediaStreams.Single(stream => stream.Type == MediaStreamType.Video);
            Assert.Equal("hevc", video.Codec);
            Assert.Equal(1920, video.Width);
            Assert.Equal(1080, video.Height);
            Assert.Equal(50, video.RealFrameRate);
            Assert.Equal(6000000, video.BitRate);
            var audio = source.MediaStreams.Single(stream => stream.Type == MediaStreamType.Audio);
            Assert.Equal("aac", audio.Codec);
            Assert.Equal(6, audio.Channels);
            Assert.Equal(48000, audio.SampleRate);
            Assert.Equal(192000, audio.BitRate);
        }

        [Fact]
        public void DirectPlaybackWithoutStats_AllowsProbingAndPopulatesSafeInitialMetadata()
        {
            var config = new PluginConfiguration
            {
                BaseUrl = "http://m3u-editor:36400",
                Username = "user",
                Password = "pass",
                LiveTvOutputFormat = "m3u8",
            };

            var url = M3uEditorTunerHost.BuildStreamUrl(config, 7);
            var source = MakeBareHost().CreateMediaSourceInfo(7, url, null);

            Assert.Equal("http://m3u-editor:36400/live/user/pass/7.m3u8", url);
            Assert.True(source.SupportsProbing);
            Assert.Equal(500, source.AnalyzeDurationMs);
            Assert.Collection(source.MediaStreams,
                video => Assert.Equal("h264", video.Codec),
                audio => Assert.Equal("aac", audio.Codec));
        }

        [Fact]
        public void ApplyChannelLogoVariants_UsesConfiguredLogoVariants()
        {
            var info = new ChannelInfo();

            M3uEditorTunerHost.ApplyChannelLogoVariants(info, "https://example.com/logo.png", true);

            Assert.Equal("https://example.com/logo.png", info.ImageUrl);
            Assert.Equal(info.ImageUrl, info.LightLogoImageUrl);
            Assert.Equal(info.ImageUrl, info.LightColorLogoImageUrl);
        }

        [Fact]
        public void IsOwnedChannel_UsesTunerIdOrExactNameAndNumberPair()
        {
            var channels = new[]
            {
                new ChannelInfo { TunerChannelId = "42", Number = "7", Name = "News" }
            };

            Assert.True(M3uEditorTunerHost.IsOwnedChannel(channels, "42", null, null));
            Assert.True(M3uEditorTunerHost.IsOwnedChannel(channels, null, "7", "News"));
            Assert.False(M3uEditorTunerHost.IsOwnedChannel(channels, null, "7", "Other"));
            Assert.False(M3uEditorTunerHost.IsOwnedChannel(channels, null, null, "News"));
        }

        [Fact]
        public void TryClearImageInfos_MissingProperty_ReturnsFalse()
        {
            Assert.False(M3uEditorTunerHost.TryClearImageInfos(new object()));
        }

        [Fact]
        public void TryClearImageInfos_PopulatedArray_ClearsImages()
        {
            var item = new ChannelItemWithImages
            {
                ImageInfos = new[] { "cached-logo" },
            };

            Assert.True(M3uEditorTunerHost.TryClearImageInfos(item));
            Assert.Empty(item.ImageInfos);
        }

        private static void SetField(object obj, string name, object value)
        {
            var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(obj, value);
        }

        private static T GetField<T>(object obj, string name)
        {
            var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (T)field.GetValue(obj);
        }

        private sealed class ChannelItemWithImages
        {
            public string[] ImageInfos { get; set; }
        }
    }
}
