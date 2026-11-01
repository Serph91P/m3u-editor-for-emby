using System.IO;
using System.Text;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class PreservedPrimaryTests
    {
        private const string Url = "https://example.com/still.jpg";
        private static EpgProgram Parse(string art)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<tv><programme channel=\"synthetic\" start=\"20261023100000 +0000\" stop=\"20261023110000 +0000\"><title>Synthetic</title>" + art + "</programme></tv>"));
            return Assert.Single(XmltvParser.Parse(stream, null, null)["synthetic"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SerializedProtectionPrecedesPosterAndRetainsStill(bool xml)
        {
            var program = xml ? Parse("<?m3ue-preserved-primary?><icon src=\"" + Url + "\"/><icon src=\"" + Url + "\" width=\"1920\" height=\"1080\"/><icon src=\"https://example.com/poster.jpg\" width=\"500\" height=\"750\"/><image type=\"poster\" orient=\"P\">https://example.com/poster.jpg</image><image type=\"still\" orient=\"L\">" + Url + "</image>")
                : JsonSerializer.Deserialize<EpgProgram>("{\"preserved_primary_url\":\"" + Url + "\",\"poster_url\":\"https://example.com/poster.jpg\",\"poster_width\":500,\"poster_height\":750,\"still_url\":\"" + Url + "\",\"still_width\":1920,\"still_height\":1080}");
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", "Synthetic", "");
            Assert.Equal(Url, info.ImageUrl);
            Assert.Equal(1920, info.ImageWidth);
            Assert.Equal(1080, info.ImageHeight);
            Assert.Equal(Url, info.ThumbImageUrl);
        }

        [Theory]
        [InlineData("<?m3ue-preserved-primary?>", true)]
        [InlineData("<?m3ue-preserved-primary?> \n<!-- ignored -->", true)]
        [InlineData("<?m3ue-preserved-primary invalid?>", false)]
        [InlineData("<?m3ue-preserved-primary?><new/>", false)]
        [InlineData("<rating><?m3ue-preserved-primary?></rating>", false)]
        [InlineData("<?m3ue-preserved-primary?><?m3ue-preserved-primary?>", false)]
        [InlineData("<?other?>", false)]
        public void ProcessingInstructionBindsOnlyTheNextDirectIcon(string prefix, bool valid)
        {
            var program = Parse(prefix + "<icon src=\"" + Url + "\"/><icon src=\"" + Url + "\" width=\"1920\" height=\"1080\"/><image type=\"still\" orient=\"L\">" + Url + "</image>");
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", "Synthetic", "");
            Assert.Equal(valid ? Url : null, info.ImageUrl);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SameUrlConflictingGeometryKeepsPrimaryWithoutInventingDimensions(bool xml)
        {
            var program = xml ? Parse("<?m3ue-preserved-primary?><icon src=\"" + Url + "\" width=\"1920\" height=\"1080\"/><icon src=\"" + Url + "\" width=\"640\" height=\"360\"/><image type=\"still\" orient=\"L\">" + Url + "</image>")
                : JsonSerializer.Deserialize<EpgProgram>("{\"preserved_primary_url\":\"" + Url + "\",\"poster_url\":\"" + Url + "\",\"poster_width\":500,\"poster_height\":750,\"still_url\":\"" + Url + "\",\"still_width\":1920,\"still_height\":1080}");
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", "Synthetic", "");
            Assert.Equal(Url, info.ImageUrl);
            Assert.Equal(0, info.ImageWidth);
            Assert.Equal(0, info.ImageHeight);
        }

        [Theory]
        [InlineData("file:///private/image.jpg")]
        [InlineData("not a URL")]
        public void InvalidSerializedProtectionCannotSelectSecondary(string invalid)
        {
            var program = JsonSerializer.Deserialize<EpgProgram>("{\"preserved_primary_url\":\"" + invalid + "\",\"still_url\":\"" + Url + "\",\"still_width\":1920,\"still_height\":1080}");
            Assert.Null(M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", "Synthetic", "").ImageUrl);
            program = Parse("<?m3ue-preserved-primary?><icon src=\"" + invalid + "\"/><icon src=\"" + Url + "\"/><image type=\"still\" orient=\"L\">" + Url + "</image>");
            Assert.Null(M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", "Synthetic", "").ImageUrl);
        }

        [Fact]
        public void ProcessingInstructionDoesNotEscapeProgrammeOrBindSecondaryTwin()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<tv><?m3ue-preserved-primary?><programme channel=\"synthetic\" start=\"20261023100000 +0000\" stop=\"20261023110000 +0000\"><title>First</title><?m3ue-preserved-primary?></programme><programme channel=\"synthetic\" start=\"20261023110000 +0000\" stop=\"20261023120000 +0000\"><title>Second</title><icon src=\"" + Url + "\"/><image type=\"still\" orient=\"L\">" + Url + "</image></programme></tv>"));
            foreach (var program in XmltvParser.Parse(stream, null, null)["synthetic"])
                Assert.Null(M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", "Synthetic", "").ImageUrl);
        }

        [Fact]
        public void ConflictingMarkersDoNotGuessAndGeometryDoesNotBorrowAnotherUrl()
        {
            var ambiguous = Parse("<?m3ue-preserved-primary?><icon src=\"" + Url + "\"/><?m3ue-preserved-primary?><icon src=\"https://example.com/other.jpg\"/><image type=\"still\" orient=\"L\">" + Url + "</image>");
            Assert.Null(M3uEditorTunerHost.BuildProgramInfo(ambiguous, 1, "synthetic", "Synthetic", "").ImageUrl);
            var missing = JsonSerializer.Deserialize<EpgProgram>("{\"preserved_primary_url\":\"" + Url + "\",\"poster_url\":\"https://example.com/poster.jpg\",\"poster_width\":500,\"poster_height\":750}");
            var info = M3uEditorTunerHost.BuildProgramInfo(missing, 1, "synthetic", "Synthetic", "");
            Assert.Equal(Url, info.ImageUrl);
            Assert.Equal(0, info.ImageWidth);
            Assert.Equal(0, info.ImageHeight);
        }
    }
}
