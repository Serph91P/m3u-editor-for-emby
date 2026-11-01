using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    [Collection(PluginSingletonCollection.Name)]
    public class M3uEditorTunerHostTests
    {
        [Fact]
        public async System.Threading.Tasks.Task OpenedStreams_IsolateLifecycleIdentityWithoutChangingDiscoveryOrSource()
        {
            using (var environment = new PluginTestEnvironment(new PluginConfiguration
            {
                BaseUrl = "https://source.example.test", Username = "synthetic", Password = "synthetic",
                HttpUserAgent = "SyntheticPlayer/1.0"
            }, new LiveTvOptions()))
            {
                environment.CreatePlugin();
                var host = MakeBareHost();
                SetField(host, "_streamStats", new Dictionary<int, StreamStatsInfo>
                {
                    { 42, new StreamStatsInfo { VideoCodec = "h264", Resolution = "1920x1080", AudioCodec = "aac" } }
                });
                typeof(BaseTunerHost).GetField("Logger", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(host, DispatchProxy.Create<ILogger, TestLogger>());
                var tuner = new TunerHostInfo { Id = "synthetic-tuner" };
                var channel = new ChannelInfo { Id = "42", Name = "Synthetic channel" };
                var discoveryMethod = typeof(M3uEditorTunerHost).GetMethod("GetChannelStreamMediaSources", BindingFlags.NonPublic | BindingFlags.Instance);
                var openMethod = typeof(M3uEditorTunerHost).GetMethod("GetChannelStream", BindingFlags.NonPublic | BindingFlags.Instance);
                async System.Threading.Tasks.Task<MediaBrowser.Model.Dto.MediaSourceInfo> Discover()
                {
                    return Assert.Single(await (System.Threading.Tasks.Task<List<MediaBrowser.Model.Dto.MediaSourceInfo>>)discoveryMethod.Invoke(
                        host, new object[] { tuner, null, channel, System.Threading.CancellationToken.None }));
                }
                async System.Threading.Tasks.Task<MediaBrowser.Controller.Library.ILiveStream> Open()
                {
                    return await (System.Threading.Tasks.Task<MediaBrowser.Controller.Library.ILiveStream>)openMethod.Invoke(
                        host, new object[] { tuner, null, channel, "xtream_live_42", System.Threading.CancellationToken.None });
                }
                var discovery = await Discover();
                Assert.Equal("xtream_live_42", discovery.Id);
                var a = await Open();
                var b = await Open();
                try
                {
                    Assert.NotEqual(a.MediaSource.Id, b.MediaSource.Id);
                    foreach (var stream in new[] { a, b })
                    {
                        Assert.Equal(stream.UniqueId, stream.MediaSource.Id);
                        Assert.Equal(discovery.Id, stream.OriginalStreamId);
                        Assert.Equal(discovery.Path, stream.MediaSource.Path);
                        Assert.Equal("SyntheticPlayer/1.0", stream.MediaSource.RequiredHttpHeaders["User-Agent"]);
                        Assert.Equal("mpegts", stream.MediaSource.Container);
                        Assert.True(stream.MediaSource.RequiresOpening);
                        Assert.True(stream.MediaSource.RequiresClosing);
                        Assert.Equal(1920, stream.MediaSource.MediaStreams[0].Width);
                        Assert.Equal("aac", stream.MediaSource.MediaStreams[1].Codec);
                        Assert.False(stream.EnableStreamSharing);
                        await stream.Open(System.Threading.CancellationToken.None);
                    }
                    Assert.Equal("xtream_live_42", (await Discover()).Id);
                    await a.Close();
                    await b.Close();
                    var restarted = await Open();
                    try
                    {
                        Assert.NotEqual(a.MediaSource.Id, restarted.MediaSource.Id);
                        Assert.NotEqual(b.MediaSource.Id, restarted.MediaSource.Id);
                        Assert.Equal(discovery.Id, restarted.OriginalStreamId);
                    }
                    finally { await restarted.Close(); }
                    channel.Id = "43";
                    var other = await Open();
                    try
                    {
                        Assert.Equal("xtream_live_43", other.OriginalStreamId);
                        Assert.NotEqual(b.MediaSource.Id, other.MediaSource.Id);
                        Assert.NotEqual(discovery.Path, other.MediaSource.Path);
                    }
                    finally { await other.Close(); }
                }
                finally { await a.Close(); await b.Close(); }
            }
        }

        private static M3uEditorTunerHost MakeBareHost()
        {
            return (M3uEditorTunerHost)RuntimeHelpers.GetUninitializedObject(
                typeof(M3uEditorTunerHost));
        }

        [Fact]
        public void PluginConfiguration_DefaultLiveTvTunerCountIsZeroSentinel()
        {
            Assert.Equal(0, new PluginConfiguration().LiveTvTunerCount);
        }

        [Theory]
        [InlineData("\"5\"")]
        [InlineData("1.5")]
        [InlineData("1e3")]
        [InlineData("2147483648")]
        [InlineData("null")]
        public void PluginConfiguration_JsonContractRejectsNonInt32TunerCount(string jsonValue)
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PluginConfiguration>(
                "{\"LiveTvTunerCount\":" + jsonValue + "}"));
        }

        [Fact]
        public void PluginConfiguration_JsonContractAcceptsPositiveInt32TunerCount()
        {
            var configuration = JsonSerializer.Deserialize<PluginConfiguration>(
                "{\"LiveTvTunerCount\":2147483647}");

            Assert.Equal(int.MaxValue, configuration.LiveTvTunerCount);
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
        public void ReconcileTunerHosts_SentinelKeepsExistingPositiveTunerCount()
        {
            var options = new LiveTvOptions
            {
                TunerHosts = new[]
                {
                    new TunerHostInfo
                    {
                        Type = M3uEditorTunerHost.TunerType,
                        Id = M3uEditorTunerHost.StableTunerId,
                        TunerCount = 2,
                    }
                }
            };

            var changed = M3uEditorTunerHost.ReconcileTunerHosts(options, true, 0);

            Assert.False(changed);
            Assert.Equal(2, options.TunerHosts[0].TunerCount);
        }

        [Fact]
        public void ReconcileTunerHosts_PositiveOverrideAppliesOnlyToPluginTuner()
        {
            var unrelated = new TunerHostInfo { Type = "native-tuner", Id = "native", TunerCount = 7 };
            var plugin = new TunerHostInfo { Type = M3uEditorTunerHost.TunerType, Id = "plugin", TunerCount = 2 };
            var options = new LiveTvOptions { TunerHosts = new[] { unrelated, plugin } };

            var changed = M3uEditorTunerHost.ReconcileTunerHosts(options, true, 4);

            Assert.True(changed);
            Assert.Equal(7, options.TunerHosts[0].TunerCount);
            Assert.Equal(4, options.TunerHosts[1].TunerCount);
            Assert.Equal(2, options.TunerHosts.Length);
        }

        [Fact]
        public void ReconcileTunerHosts_ResolvesMissingCountToOneWithoutManualOverride()
        {
            var options = new LiveTvOptions
            {
                TunerHosts = new[]
                {
                    new TunerHostInfo
                    {
                        Type = M3uEditorTunerHost.TunerType,
                        Id = M3uEditorTunerHost.StableTunerId,
                        TunerCount = 0,
                    }
                }
            };

            var changed = M3uEditorTunerHost.ReconcileTunerHosts(options, true, 0);

            Assert.True(changed);
            Assert.Equal(1, options.TunerHosts[0].TunerCount);
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
        public void ReconcileConfiguredTunerHost_AppliesOverrideAndAvoidsRedundantLiveTvSave()
        {
            var startupOptions = new LiveTvOptions();
            var startupEnvironment = CreateEnvironment(startupOptions);

            M3uEditorTunerHost.ReconcileConfiguredTunerHost(
                startupEnvironment.host,
                true,
                3,
                null);

            Assert.Equal(1, startupEnvironment.configManager.SaveConfigurationCalls);
            Assert.Single(startupEnvironment.configManager.LiveTvOptions.TunerHosts);
            Assert.Equal(3, startupEnvironment.configManager.LiveTvOptions.TunerHosts[0].TunerCount);

            var saveOptions = new LiveTvOptions
            {
                TunerHosts = new[]
                {
                    new TunerHostInfo
                    {
                        Type = M3uEditorTunerHost.TunerType,
                        Id = M3uEditorTunerHost.StableTunerId,
                        TunerCount = 2,
                    },
                    new TunerHostInfo { Type = "native", Id = "native", TunerCount = 9 }
                }
            };

            var saveEnvironment = CreateEnvironment(saveOptions);

            M3uEditorTunerHost.ReconcileConfiguredTunerHost(
                saveEnvironment.host,
                true,
                0,
                null);

            Assert.Equal(0, saveEnvironment.configManager.SaveConfigurationCalls);
            Assert.Equal(2, saveEnvironment.configManager.LiveTvOptions.TunerHosts.Length);
            Assert.Equal(9, saveEnvironment.configManager.LiveTvOptions.TunerHosts[1].TunerCount);

            M3uEditorTunerHost.ReconcileConfiguredTunerHost(
                saveEnvironment.host,
                true,
                0,
                null);

            Assert.Equal(0, saveEnvironment.configManager.SaveConfigurationCalls);
            Assert.Equal(2, saveEnvironment.configManager.LiveTvOptions.TunerHosts.Length);
        }

        [Fact]
        public void PluginConstructor_AppliesPersistedTunerOverrideAtStartupWithoutChangingOtherTuners()
        {
            var pluginTuner = new TunerHostInfo
            {
                Type = M3uEditorTunerHost.TunerType,
                Id = M3uEditorTunerHost.StableTunerId,
                TunerCount = 2,
            };
            var sourceOne = new TunerHostInfo { Type = "hdhomerun", Id = "source-one", TunerCount = 6 };
            var sourceTwo = new TunerHostInfo { Type = "m3u", Id = "source-two", TunerCount = 11 };
            var liveTvOptions = new LiveTvOptions
            {
                TunerHosts = new[] { sourceOne, pluginTuner, sourceTwo },
            };

            using (var environment = new PluginTestEnvironment(
                new PluginConfiguration { EnableLiveTv = true, LiveTvTunerCount = 4 },
                liveTvOptions))
            {
                var plugin = environment.CreatePlugin();

                Assert.Equal(4, plugin.Configuration.LiveTvTunerCount);
                Assert.Equal(1, environment.ConfigurationManager.SaveConfigurationCalls);
                Assert.Collection(
                    environment.ConfigurationManager.LiveTvOptions.TunerHosts,
                    tuner => Assert.Same(sourceOne, tuner),
                    tuner => Assert.Same(pluginTuner, tuner),
                    tuner => Assert.Same(sourceTwo, tuner));
                Assert.Equal(6, sourceOne.TunerCount);
                Assert.Equal(4, pluginTuner.TunerCount);
                Assert.Equal(11, sourceTwo.TunerCount);
            }
        }

        [Fact]
        public void UpdateConfiguration_PersistsThenImmediatelyReconcilesOnlyPluginTuner()
        {
            var pluginTuner = new TunerHostInfo
            {
                Type = M3uEditorTunerHost.TunerType,
                Id = M3uEditorTunerHost.StableTunerId,
                TunerCount = 2,
            };
            var unrelated = new TunerHostInfo { Type = "native", Id = "source", TunerCount = 9 };
            using (var environment = new PluginTestEnvironment(
                new PluginConfiguration { EnableLiveTv = true, LiveTvTunerCount = 0 },
                new LiveTvOptions { TunerHosts = new[] { unrelated, pluginTuner } }))
            {
                var plugin = environment.CreatePlugin();
                environment.ResetCounts();
                var replacement = new PluginConfiguration
                {
                    BaseUrl = "https://updated.example",
                    EnableLiveTv = true,
                    LiveTvTunerCount = 5,
                };

                plugin.UpdateConfiguration(replacement);

                Assert.Same(replacement, plugin.Configuration);
                Assert.Same(replacement, environment.Plugin.LastSavedConfiguration);
                Assert.Equal(1, environment.Plugin.SaveConfigurationCalls);
                Assert.Equal(1, environment.ConfigurationManager.SaveConfigurationCalls);
                Assert.Same(unrelated, environment.ConfigurationManager.LiveTvOptions.TunerHosts[0]);
                Assert.Equal(9, unrelated.TunerCount);
                Assert.Equal(5, pluginTuner.TunerCount);

                plugin.UpdateConfiguration(replacement);

                Assert.Equal(2, environment.Plugin.SaveConfigurationCalls);
                Assert.Equal(1, environment.ConfigurationManager.SaveConfigurationCalls);
            }
        }

        [Fact]
        public void UpdateConfiguration_WithNegativeLiveTvTunerCount_ThrowsBeforePersistenceOrReconciliation()
        {
            var original = new PluginConfiguration { EnableLiveTv = true, LiveTvTunerCount = 0 };
            var pluginTuner = new TunerHostInfo
            {
                Type = M3uEditorTunerHost.TunerType,
                Id = M3uEditorTunerHost.StableTunerId,
                TunerCount = 2,
            };
            using (var environment = new PluginTestEnvironment(
                original,
                new LiveTvOptions { TunerHosts = new[] { pluginTuner } }))
            {
                var plugin = environment.CreatePlugin();
                environment.ResetCounts();

                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    plugin.UpdateConfiguration(new PluginConfiguration { LiveTvTunerCount = -1 }));

                Assert.Same(original, plugin.Configuration);
                Assert.Equal(0, environment.Plugin.SaveConfigurationCalls);
                Assert.Equal(0, environment.ConfigurationManager.SaveConfigurationCalls);
                Assert.Equal(2, pluginTuner.TunerCount);
            }
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
        public void IsOwnedChannel_RequiresGlobalIdentityAndKnownTuner()
        {
            var channels = new[]
            {
                new ChannelInfo { Id = "global-owned-42", TunerHostId = "owned", TunerChannelId = "42", Number = "7", Name = "News" }
            };

            Assert.True(M3uEditorTunerHost.IsOwnedChannel(channels, "global-owned-42"));
            Assert.False(M3uEditorTunerHost.IsOwnedChannel(channels, "42"));
            Assert.False(M3uEditorTunerHost.IsOwnedChannel(channels, "foreign-42"));
            Assert.False(M3uEditorTunerHost.IsOwnedChannel(channels, null));
            channels[0].TunerHostId = null;
            Assert.False(M3uEditorTunerHost.IsOwnedChannel(channels, "global-owned-42"));
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

        private static (IApplicationHost host, TestConfigurationManager configManager) CreateEnvironment(
            LiveTvOptions options)
        {
            var configManagerProxy = DispatchProxy.Create<IConfigurationManager, TestConfigurationManager>();
            var configManager = (TestConfigurationManager)configManagerProxy;
            configManager.LiveTvOptions = options;

            var hostProxy = DispatchProxy.Create<IApplicationHost, TestApplicationHost>();
            var host = (TestApplicationHost)hostProxy;
            host.ConfigurationManager = configManagerProxy;

            return (hostProxy, configManager);
        }

        [Fact]
        public void Issue130_ExplicitArtworkRecoveryMutatesOnlyProvenGlobalChannels()
        {
            var tuner = new TunerHostInfo { Id = "owned-tuner", Type = M3uEditorTunerHost.TunerType };
            var environment = CreateEnvironment(new LiveTvOptions { TunerHosts = new[] { tuner } });
            var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, ArtworkLibrary>();
            var app = DispatchProxy.Create<MediaBrowser.Controller.IServerApplicationHost, TestApplicationHost>();
            ((TestApplicationHost)app).ConfigurationManager = ((TestApplicationHost)environment.host).ConfigurationManager;
            ((TestApplicationHost)app).LibraryManager = library;
            var host = MakeBareHost();
            SetField(host, "_applicationHost", app);
            var globalId = (string)typeof(BaseTunerHost).GetMethod("CreateEmbyChannelId", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(host, new object[] { tuner, "42" });
            var owned = new MediaBrowser.Controller.LiveTv.LiveTvChannel { ExternalId = globalId, Name = "Synthetic", Number = "7",
                ImageInfos = new[] { new MediaBrowser.Controller.Entities.ItemImageInfo() } };
            var foreign = new MediaBrowser.Controller.LiveTv.LiveTvChannel { ExternalId = "42", Name = "Other", Number = "8",
                ImageInfos = new[] { new MediaBrowser.Controller.Entities.ItemImageInfo() } };
            var collision = new MediaBrowser.Controller.LiveTv.LiveTvChannel { ExternalId = "foreign-other", Name = "Synthetic", Number = "7",
                ImageInfos = new[] { new MediaBrowser.Controller.Entities.ItemImageInfo() } };
            var unknown = new MediaBrowser.Controller.LiveTv.LiveTvChannel { Name = "Synthetic", Number = "7",
                ImageInfos = new[] { new MediaBrowser.Controller.Entities.ItemImageInfo() } };
            ((ArtworkLibrary)library).Items = new MediaBrowser.Controller.Entities.BaseItem[] { owned, foreign, collision, unknown };
            SetField(host, "_cachedChannels", new List<ChannelInfo> { new ChannelInfo { Id = globalId,
                TunerHostId = tuner.Id, TunerChannelId = "42", Name = "Synthetic", Number = "7" } });
            var result = host.ClearCachedChannelArtwork();
            Assert.True(result.Success, result.Message);
            Assert.Empty(owned.ImageInfos);
            Assert.Single(foreign.ImageInfos);
            Assert.Single(collision.ImageInfos);
            Assert.Single(unknown.ImageInfos);
            Assert.Same(owned, Assert.Single(((ArtworkLibrary)library).Updated));
            host.ClearCaches();
            Assert.Single(foreign.ImageInfos);
        }

        private class ArtworkLibrary : DispatchProxy
        {
            public MediaBrowser.Controller.Entities.BaseItem[] Items { get; set; }
            public List<object> Updated { get; } = new List<object>();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetItemList") return Items;
                if (method.Name == "UpdateItem") Updated.Add(args[0]);
                return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }

        private class TestApplicationHost : DispatchProxy
        {
            public IConfigurationManager ConfigurationManager { get; set; }
            public MediaBrowser.Controller.Library.ILibraryManager LibraryManager { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "Resolve" && targetMethod.IsGenericMethod &&
                    targetMethod.GetGenericArguments()[0] == typeof(MediaBrowser.Controller.Library.ILibraryManager)) return LibraryManager;
                if (string.Equals(targetMethod.Name, "Resolve", StringComparison.Ordinal) &&
                    targetMethod.IsGenericMethod &&
                    targetMethod.GetGenericArguments().Length == 1 &&
                    targetMethod.GetGenericArguments()[0] == typeof(IConfigurationManager))
                {
                    return ConfigurationManager;
                }

                if (targetMethod.ReturnType == typeof(void))
                {
                    return null;
                }

                return targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        private class TestConfigurationManager : DispatchProxy
        {
            public int SaveConfigurationCalls { get; private set; }
            public LiveTvOptions LiveTvOptions { get; set; }

            public void Reset()
            {
                SaveConfigurationCalls = 0;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod.Name, "GetConfiguration", StringComparison.Ordinal) &&
                    args.Length == 1 &&
                    string.Equals((string)args[0], "livetv", StringComparison.Ordinal))
                {
                    return LiveTvOptions;
                }

                if (string.Equals(targetMethod.Name, "SaveConfiguration", StringComparison.Ordinal) &&
                    args.Length == 2 &&
                    string.Equals((string)args[0], "livetv", StringComparison.Ordinal) &&
                    args[1] is LiveTvOptions updated)
                {
                    ++SaveConfigurationCalls;
                    LiveTvOptions = updated;
                }

                if (targetMethod.ReturnType == typeof(void))
                {
                    return null;
                }

                return targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        [Theory]
        [InlineData("<EnableEpg>false</EnableEpg>", EpgSourceMode.Disabled)]
        [InlineData("<EnableEpg>true</EnableEpg>", EpgSourceMode.XtreamServer)]
        [InlineData("", EpgSourceMode.XtreamServer)]
        [InlineData("<EnableEpg>false</EnableEpg><EpgSource>XtreamServer</EpgSource>", EpgSourceMode.XtreamServer)]
        [InlineData("<EpgSource>CustomUrl</EpgSource><EnableEpg>false</EnableEpg>", EpgSourceMode.CustomUrl)]
        [InlineData("<EpgSource>Disabled</EpgSource><EnableEpg>true</EnableEpg>", EpgSourceMode.Disabled)]
        public async System.Threading.Tasks.Task Issue134_RealXmlIsConsumedByPluginStartupAndStableOnReload(string elements, EpgSourceMode expected)
        {
            using (var env = new PluginTestEnvironment(new PluginConfiguration(), new LiveTvOptions()))
            {
                env.XmlSerializer.ConfigurationXml = "<PluginConfiguration>" + elements + "</PluginConfiguration>";
                var plugin = env.CreatePlugin();
                Assert.True(env.XmlSerializer.DeserializeCalls > 0);
                Assert.Equal(expected, plugin.Configuration.EpgSource);
                Assert.Equal(expected != EpgSourceMode.Disabled, MakeBareHost().SupportsGuideData(new TunerHostInfo()));
                if (expected == EpgSourceMode.Disabled)
                {
                    var handler = new FakeHttpHandler();
                    using (var service = new LiveTvService(DispatchProxy.Create<ILogger, TestLogger>(),
                        _ => new System.Net.Http.HttpClient(handler, false), () => plugin.Configuration, () => { }))
                    {
                        Assert.Empty(await service.FetchEpgForChannelCachedAsync(42, System.Threading.CancellationToken.None));
                        Assert.Empty(handler.ReceivedUrls);
                    }
                }
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
                using (var text = new StringWriter())
                {
                    serializer.Serialize(text, plugin.Configuration);
                    using (var reader = new StringReader(text.ToString()))
                        Assert.Equal(expected, ((PluginConfiguration)serializer.Deserialize(reader)).EpgSource);
                }
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Issue132_ConfigurationUpdateRejectsBlankCustomSource(string url)
        {
            using (var env = new PluginTestEnvironment(new PluginConfiguration(), new LiveTvOptions()))
            {
                var plugin = env.CreatePlugin();
                var error = Assert.Throws<ArgumentException>(() => plugin.UpdateConfiguration(new PluginConfiguration
                    { EpgSource = EpgSourceMode.CustomUrl, CustomEpgUrl = url }));
                Assert.Contains("Custom EPG URL", error.Message);
                Assert.Equal(EpgSourceMode.XtreamServer, plugin.Configuration.EpgSource);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Issue124_ActualConfigurationUpdateInvalidatesWarmedLiveState()
        {
            var original = new PluginConfiguration { BaseUrl = "https://editor.example.test", Username = "user", Password = "pass",
                EpgSource = EpgSourceMode.CustomUrl, CustomEpgUrl = "https://custom.example.test/old.xml" };
            using (var env = new PluginTestEnvironment(original, new LiveTvOptions()))
            {
                var plugin = env.CreatePlugin();
                var handler = new FakeHttpHandler();
                handler.RespondWithSequence("get_live_streams", Enumerable.Repeat("[{\"stream_id\":42,\"name\":\"Synthetic\",\"epg_channel_id\":\"guide\"}]", 4));
                var start = DateTime.UtcNow.AddMinutes(-1).ToString("yyyyMMddHHmmss");
                var stop = DateTime.UtcNow.AddHours(1).ToString("yyyyMMddHHmmss");
                var xml = "<tv><programme channel=\"guide\" start=\"" + start + " +0000\" stop=\"" + stop + " +0000\"><title>{0}</title></programme></tv>";
                handler.RespondWith("old.xml", string.Format(xml, "Old"));
                handler.RespondWith("new.xml", string.Format(xml, "New"));
                using (var service = new LiveTvService(DispatchProxy.Create<ILogger, TestLogger>(),
                    _ => new System.Net.Http.HttpClient(handler, false), () => plugin.Configuration, () => { }))
                {
                    typeof(Plugin).GetField("_liveTvService", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(plugin, service);
                    Assert.Equal("Old", Assert.Single(await service.FetchEpgForChannelCachedAsync(42, System.Threading.CancellationToken.None)).Title);
                    plugin.UpdateConfiguration(new PluginConfiguration { BaseUrl = original.BaseUrl, Username = "new-user", Password = "pass",
                        EpgSource = EpgSourceMode.CustomUrl, CustomEpgUrl = "https://custom.example.test/new.xml" });
                    Assert.False(service.HasXmltvCache);
                    Assert.Equal(0, service.PerChannelEpgCacheCount);
                    Assert.Equal("New", Assert.Single(await service.FetchEpgForChannelCachedAsync(42, System.Threading.CancellationToken.None)).Title);
                    var before = handler.ReceivedUrls.Count;
                    plugin.Configuration.ManagedLastError = "managed-only";
                    plugin.UpdateConfiguration(plugin.Configuration);
                    Assert.Equal("New", Assert.Single(await service.FetchEpgForChannelCachedAsync(42, System.Threading.CancellationToken.None)).Title);
                    Assert.Equal(before, handler.ReceivedUrls.Count);
                }
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Issue124And125_NativeTunerRefreshAndInflightFence()
        {
            var config = new PluginConfiguration { BaseUrl = "https://editor.example.test", Username = "user", Password = "pass", IncludeAdultChannels = true };
            using (var env = new PluginTestEnvironment(config, new LiveTvOptions()))
            {
                var plugin = env.CreatePlugin();
                var handler = new TunerBarrierHandler();
                using (var service = new LiveTvService(DispatchProxy.Create<ILogger, TestLogger>(), _ => new System.Net.Http.HttpClient(handler, false), () => plugin.Configuration, () => { }))
                {
                    typeof(Plugin).GetField("_liveTvService", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(plugin, service);
                    var host = MakeBareHost();
                    typeof(BaseTunerHost).GetField("Logger", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host, DispatchProxy.Create<ILogger, TestLogger>());
                    var instanceField = typeof(M3uEditorTunerHost).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
                    var previousHost = instanceField.GetValue(null);
                    instanceField.SetValue(null, host);
                    try
                    {
                        var method = typeof(M3uEditorTunerHost).GetMethod("GetChannelsInternal", BindingFlags.Instance | BindingFlags.NonPublic);
                        System.Threading.Tasks.Task<List<ChannelInfo>> Scan() => (System.Threading.Tasks.Task<List<ChannelInfo>>)method.Invoke(host,
                            new object[] { new TunerHostInfo { Id = "owned-tuner" }, System.Threading.CancellationToken.None });
                        Assert.Equal("Alpha Beta Synthetic", Assert.Single(await Scan()).Name);
                        config.EnableChannelNameCleaning = true; config.ChannelRemoveTerms = "Alpha,Beta"; config.LiveTvOutputFormat = "m3u8";
                        config.Username = "new-user";
                        plugin.UpdateConfiguration(config);
                        Assert.Equal(0, host.CachedChannelCount);
                        Assert.Equal("Synthetic", Assert.Single(await Scan()).Name);
                        Assert.Equal("https://editor.example.test/live/new-user/pass/42.m3u8", M3uEditorTunerHost.BuildStreamUrl(plugin.Configuration, 42));
                        config.SelectedLiveCategoryIds = new[] { 99 };
                        plugin.UpdateConfiguration(config);
                        Assert.Empty(await Scan());
                        config.SelectedLiveCategoryIds = new int[0]; plugin.UpdateConfiguration(config);
                        handler.Block = true;
                        var pending = Scan();
                        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        service.InvalidateCache(); host.ClearCaches();
                        handler.Release.TrySetResult(true);
                        await pending.WaitAsync(TimeSpan.FromSeconds(3));
                        Assert.Equal(0, host.CachedChannelCount);
                        Assert.Single(await Scan());
                    }
                    finally { instanceField.SetValue(null, previousHost); }
                }
            }
        }

        private sealed class TunerBarrierHandler : System.Net.Http.HttpMessageHandler
        {
            public bool Block;
            public System.Threading.Tasks.TaskCompletionSource<bool> Entered = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            public System.Threading.Tasks.TaskCompletionSource<bool> Release = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken token)
            {
                if (Block && request.RequestUri.ToString().Contains("get_live_categories"))
                { Block = false; Entered.TrySetResult(true); await Release.Task.WaitAsync(token); }
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(
                    request.RequestUri.ToString().Contains("get_live_streams") ? "[{\"stream_id\":42,\"name\":\"Alpha Beta Synthetic\",\"category_id\":1}]" : "[]") };
            }
        }

        private sealed class PluginTestEnvironment : IDisposable
        {
            private readonly TempDirectory _directory;
            private readonly Plugin _previousPlugin;
            private readonly IApplicationPaths _applicationPaths;
            private readonly MediaBrowser.Model.Serialization.IXmlSerializer _xmlSerializer;
            private readonly ILogManager _logManager;
            private readonly IApplicationHost _applicationHost;

            public PluginTestEnvironment(PluginConfiguration configuration, LiveTvOptions liveTvOptions)
            {
                _directory = new TempDirectory();
                File.WriteAllText(
                    Path.Join(_directory.Path, "Emby.Xtr" + "eam.Plugin.xml"),
                    "test configuration marker");

                _previousPlugin = global::Emby.M3uEditor.Plugin.Plugin.InstanceOrNull;

                _applicationPaths = DispatchProxy.Create<IApplicationPaths, TestApplicationPaths>();
                ((TestApplicationPaths)_applicationPaths).RootPath = _directory.Path;

                _xmlSerializer = DispatchProxy.Create<MediaBrowser.Model.Serialization.IXmlSerializer, TestXmlSerializer>();
                XmlSerializer = (TestXmlSerializer)_xmlSerializer;
                XmlSerializer.ConfigurationToLoad = configuration;

                var logger = DispatchProxy.Create<ILogger, TestLogger>();
                _logManager = DispatchProxy.Create<ILogManager, TestLogManager>();
                ((TestLogManager)_logManager).Logger = logger;

                var tunerEnvironment = CreateEnvironment(liveTvOptions);
                _applicationHost = tunerEnvironment.host;
                ConfigurationManager = tunerEnvironment.configManager;
            }

            public TestConfigurationManager ConfigurationManager { get; }

            public TestXmlSerializer XmlSerializer { get; }

            public PersistingTestPlugin Plugin { get; private set; }

            public Plugin CreatePlugin()
            {
                Plugin = new PersistingTestPlugin(
                    _applicationPaths,
                    _xmlSerializer,
                    _logManager,
                    _applicationHost);
                return Plugin;
            }

            public void ResetCounts()
            {
                ConfigurationManager.Reset();
                XmlSerializer.Reset();
                Plugin?.Reset();
            }

            public void Dispose()
            {
                var instanceField = typeof(Plugin).GetField(
                    "_instance",
                    BindingFlags.NonPublic | BindingFlags.Static);
                instanceField.SetValue(null, _previousPlugin);
                _directory.Dispose();
            }
        }

        private sealed class PersistingTestPlugin : Plugin
        {
            public PersistingTestPlugin(
                IApplicationPaths applicationPaths,
                MediaBrowser.Model.Serialization.IXmlSerializer xmlSerializer,
                ILogManager logManager,
                IApplicationHost applicationHost)
                : base(applicationPaths, xmlSerializer, logManager, applicationHost)
            {
            }

            public int SaveConfigurationCalls { get; private set; }

            public PluginConfiguration LastSavedConfiguration { get; private set; }

            public override void SaveConfiguration()
            {
                ++SaveConfigurationCalls;
                LastSavedConfiguration = Configuration;
            }

            public void Reset()
            {
                SaveConfigurationCalls = 0;
                LastSavedConfiguration = null;
            }
        }

        private class TestApplicationPaths : DispatchProxy
        {
            public string RootPath { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.ReturnType == typeof(string))
                {
                    return RootPath;
                }

                return targetMethod.ReturnType == typeof(void)
                    ? null
                    : (targetMethod.ReturnType.IsValueType
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null);
            }
        }

        private class TestXmlSerializer : DispatchProxy
        {
            public string ConfigurationXml { get; set; }
            public int DeserializeCalls { get; private set; }
            public PluginConfiguration ConfigurationToLoad { get; set; }
            public int SerializeToFileCalls { get; private set; }
            public object LastSerializedConfiguration { get; private set; }

            public void Reset()
            {
                SerializeToFileCalls = 0;
                LastSerializedConfiguration = null;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod.Name, "DeserializeFromFile", StringComparison.Ordinal))
                {
                    DeserializeCalls++;
                    if (ConfigurationXml != null)
                    {
                        using (var reader = new StringReader(ConfigurationXml))
                            return new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader);
                    }
                    return ConfigurationToLoad;
                }

                if (string.Equals(targetMethod.Name, "SerializeToFile", StringComparison.Ordinal))
                {
                    ++SerializeToFileCalls;
                    LastSerializedConfiguration = args[0];
                    return null;
                }

                return targetMethod.ReturnType == typeof(void)
                    ? null
                    : (targetMethod.ReturnType.IsValueType
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null);
            }
        }

        private class TestLogManager : DispatchProxy
        {
            public ILogger Logger { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod.Name, "GetLogger", StringComparison.Ordinal))
                {
                    return Logger;
                }

                return targetMethod.ReturnType == typeof(void)
                    ? null
                    : (targetMethod.ReturnType.IsValueType
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null);
            }
        }

        private class TestLogger : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.ReturnType == typeof(void)
                    ? null
                    : (targetMethod.ReturnType.IsValueType
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null);
            }
        }
    }
}
