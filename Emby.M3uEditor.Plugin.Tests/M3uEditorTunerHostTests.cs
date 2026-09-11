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

        private class TestApplicationHost : DispatchProxy
        {
            public IConfigurationManager ConfigurationManager { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
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
