using System;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class DirectLiveTvTests
    {
        [Fact]
        public async Task M3uEditorResponses_GenerateDirectPlaylistAndEpg()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const string channels = "[{\"num\":7,\"name\":\"News HD\",\"stream_id\":42," +
                "\"stream_icon\":\"https://editor.example/news.png\",\"epg_channel_id\":\"news.example\"," +
                "\"category_id\":5,\"stream_stats\":{\"resolution\":\"1920x1080\",\"video_codec\":\"h264\"}}]";
            var epg = "{\"epg_listings\":[{\"id\":\"programme-1\",\"epg_id\":\"news.example\"," +
                "\"title\":\"TmV3cyBBdCBOb29u\",\"description\":\"RGFpbHkgYnVsbGV0aW4=\"," +
                "\"start_timestamp\":" + (now - 60) + ",\"stop_timestamp\":" + (now + 3600) + "}]}";
            var handler = new FakeHttpHandler();
            handler.RespondWithSequence("action=get_live_streams", new[] { channels, channels });
            handler.RespondWith("action=get_live_categories", "[{\"category_id\":5,\"category_name\":\"News\"}]");
            handler.RespondWith("action=get_simple_data_table&stream_id=42", epg);
            var instanceField = typeof(Plugin).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
            var previous = Plugin.InstanceOrNull;
            var plugin = (TestPlugin)RuntimeHelpers.GetUninitializedObject(typeof(TestPlugin));
            plugin.SetConfiguration(new PluginConfiguration
            {
                BaseUrl = "http://editor.example",
                Username = "user",
                Password = "pass",
                EnableLiveTv = true,
                EpgSource = EpgSourceMode.XtreamServer,
                IncludeAdultChannels = true,
            });
            instanceField.SetValue(null, plugin);

            try
            {
                using (var service = new LiveTvService(
                    new NullLogger(),
                    _ => new HttpClient(handler, false)))
                {
                    var playlist = await service.GetM3UPlaylistAsync(CancellationToken.None);
                    var xmltv = await service.GetXmltvEpgAsync(CancellationToken.None);

                    Assert.Contains("tvg-id=\"news.example\"", playlist);
                    Assert.Contains("group-title=\"News\"", playlist);
                    Assert.Contains("http://editor.example/live/user/pass/42.ts", playlist);
                    Assert.Contains("<channel id=\"news.example\">", xmltv);
                    Assert.Contains("<title>News At Noon</title>", xmltv);
                    Assert.Contains("channel=\"news.example\"", xmltv);
                }
            }
            finally
            {
                instanceField.SetValue(null, previous);
            }
        }

        private sealed class TestPlugin : Plugin
        {
            private TestPlugin()
                : base(null, null, null, null)
            {
            }

            public void SetConfiguration(PluginConfiguration configuration)
            {
                Configuration = configuration;
            }

            public override void SaveConfiguration()
            {
            }
        }

        private sealed class NullLogger : ILogger
        {
            public void Info(string message, params object[] paramList) { }
            public void Error(string message, params object[] paramList) { }
            public void Warn(string message, params object[] paramList) { }
            public void Debug(string message, params object[] paramList) { }
            public void Fatal(string message, params object[] paramList) { }
            public void FatalException(string message, Exception exception, params object[] paramList) { }
            public void ErrorException(string message, Exception exception, params object[] paramList) { }
            public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }
            public void Log(LogSeverity severity, string message, params object[] paramList) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
            public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
        }
    }
}
