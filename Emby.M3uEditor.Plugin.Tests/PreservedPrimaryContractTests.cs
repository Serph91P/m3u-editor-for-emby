using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class PreservedPrimaryContractTests
    {
        internal static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","preserved-primary-v1.json")));
        internal static EpgProgram Parse(JsonElement root, string format)
        {
            if(format=="json") return JsonSerializer.Deserialize<EpgProgram>(root.GetProperty("json").GetRawText(),new JsonSerializerOptions { NumberHandling=JsonNumberHandling.AllowReadingFromString });
            using var stream=new MemoryStream(Encoding.UTF8.GetBytes(root.GetProperty("xmltv").GetString()));
            return Assert.Single(XmltvParser.Parse(stream,null,null)["synthetic-output"]);
        }

        [Theory][InlineData("xmltv")][InlineData("json")]
        public async Task RealCoreExportAndRealEnricherProjectionReachProgramInfo(string format)
        {
            using var fixture=Fixture(); var root=fixture.RootElement;
            Assert.DoesNotContain("m3ue-preserved-primary",root.GetProperty("xmltv").GetString());
            Assert.False(root.GetProperty("json").TryGetProperty("preserved_primary_url",out _));
            var program=Parse(root,format);
            Assert.Null(program.PreservedPrimaryUrl);
            var normal=M3uEditorTunerHost.BuildProgramInfo(program,1,"channel","Synthetic Harbour","");
            Assert.Equal("https://example.invalid/poster.jpg",normal.ImageUrl);
            var handler=new FakeHttpHandler(); handler.RespondWith("player_api.php",PreservedPrimaryClientTests.Page(1,root.GetProperty(format+"_projection").GetRawText()));
            var entries=await new PreservedPrimaryClient(_=>new HttpClient(handler,false)).FetchAsync(PreservedPrimaryClientTests.Config(),new[]{1},1893499200,1893502800,format,CancellationToken.None);
            var copy=Assert.Single(PreservedPrimaryOverlay.Apply(new[]{program},1,entries,format));
            var info=M3uEditorTunerHost.BuildProgramInfo(copy,1,"channel",format=="xmltv" ? copy.Title : LiveTvService.DecodeBase64(copy.Title),"");
            Assert.Equal("Synthetic Harbour",info.Name);
            Assert.Equal("https://example.invalid/primary.jpg",info.ImageUrl);
            Assert.Equal(1920,info.ImageWidth); Assert.Equal(1080,info.ImageHeight);
            Assert.Equal("https://example.invalid/backdrop.jpg",info.BackdropImageUrl);
            Assert.Equal("https://example.invalid/primary.jpg",info.ThumbImageUrl);
            Assert.Null(program.PreservedPrimaryUrl);
        }
    }
}
