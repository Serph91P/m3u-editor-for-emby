using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class PreservedPrimaryClientTests
    {
        internal static string Page(int total = 0, string entries = "[]", string cursor = null, string snapshot = "snapshot") =>
            JsonSerializer.Serialize(new { api_version = 1, plugin_id = "epg-enricher", query = "preserved_primary_v1", data = new { snapshot_id = snapshot, total, next_cursor = cursor, entries = JsonSerializer.Deserialize<JsonElement>(entries) } });
        internal static string Entry(int stream = 1, long start = 100, long stop = 200) =>
            JsonSerializer.Serialize(new { stream_id = stream, start_timestamp = start, stop_timestamp = stop, title = "Synthetic", subtitle = default(string), guide = new { primary_icon_url = default(string), poster_url = default(string), poster_width = 0, poster_height = 0, backdrop_url = default(string), still_url = default(string), still_width = 0, still_height = 0 }, primary = new { url = "https://example.invalid/primary.jpg", width = 0, height = 0 } });
        internal static PluginConfiguration Config() => new PluginConfiguration { BaseUrl = "https://editor.example.invalid", Username = "synthetic+user", Password = "synthetic&password" };
        private static PreservedPrimaryClient Client(FakeHttpHandler handler) => new PreservedPrimaryClient(_ => new HttpClient(handler, false));

        [Fact]
        public async Task CompletePagesUseBodyOnlyCredentialsAndStableScope()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWithSequence("/player_api.php", new[] { Page(2, "[" + Entry() + "]", new string('a', 64)), Page(2, "[" + Entry(start: 200, stop: 300) + "]") });
            var result = await Client(handler).FetchAsync(Config(), new[] { 1 }, 100, 300, "json", CancellationToken.None);
            Assert.Equal(2, result.Count);
            Assert.All(handler.ReceivedUrls, u => Assert.Equal("https://editor.example.invalid/player_api.php", u));
            var forms = handler.ReceivedBodies.Select(ParseForm).ToArray();
            Assert.All(forms, f => { Assert.Equal("synthetic+user", f["username"]); Assert.Equal("synthetic&password", f["password"]); Assert.Equal("plugin_query", f["action"]); Assert.Equal("epg-enricher", f["plugin_id"]); Assert.Equal("preserved_primary_v1", f["query"]); });
            Assert.Contains(new string('a',64), forms[1]["payload"]);
            handler.AssertNoUnhandledRequests();
        }

        [Theory]
        [InlineData("version")][InlineData("plugin")][InlineData("query")][InlineData("json")]
        [InlineData("oversize")][InlineData("count")][InlineData("missing")][InlineData("scope")][InlineData("duplicate")]
        [InlineData("snapshot")][InlineData("total")][InlineData("repeat")][InlineData("empty-progress")][InlineData("257")]
        public async Task InvalidSnapshotIsEntirelyDiscarded(string mode)
        {
            var handler = new FakeHttpHandler();
            var first = Page(1, "[" + Entry() + "]");
            var second = Page();
            switch (mode)
            {
                case "version": first = first.Replace("\"api_version\":1", "\"api_version\":2"); break;
                case "plugin": first = first.Replace("epg-enricher", "other"); break;
                case "query": first = first.Replace("preserved_primary_v1", "other"); break;
                case "json": first = "{"; break;
                case "oversize": first += new string(' ', 524289); break;
                case "count": first = Page(2, "["+Entry()+"]"); break;
                case "missing": first = "{\"api_version\":1,\"plugin_id\":\"epg-enricher\",\"query\":\"preserved_primary_v1\",\"data\":{\"snapshot_id\":\"s\"}}"; break;
                case "scope": first = Page(1, "["+Entry(2)+"]"); break;
                case "duplicate": first = Page(2, "["+Entry()+","+Entry()+"]"); break;
                case "257": first = Page(257, "["+string.Join(",", Enumerable.Range(0,257).Select(i => Entry(start:100+i, stop:101+i)))+"]"); break;
                case "empty-progress": first = Page(1, cursor: new string('a',64)); break;
                default:
                    first = Page(2, "["+Entry()+"]", new string('a',64));
                    second = Page(mode == "total" ? 3 : 2, "["+Entry(start:200,stop:300)+"]", mode == "repeat" ? new string('a',64) : null, mode == "snapshot" ? "changed" : "snapshot"); break;
            }
            handler.RespondWithSequence("/player_api.php", new[] { first, second });
            Assert.Null(await Client(handler).FetchAsync(Config(), new[] { 1 }, 100, 1000, "json", CancellationToken.None));
        }

        [Theory]
        [InlineData(403)][InlineData(404)][InlineData(429)][InlineData(500)][InlineData(503)]
        public async Task OptionalHttpFailureReturnsNoProjection(int status)
        {
            var handler = new FakeHttpHandler(); handler.RespondWith("player_api.php", "{}", (HttpStatusCode)status);
            Assert.Null(await Client(handler).FetchAsync(Config(), new[] {1}, 100, 300, "json", CancellationToken.None));
        }

        [Theory]
        [InlineData("subtitle")][InlineData("guide.poster_url")][InlineData("guide.poster_width")]
        [InlineData("guide.poster_height")][InlineData("guide.primary_icon_url")][InlineData("guide.still_width")]
        [InlineData("guide.still_height")][InlineData("guide.still_url")][InlineData("guide.backdrop_url")]
        [InlineData("primary.width")][InlineData("primary.height")]
        public async Task TruncatedEntryMetadataInvalidatesWholeSnapshot(string field)
        {
            var entry=System.Text.Json.Nodes.JsonNode.Parse(Entry()); var parts=field.Split('.');
            if(parts.Length==1) entry.AsObject().Remove(field);
            else entry[parts[0]].AsObject().Remove(parts[1]);
            var handler=new FakeHttpHandler(); handler.RespondWith("player_api.php",Page(1,"["+entry.ToJsonString()+"]"));
            Assert.Null(await Client(handler).FetchAsync(Config(),new[]{1},100,300,"json",CancellationToken.None));
        }

        [Theory]
        [InlineData("poster_url","file:///poster")][InlineData("still_url","javascript:bad")]
        [InlineData("backdrop_url"," https://example.invalid/a.jpg ")][InlineData("primary_icon_url","not-a-url")]
        [InlineData("poster_width",-1)][InlineData("still_height",-1)][InlineData("still_width",1920)]
        public async Task InvalidGuideMetadataDiscardsAlsoOtherValidEntries(string field,object value)
        {
            var entry=System.Text.Json.Nodes.JsonNode.Parse(Entry(start:200,stop:300));
            entry["guide"][field]=System.Text.Json.Nodes.JsonValue.Create(value);
            var handler=new FakeHttpHandler(); handler.RespondWith("player_api.php",Page(2,"["+Entry()+","+entry.ToJsonString()+"]"));
            Assert.Null(await Client(handler).FetchAsync(Config(),new[]{1},100,300,"json",CancellationToken.None));
        }

        [Fact]
        public async Task EmptyCompleteSnapshotIsValid()
        {
            var handler = new FakeHttpHandler(); handler.RespondWith("player_api.php", Page());
            Assert.Empty(await Client(handler).FetchAsync(Config(), new[] {1}, 100, 300, "xmltv", CancellationToken.None));
        }

        [Fact]
        public async Task BatchesAreDisjointAndDoNotTruncateStreamsOrDays()
        {
            var handler = new FakeHttpHandler(); handler.RespondWithSequence("player_api.php", Enumerable.Repeat(Page(),4));
            Assert.Empty(await Client(handler).FetchAsync(Config(), Enumerable.Range(1,101).ToArray(), 100, 100+15*86400, "xmltv", CancellationToken.None));
            var payloads = handler.ReceivedBodies.Select(b => JsonDocument.Parse(ParseForm(b)["payload"])).ToArray();
            Assert.Equal(4,payloads.Length);
            Assert.Equal(new[] {100,100,1,1},payloads.Select(p => p.RootElement.GetProperty("stream_ids").GetArrayLength()));
            Assert.All(payloads, p => Assert.InRange(p.RootElement.GetProperty("stop_timestamp").GetInt64()-p.RootElement.GetProperty("start_timestamp").GetInt64(),1,14*86400));
            Assert.Equal(100+15*86400,payloads[1].RootElement.GetProperty("stop_timestamp").GetInt64());
        }

        [Fact]
        public async Task MultiPageMetadataAndLaterBatchFailureAreAtomic()
        {
            var first="["+string.Join(",",Enumerable.Range(0,256).Select(i=>Entry(start:100+i,stop:101+i)))+"]";
            foreach(var fail in new[]{false,true})
            {
                var handler=new FakeHttpHandler();
                handler.RespondWithSequence("player_api.php",new[]{Page(257,first,new string('a',64)),Page(257,"["+Entry(start:356,stop:357)+"]"),Page(),Page(),fail ? "{}" : Page()});
                var result=await Client(handler).FetchAsync(Config(),Enumerable.Range(1,101).ToArray(),100,100+15*86400,"xmltv",CancellationToken.None);
                if(fail) Assert.Null(result); else Assert.Equal(257,result.Count);
                Assert.Equal(5,handler.ReceivedBodies.Count);
            }
        }

        [Theory]
        [InlineData(false)][InlineData(true)]
        public async Task TimeoutFailsOpenButCallerCancellationPropagates(bool caller)
        {
            using var cts = new CancellationTokenSource();
            var client = new PreservedPrimaryClient(_ => new HttpClient(new CancelHandler(cts,caller)));
            if (caller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(Config(), new[]{1},100,300,"json",cts.Token));
            else Assert.Null(await client.FetchAsync(Config(),new[]{1},100,300,"json",cts.Token));
        }
        [Theory][InlineData(524288,true)][InlineData(524289,false)]
        public async Task UnknownLengthBodyHonorsInclusiveByteLimitAndPostMethod(int bytes,bool accepted)
        {
            var text=Page(); text+=new string(' ',bytes-System.Text.Encoding.UTF8.GetByteCount(text));
            using var stream=new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            using var content=new StreamBody(stream);
            using var response=new HttpResponseMessage(HttpStatusCode.OK){Content=content};
            var client=new PreservedPrimaryClient(_=>new HttpClient(new BodyHandler(response)));
            var result=await client.FetchAsync(Config(),new[]{1},100,300,"json",CancellationToken.None);
            if(accepted) Assert.Empty(result); else Assert.Null(result);
        }

        private sealed class BodyHandler(HttpResponseMessage response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                Assert.Equal(HttpMethod.Post,request.Method);
                Assert.Equal("application/x-www-form-urlencoded",request.Content.Headers.ContentType.MediaType);
                return Task.FromResult(response);
            }
        }
        private sealed class StreamBody(System.IO.Stream stream) : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length=0; return false; }
            protected override Task<System.IO.Stream> CreateContentReadStreamAsync()=>Task.FromResult(stream);
            protected override Task SerializeToStreamAsync(System.IO.Stream stream,System.Net.TransportContext context)=>throw new InvalidOperationException("Do not buffer the whole response");
        }

        private sealed class CancelHandler(CancellationTokenSource source, bool caller) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if(caller) { source.Cancel(); }
                throw new TaskCanceledException();
            }
        }
        internal static System.Collections.Generic.Dictionary<string,string> ParseForm(string body) => body.Split('&').Select(p=>p.Split('=',2)).ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>Uri.UnescapeDataString(p[1].Replace('+',' ')));
    }
}
