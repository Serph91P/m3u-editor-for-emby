using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class PreservedPrimaryLiveTvTests
    {
        [Theory][InlineData(false)][InlineData(true)]
        public async Task NormalGuideSharesProjectionAndCopiesSharedXmltvInstances(bool json)
        {
            var handler=new GuideHandler { Json=json }; var config=Config();
            using var service=Service(handler,()=>config);
            var calls=await Task.WhenAll(Enumerable.Range(0,6).Select(i=>service.FetchEpgForChannelCachedAsync(i%2+1,CancellationToken.None)));
            Assert.All(calls, list=>Assert.Equal("https://example.invalid/primary.jpg",Assert.Single(list).PreservedPrimaryUrl));
            Assert.Equal(1,handler.QueryRequests);
            Assert.NotSame(calls[0][0],calls[1][0]);
            Assert.Equal("https://example.invalid/poster.jpg",calls[0][0].PosterUrl);
        }

        [Theory][InlineData(EpgSourceMode.CustomUrl)][InlineData(EpgSourceMode.Disabled)]
        public async Task NonXtreamSourcesNeverProbeQuery(EpgSourceMode mode)
        {
            var handler=new GuideHandler(); var config=Config(); config.EpgSource=mode; config.CustomEpgUrl="https://custom.example.invalid/guide.xml";
            using var service=Service(handler,()=>config);
            var result=await service.FetchEpgForChannelCachedAsync(1,CancellationToken.None);
            Assert.Equal(0,handler.QueryRequests);
            Assert.All(handler.Requests.Where(r=>r.Contains("custom.example.invalid")),r=>Assert.DoesNotContain("username",r));
            Assert.All(result,p=>Assert.Null(p.PreservedPrimaryUrl));
        }

        [Theory][InlineData(false,403)][InlineData(false,404)][InlineData(false,429)][InlineData(false,500)]
        [InlineData(true,403)][InlineData(true,404)][InlineData(true,429)][InlineData(true,500)]
        public async Task MissingCapabilityLeavesGuideAndRecoversNextGeneration(bool json,int status)
        {
            var handler=new GuideHandler { Json=json, Status=status }; var config=Config();
            using var service=Service(handler,()=>config);
            Assert.Null(Assert.Single(await service.FetchEpgForChannelCachedAsync(1,CancellationToken.None)).PreservedPrimaryUrl);
            Assert.Null(Assert.Single(await service.FetchEpgForChannelCachedAsync(2,CancellationToken.None)).PreservedPrimaryUrl);
            Assert.Equal(1,handler.QueryRequests);
            handler.Status=200; service.InvalidateCache();
            Assert.NotNull(Assert.Single(await service.FetchEpgForChannelCachedAsync(1,CancellationToken.None)).PreservedPrimaryUrl);
            Assert.Equal(2,handler.QueryRequests);
        }

        [Theory][InlineData(false)][InlineData(true)]
        public async Task IncompletePagingNeverInstallsHalfAnOverlay(bool json)
        {
            var handler=new GuideHandler { Json=json, Incomplete=true }; var config=Config();
            using var service=Service(handler,()=>config);
            Assert.Null(Assert.Single(await service.FetchEpgForChannelCachedAsync(1,CancellationToken.None)).PreservedPrimaryUrl);
            Assert.Null(Assert.Single(await service.FetchEpgForChannelCachedAsync(2,CancellationToken.None)).PreservedPrimaryUrl);
            Assert.Equal(2,handler.QueryRequests);
        }

        [Theory][InlineData("invalidate")][InlineData("credentials")][InlineData("cancel")]
        public async Task DelayedQueryCannotInstallObsoleteOrCancelledGeneration(string change)
        {
            var handler=new GuideHandler { Block=true }; var config=Config();
            using var service=Service(handler,()=>config); using var cts=new CancellationTokenSource();
            var first=service.FetchEpgForChannelCachedAsync(1,cts.Token);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(change=="cancel") cts.Cancel();
            else if(change=="credentials") { config=Config(); config.Username="new-user"; service.RefreshConfiguration(); }
            else service.InvalidateCache();
            handler.Release.TrySetResult(true);
            if(change=="cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
            else Assert.Empty(await first);
            Assert.Equal(0,service.PerChannelEpgCacheCount);
            Assert.NotNull(Assert.Single(await service.FetchEpgForChannelCachedAsync(1,CancellationToken.None)).PreservedPrimaryUrl);
        }

        [Fact]
        public async Task WaitingCallerCancelsWithoutCancellingTheSharedLoader()
        {
            var handler=new GuideHandler { Block=true }; var config=Config();
            using var service=Service(handler,()=>config); using var cts=new CancellationTokenSource();
            var loader=service.FetchEpgForChannelCachedAsync(1,CancellationToken.None);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiter=service.FetchEpgForChannelCachedAsync(2,cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiter);
            handler.Release.TrySetResult(true);
            Assert.NotNull(Assert.Single(await loader).PreservedPrimaryUrl);
            Assert.NotNull(Assert.Single(await service.FetchEpgForChannelCachedAsync(2,CancellationToken.None)).PreservedPrimaryUrl);
            Assert.Equal(1,handler.QueryRequests);
        }

        [Fact]
        public async Task CancellationDuringPagingLeavesNoPartialPerChannelCache()
        {
            var handler=new GuideHandler { Block=true, BlockAt=2, Incomplete=true, Large=true }; var config=Config();
            config.EpgDaysToFetch=15;
            using var service=Service(handler,()=>config); using var cts=new CancellationTokenSource();
            var first=service.FetchEpgForChannelCachedAsync(1,cts.Token);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
            Assert.Equal(0,service.PerChannelEpgCacheCount);
            Assert.Equal(100,handler.QueryPayloads[0].GetProperty("stream_ids").GetArrayLength());
            Assert.Equal(14*86400,handler.QueryPayloads[0].GetProperty("stop_timestamp").GetInt64()-handler.QueryPayloads[0].GetProperty("start_timestamp").GetInt64());
            handler.Incomplete=false; handler.Block=false; handler.Large=false;
            config=Config(); service.RefreshConfiguration();
            Assert.NotNull(Assert.Single(await service.FetchEpgForChannelCachedAsync(1,CancellationToken.None)).PreservedPrimaryUrl);
            Assert.Equal(3,handler.QueryRequests);
        }

        private static PluginConfiguration Config()=>new PluginConfiguration { BaseUrl="https://editor.example.invalid",Username="synthetic",Password="synthetic",IncludeAdultChannels=true };
        private static LiveTvService Service(GuideHandler handler,Func<PluginConfiguration> config)=>new LiveTvService(System.Reflection.DispatchProxy.Create<ILogger,LiveTvGenerationTests.SilentLogger>(),_=>new HttpClient(handler,false),config,()=>{});
        private sealed class GuideHandler : HttpMessageHandler
        {
            internal bool Json,Incomplete,Block,Large; internal int QueryRequests,Status=200,BlockAt=1;
            internal List<string> Requests=new List<string>();
            internal List<JsonElement> QueryPayloads=new List<JsonElement>();
            internal TaskCompletionSource<bool> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                var url=request.RequestUri.ToString(); lock(Requests) Requests.Add(url);
                string body;
                if(request.Method==HttpMethod.Post)
                {
                    using var payload=JsonDocument.Parse(PreservedPrimaryClientTests.ParseForm(await request.Content.ReadAsStringAsync())["payload"]);
                    QueryPayloads.Add(payload.RootElement.Clone());
                    var count=Interlocked.Increment(ref QueryRequests);
                    if(Block && count==BlockAt) { Entered.TrySetResult(true); await Release.Task.WaitAsync(token); }
                    if(Status!=200) return new HttpResponseMessage((HttpStatusCode)Status) { Content=new StringContent("{}") };
                    using var fixture=PreservedPrimaryContractTests.Fixture();
                    var entries=fixture.RootElement.GetProperty((Json ? "json" : "xmltv")+"_projection").EnumerateArray().Select(e=>e.GetRawText()).ToArray();
                    var first=entries[0].Replace("1893499200",Start.ToString()).Replace("1893502800",Stop.ToString());
                    var second=first.Replace("\"stream_id\": 1","\"stream_id\": 2");
                    var page=Large ? string.Join(",",Enumerable.Range(0,256).Select(i=>first.Replace(Start.ToString(),(Start+i).ToString()).Replace(Stop.ToString(),(Stop+i).ToString()))) : first;
                    body=Incomplete ? PreservedPrimaryClientTests.Page(Large ? 257 : 2,"["+page+"]",new string('a',64),count==1 ? "snapshot" : "changed") : PreservedPrimaryClientTests.Page(2,"["+first+","+second+"]");
                }
                else if(url.Contains("get_live_streams")) body=JsonSerializer.Serialize(Enumerable.Range(1,Large ? 101 : 2).Select(id=>new { stream_id=id,name="Synthetic "+id,epg_channel_id="synthetic-output" }));
                else if(url.Contains("xmltv.php")||url.Contains("guide.xml"))
                {
                    if(Json) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    using var fixture=PreservedPrimaryContractTests.Fixture();
                    body=fixture.RootElement.GetProperty("xmltv").GetString().Replace("20300101120000",DateTimeOffset.FromUnixTimeSeconds(Start).UtcDateTime.ToString("yyyyMMddHHmmss")).Replace("20300101130000",DateTimeOffset.FromUnixTimeSeconds(Stop).UtcDateTime.ToString("yyyyMMddHHmmss"));
                }
                else if(url.Contains("get_simple_data_table"))
                {
                    using var fixture=PreservedPrimaryContractTests.Fixture();
                    body="{\"epg_listings\":["+fixture.RootElement.GetProperty("json").GetRawText().Replace("1893499200",Start.ToString()).Replace("1893502800",Stop.ToString())+"]}";
                }
                else throw new InvalidOperationException("Unexpected synthetic request");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(body,Encoding.UTF8,"application/json") };
            }
            private static readonly long Start=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+60, Stop=Start+3600;
        }
    }
}
