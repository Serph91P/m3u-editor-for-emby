using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class LiveTvIssueContractTests
    {
        private const string User = "synthetic&user/#?+";
        private const string Password = "synthetic+p&x#tail/?";

        [Theory]
        [InlineData("ts")]
        [InlineData("m3u8")]
        public async Task Issue123_PlaybackAndGuideCredentialsRoundTrip(string format)
        {
            var config = new PluginConfiguration { BaseUrl = "https://editor.example.test", Username = User,
                Password = Password, LiveTvOutputFormat = format, IncludeAdultChannels = true };
            var handler = new FakeHttpHandler();
            handler.RespondWithSequence("action=get_live_streams", Enumerable.Repeat(
                "[{\"stream_id\":42,\"name\":\"Synthetic\",\"epg_channel_id\":\"guide\"}]", 4));
            handler.RespondWith("action=get_live_categories", "[]");
            handler.RespondWith("/xmltv.php?", "unavailable", HttpStatusCode.ServiceUnavailable);
            handler.RespondWith("action=get_simple_data_table", "{\"epg_listings\":[]}");
            using (var service = new LiveTvService(new NullLogger(), _ => new HttpClient(handler, false), () => config, () => { }))
            {
                var playlist = await service.GetM3UPlaylistAsync(CancellationToken.None);
                AssertPlayback(new Uri(playlist.Split('\n').Single(line => line.StartsWith("https://")).Trim()), format);
                AssertPlayback(new Uri(M3uEditorTunerHost.BuildStreamUrl(config, 42)), format);
                await service.FetchEpgForChannelCachedAsync(42, CancellationToken.None);
                foreach (var url in handler.ReceivedUrls)
                {
                    var uri = new Uri(url);
                    Assert.Empty(uri.Fragment);
                    var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split(new[] { '=' }, 2))
                        .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1].Replace("+", " ")));
                    Assert.Equal(User, query["username"]);
                    Assert.Equal(Password, query["password"]);
                }
                Assert.Contains(handler.ReceivedUrls, url => url.Contains("/xmltv.php?"));
                Assert.Contains(handler.ReceivedUrls, url => url.Contains("action=get_simple_data_table"));
                handler.AssertNoUnhandledRequests();
            }
        }

        [Theory]
        [InlineData(" +0000")]
        [InlineData(" +0230")]
        [InlineData(" -0530")]
        [InlineData("")]
        public void Issue128_MinutePrecisionReachesNativeProgram(string zone)
        {
            var full = Emby.M3uEditor.Plugin.Client.XmltvParser.ParseXmltvTimestamp("20261031120000" + zone);
            Assert.NotEqual(0, full);
            Assert.Equal(full, Emby.M3uEditor.Plugin.Client.XmltvParser.ParseXmltvTimestamp("202610311200" + zone));
            var xml = "<tv><programme channel=\"guide\" start=\"202610311200" + zone +
                "\" stop=\"202610311300" + zone + "\"><title>Synthetic</title></programme></tv>";
            using (var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)))
            {
                var program = Assert.Single(Emby.M3uEditor.Plugin.Client.XmltvParser.Parse(stream, null, null)["guide"]);
                var native = M3uEditorTunerHost.BuildProgramInfo(program, 42, "42", "Synthetic", "");
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(full).UtcDateTime, native.StartDate);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(full + 3600).UtcDateTime, native.EndDate);
            }
            Assert.Equal(0, Emby.M3uEditor.Plugin.Client.XmltvParser.ParseXmltvTimestamp("202602301200" + zone));
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("hevc", false)]
        [InlineData("h264", false)]
        [InlineData(null, false)]
        public void Issue129_ProbeAndCoverageUseUsableCodecEvidence(string codec, bool probes)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { stream_id = 42,
                stream_stats = new { video_codec = codec, audio_codec = "aac" } });
            var channel = System.Text.Json.JsonSerializer.Deserialize<LiveStreamInfo>(json);
            var host = (M3uEditorTunerHost)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(M3uEditorTunerHost));
            var source = host.CreateMediaSourceInfo(42, "https://editor.example.test/live/42.ts", channel.StreamStats);
#pragma warning disable CS0612
            Assert.Equal(probes, source.SupportsProbing);
#pragma warning restore CS0612
            Assert.Equal(probes ? 0 : 1, M3uEditorTunerHost.CollectBackendStreamStats(new[] { channel }).Count);
            Assert.DoesNotContain(source.MediaStreams, stream => string.IsNullOrWhiteSpace(stream.Codec));
            if (codec == null) Assert.DoesNotContain(source.MediaStreams, stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Issue132_BlankCustomSourceNeverRequestsProvider(string url)
        {
            var config = new PluginConfiguration { EpgSource = EpgSourceMode.CustomUrl, CustomEpgUrl = url };
            var handler = new FakeHttpHandler();
            using (var service = new LiveTvService(new NullLogger(), _ => new HttpClient(handler, false), () => config, () => { }))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.FetchEpgForChannelCachedAsync(42, CancellationToken.None));
                Assert.Contains("Custom EPG URL", error.Message);
                Assert.Empty(handler.ReceivedUrls);
                Assert.False(service.HasXmltvCache);
                Assert.Equal(0, service.PerChannelEpgCacheCount);
            }
        }

        [Theory]
        [InlineData("channels")]
        [InlineData("categories")]
        [InlineData("xmltv")]
        [InlineData("json")]
        public async Task Issue131_CallerCancellationReachesActiveHttp(string phase)
        {
            var config = new PluginConfiguration { BaseUrl = "https://editor.example.test", Username = "user", Password = "pass" };
            var handler = new CancelHandler(phase);
            var saves = 0;
            using (var service = new LiveTvService(new NullLogger(), _ => new HttpClient(handler, false), () => config, () => saves++))
            using (var cts = new CancellationTokenSource())
            {
                Task task = phase == "channels" ? service.GetFilteredChannelsAsync(cts.Token)
                    : phase == "categories" ? service.GetLiveCategoriesAsync(cts.Token)
                    : service.FetchEpgForChannelCachedAsync(42, cts.Token);
                await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var before = saves;
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(handler.ReceivedToken.IsCancellationRequested);
                Assert.Equal(before, saves);
                Assert.False(service.HasXmltvCache);
                Assert.Equal(0, service.PerChannelEpgCacheCount);
            }
        }

        private sealed class CancelHandler : HttpMessageHandler
        {
            private readonly string _phase;
            public CancelHandler(string phase) { _phase = phase; }
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken ReceivedToken { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var url = request.RequestUri.ToString();
                var target = _phase == "channels" ? "action=get_live_streams" : _phase == "categories" ? "action=get_live_categories"
                    : _phase == "xmltv" ? "/xmltv.php?" : "action=get_simple_data_table";
                if (url.Contains(target))
                {
                    ReceivedToken = token;
                    Entered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return new HttpResponseMessage(url.Contains("xmltv.php") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent("[{\"stream_id\":42,\"name\":\"Synthetic\"}]") };
            }
        }

        private static void AssertPlayback(Uri uri, string format)
        {
            Assert.Empty(uri.Query);
            Assert.Empty(uri.Fragment);
            var segments = uri.AbsolutePath.Split('/');
            Assert.Equal(5, segments.Length);
            Assert.Equal(User, Uri.UnescapeDataString(segments[2]));
            Assert.Equal(Password, Uri.UnescapeDataString(segments[3]));
            Assert.Equal("42." + format, segments[4]);
        }

        private sealed class NullLogger : ILogger
        {
            public void Info(string message, params object[] args) { }
            public void Warn(string message, params object[] args) { }
            public void Error(string message, params object[] args) { }
            public void Debug(string message, params object[] args) { }
            public void Fatal(string message, params object[] args) { }
            public void ErrorException(string message, Exception exception, params object[] args) { }
            public void FatalException(string message, Exception exception, params object[] args) { }
            public void Log(LogSeverity severity, string message, params object[] args) { }
            public void LogMultiline(string message, LogSeverity severity, System.Text.StringBuilder text) { }
            public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
        }
    }
}
