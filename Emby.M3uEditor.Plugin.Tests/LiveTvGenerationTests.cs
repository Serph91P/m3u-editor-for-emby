using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class LiveTvGenerationTests
    {
        [Theory]
        [InlineData("xmltv")]
        [InlineData("json")]
        [InlineData("m3u")]
        [InlineData("m3u-channels")]
        [InlineData("export")]
        public async Task Issue125_InflightInvalidationCannotRepopulateAnyCache(string mode)
        {
            var handler = new GuideHandler { JsonFallback = mode == "json" };
            var config = Configuration();
            using (var service = Service(handler, () => config))
            {
                await service.GetFilteredChannelsAsync(CancellationToken.None);
                handler.BarrierTarget = mode == "m3u-channels" ? "get_live_streams" : mode == "m3u" ? "get_live_categories" : mode == "json" ? "get_simple_data_table" : "xmltv.php";
                var old = Read(service, mode);
                await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                service.InvalidateCache();
                handler.Title = "New generation";
                handler.Release.TrySetResult(true);
                await old.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(service.HasCachedM3U);
                Assert.False(service.HasCachedEpgXml);
                Assert.False(service.HasXmltvCache);
                Assert.Equal(0, service.PerChannelEpgCacheCount);
                var next = await Read(service, mode).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Contains("New generation", next);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Issue125_ExportChannelAssociationDoesNotMutateCurrentJsonCache(bool obsoleteExport)
        {
            var handler = new GuideHandler { JsonFallback = true };
            var config = Configuration();
            config.EnableDiagnosticsLogging = true;
            var logger = System.Reflection.DispatchProxy.Create<ILogger, ExportBarrierLogger>();
            var barrier = (ExportBarrierLogger)logger;
            barrier.Enabled = obsoleteExport;
            using (var service = new LiveTvService(logger, _ => new HttpClient(handler, false), () => config, () => { }))
            {
                var old = obsoleteExport ? Task.Run(() => service.GetXmltvEpgAsync(CancellationToken.None)) : null;
                try
                {
                    if (old != null) await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    service.InvalidateCache();
                    handler.GuideId = "guide-b";
                    await service.GetFilteredChannelsAsync(CancellationToken.None);
                    var current = Assert.Single(await service.FetchEpgForChannelCachedAsync(42, CancellationToken.None));
                    Assert.True(string.IsNullOrEmpty(current.ChannelId));

                    barrier.Release.Set();
                    if (old != null)
                    {
                        await old.WaitAsync(TimeSpan.FromSeconds(3));
                        Assert.False(service.HasCachedEpgXml);
                    }
                    Assert.Same(current, Assert.Single(await service.FetchEpgForChannelCachedAsync(42, CancellationToken.None)));
                    Assert.True(string.IsNullOrEmpty(current.ChannelId), "Obsolete export must not mutate the current JSON cache.");

                    var xml = System.Xml.Linq.XDocument.Parse(await service.GetXmltvEpgAsync(CancellationToken.None));
                    Assert.Equal("guide-b", (string)Assert.Single(xml.Root.Elements("channel")).Attribute("id"));
                    Assert.Equal("guide-b", (string)Assert.Single(xml.Root.Elements("programme")).Attribute("channel"));
                    Assert.True(string.IsNullOrEmpty(current.ChannelId), "Current export must also keep its association output-local.");
                    Assert.True(service.HasCachedEpgXml);
                    Assert.Equal(xml.ToString(), System.Xml.Linq.XDocument.Parse(await service.GetXmltvEpgAsync(CancellationToken.None)).ToString());
                }
                finally
                {
                    barrier.Release.Set();
                    if (old != null) await old.WaitAsync(TimeSpan.FromSeconds(3));
                    barrier.Release.Dispose();
                }
            }
        }

        public class ExportBarrierLogger : System.Reflection.DispatchProxy
        {
            public bool Enabled;
            public TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public ManualResetEventSlim Release = new ManualResetEventSlim(false);
            private int _blocked;
            protected override object Invoke(System.Reflection.MethodInfo method, object[] args)
            {
                if (Enabled && args.Length > 0 && args[0] is string text
                    && text.Contains("xmltv-build sourceChannels=") && Interlocked.Exchange(ref _blocked, 1) == 0)
                {
                    Entered.TrySetResult(true);
                    if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Export barrier was not released.");
                }
                return null;
            }
        }

        [Fact]
        public async Task Issue126_SameStreamIdRemappingAndRecyclingEvictGuide()
        {
            var handler = new GuideHandler();
            var config = Configuration();
            using (var service = Service(handler, () => config))
            {
                Assert.Equal("Old generation", Assert.Single(await service.FetchEpgForChannelCachedAsync(42, CancellationToken.None)).Title);
                handler.GuideId = "guide-b"; handler.Title = "Mapped B";
                await service.GetFilteredChannelsAsync(CancellationToken.None);
                Assert.Equal("Mapped B", Assert.Single(await service.FetchEpgForChannelCachedAsync(42, CancellationToken.None)).Title);
                handler.ChannelName = "Recycled"; handler.Title = "Recycled guide";
                await service.GetFilteredChannelsAsync(CancellationToken.None);
                Assert.Equal("Recycled guide", Assert.Single(await service.FetchEpgForChannelCachedAsync(42, CancellationToken.None)).Title);
                handler.NoChannels = true;
                await service.GetFilteredChannelsAsync(CancellationToken.None);
                Assert.Equal(0, service.PerChannelEpgCacheCount);
            }
        }

        [Fact]
        public async Task Issue124_EffectiveConfigChangesInvalidateButManagedAndHashPersistenceDoNot()
        {
            var handler = new GuideHandler();
            var config = Configuration();
            using (var service = Service(handler, () => config))
            {
                await Read(service, "m3u");
                await Read(service, "xmltv");
                var before = handler.Requests;
                config.ManagedLastError = "managed-only";
                config.LastChannelListHash = "persisted hash";
                await Read(service, "m3u"); await Read(service, "xmltv");
                Assert.Equal(before, handler.Requests);
                handler.Title = "New generation";
                config = Configuration();
                config.EpgSource = EpgSourceMode.CustomUrl;
                config.CustomEpgUrl = "https://custom.example.test/xmltv.php";
                config.Username = "new-user"; config.LiveTvOutputFormat = "m3u8";
                config.EnableChannelNameCleaning = true; config.ChannelRemoveTerms = "Old";
                Assert.Contains("New generation", await Read(service, "xmltv"));
                var playlist = await Read(service, "m3u");
                Assert.Contains("/new-user/pass/42.m3u8", playlist);
                Assert.DoesNotContain(",Old channel", playlist);
                Assert.Contains(handler.Urls, url => url.StartsWith(config.CustomEpgUrl));
            }
        }

        [Fact]
        public async Task Issue124_QueuedExportUsesConfigurationAfterItsFetchLock()
        {
            var handler = new GuideHandler();
            var config = Configuration();
            using (var service = Service(handler, () => config))
            {
                await service.GetFilteredChannelsAsync(CancellationToken.None);
                handler.BarrierTarget = "get_live_categories";
                var first = service.GetM3UPlaylistAsync(CancellationToken.None);
                await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var queued = service.GetM3UPlaylistAsync(CancellationToken.None);
                config = Configuration(); config.Username = "new-user";
                service.RefreshConfiguration();
                handler.Release.TrySetResult(true);
                await first.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Contains("/new-user/pass/42.ts", await queued.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Contains("/new-user/pass/42.ts", await service.GetM3UPlaylistAsync(CancellationToken.None));
            }
        }

        private static PluginConfiguration Configuration() => new PluginConfiguration {
            BaseUrl = "https://editor.example.test", Username = "user", Password = "pass", IncludeAdultChannels = true };
        private static LiveTvService Service(GuideHandler handler, Func<PluginConfiguration> config) =>
            new LiveTvService(System.Reflection.DispatchProxy.Create<ILogger, SilentLogger>(), _ => new HttpClient(handler, false), config, () => { });
        private static async Task<string> Read(LiveTvService service, string mode)
        {
            if (mode.StartsWith("m3u")) return await service.GetM3UPlaylistAsync(CancellationToken.None);
            if (mode == "export") return await service.GetXmltvEpgAsync(CancellationToken.None);
            return string.Join(",", (await service.FetchEpgForChannelCachedAsync(42, CancellationToken.None)).Select(p => p.Title));
        }
        public class SilentLogger : System.Reflection.DispatchProxy
        {
            protected override object Invoke(System.Reflection.MethodInfo method, object[] args) => null;
        }
        private sealed class GuideHandler : HttpMessageHandler
        {
            public string Title = "Old generation", GuideId = "guide-a", ChannelName = "Old channel", BarrierTarget;
            public bool NoChannels, JsonFallback;
            public int Requests;
            public List<string> Urls = new List<string>();
            public TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++; var url = request.RequestUri.ToString(); Urls.Add(url);
                var title = Title; var guide = GuideId; var name = ChannelName;
                if (BarrierTarget != null && url.Contains(BarrierTarget))
                {
                    BarrierTarget = null; Entered.TrySetResult(true); await Release.Task.WaitAsync(token);
                }
                var start = DateTimeOffset.UtcNow.AddMinutes(-1); var stop = start.AddHours(1);
                var body = url.Contains("get_live_streams") ? (NoChannels ? "[]" : "[{\"stream_id\":42,\"category_id\":1,\"name\":\"" + name + "\",\"epg_channel_id\":\"" + guide + "\"}]")
                    : url.Contains("get_live_categories") ? "[{\"category_id\":1,\"category_name\":\"" + title + "\"}]"
                    : url.Contains("get_simple_data_table") ? "{\"epg_listings\":[{\"title\":\"" + title + "\",\"start_timestamp\":" + start.ToUnixTimeSeconds() + ",\"stop_timestamp\":" + stop.ToUnixTimeSeconds() + "}]}"
                    : "<tv><programme channel=\"" + guide + "\" start=\"" + start.ToString("yyyyMMddHHmmss") + " +0000\" stop=\"" + stop.ToString("yyyyMMddHHmmss") + " +0000\"><title>" + title + "</title></programme></tv>";
                return new HttpResponseMessage(JsonFallback && url.Contains("xmltv.php") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
