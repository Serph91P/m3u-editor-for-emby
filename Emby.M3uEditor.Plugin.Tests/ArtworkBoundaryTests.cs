using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ArtworkBoundaryTests
    {
        private static XDocument ProducerXml() => XDocument.Load(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "artwork", "core-scalar-poster.xml"));

        private static EpgProgram Parse(XDocument xml)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
            return Assert.Single(XmltvParser.Parse(stream, null, null)["synthetic"]);
        }

        private static string ProducerJson(string name)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "artwork", "core-artwork.json")));
            return fixture.RootElement.GetProperty(name).GetProperty("json").GetRawText();
        }

        [Theory]
        [InlineData("still_only", "https://example.com/still.jpg", null)]
        [InlineData("still_larger_than_backdrop", "https://example.com/poster.jpg", "https://example.com/backdrop.jpg")]
        public void ProducerXml_PreservesSeparatedArtworkRoles(string name, string primary, string backdrop)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "artwork", "core-artwork.json")));
            var xml = XDocument.Parse(fixture.RootElement.GetProperty(name).GetProperty("xml").GetString());
            var programme = xml.Root.Element("programme");
            var elements = programme.Elements().ToArray();
            for (var shift = 0; shift < elements.Length; shift++)
            {
                programme.ReplaceNodes(elements.Skip(shift).Concat(elements.Take(shift)));
                var program = Parse(xml);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);
                Assert.Equal(primary, info.ImageUrl);
                Assert.Equal(backdrop, info.BackdropImageUrl);
                Assert.Equal("https://example.com/still.jpg", info.ThumbImageUrl);
            }
        }

        [Theory]
        [InlineData("control", "https://example.com/poster.jpg", 500, 750)]
        [InlineData("provider_larger_poster", "https://example.com/foreign.jpg", 1000, 1500)]
        [InlineData("portrait_orientation_only", "https://example.com/backdrop.jpg", 0, 0)]
        public void ProducerJson_UsesOnlyAttestedPortraitGeometry(string name, string expected, int width, int height)
        {
            var program = JsonSerializer.Deserialize<EpgProgram>(ProducerJson(name));
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            Assert.Equal(expected, info.ImageUrl);
            Assert.Equal(width, info.ImageWidth);
            Assert.Equal(height, info.ImageHeight);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProducerJson_WithoutQualifiedPosterKeepsBackdropAheadOfStill(bool oldPayload)
        {
            var json = JsonNode.Parse(ProducerJson("still_larger_than_backdrop")).AsObject();
            json.Remove("poster_width");
            json.Remove("poster_height");
            if (oldPayload)
            {
                json.Remove("still_url");
                json.Remove("still_width");
                json.Remove("still_height");
            }
            var program = JsonSerializer.Deserialize<EpgProgram>(json.ToJsonString());
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            Assert.Equal("https://example.com/backdrop.jpg", info.ImageUrl);
            Assert.Equal("https://example.com/backdrop.jpg", info.BackdropImageUrl);
            Assert.Equal(oldPayload ? null : "https://example.com/still.jpg", info.ThumbImageUrl);
            Assert.Equal(0, info.ImageWidth);
            Assert.Equal(0, info.ImageHeight);
        }

        [Theory]
        [InlineData("file:///private/still.jpg")]
        [InlineData("javascript:alert(1)")]
        [InlineData("/relative.jpg")]
        public void ProducerJson_RejectsUnsafeStillUrlAtProgramInfoBoundary(string url)
        {
            var json = JsonNode.Parse(ProducerJson("still_only")).AsObject();
            json["still_url"] = url;
            var program = JsonSerializer.Deserialize<EpgProgram>(json.ToJsonString());
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            Assert.Null(info.ImageUrl);
            Assert.Null(info.ThumbImageUrl);
            Assert.Equal(0, info.ImageWidth);
            Assert.Equal(0, info.ImageHeight);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StandardPosterAlternatives_SelectLargestQualifiedPair(bool reverse)
        {
            var xml = ProducerXml();
            var programme = xml.Root.Element("programme");
            programme.Elements("icon").First().Remove();
            programme.Add(new XElement("icon", new XAttribute("src", "https://example.com/larger.jpg"),
                new XAttribute("width", 600), new XAttribute("height", 900)),
                new XElement("image", new XAttribute("type", "poster"), new XAttribute("orient", "P"),
                    "https://example.com/larger.jpg"));
            if (reverse)
                programme.ReplaceNodes(programme.Elements().Reverse().ToArray());

            Assert.Equal("https://example.com/larger.jpg", Parse(xml).PosterUrl);
        }

        [Fact]
        public void ProducerJson_StillOnlyIsLastImageFallbackWithReportedDimensions()
        {
            var program = JsonSerializer.Deserialize<EpgProgram>(ProducerJson("still_only"));
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            Assert.Equal("https://example.com/still.jpg", info.ImageUrl);
            Assert.Equal(1920, info.ImageWidth);
            Assert.Equal(1080, info.ImageHeight);
            Assert.Equal("https://example.com/still.jpg", info.ThumbImageUrl);
            Assert.Null(info.BackdropImageUrl);
        }

        [Fact]
        public void ProducerJson_KeepsPosterBackdropStillInSeparateSlots()
        {
            var program = JsonSerializer.Deserialize<EpgProgram>(ProducerJson("still_larger_than_backdrop"));
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            Assert.Equal("https://example.com/poster.jpg", info.ImageUrl);
            Assert.Equal(500, info.ImageWidth);
            Assert.Equal(750, info.ImageHeight);
            Assert.Equal("https://example.com/backdrop.jpg", info.BackdropImageUrl);
            Assert.Equal("https://example.com/still.jpg", info.ThumbImageUrl);
        }

        [Theory]
        [InlineData("geometry")]
        [InlineData("orientation")]
        [InlineData("role")]
        [InlineData("unsafe")]
        [InlineData("unattested")]
        public void ProducerPortrait_RejectsContradictoryOrMissingEvidence(string mutation)
        {
            var xml = ProducerXml();
            var programme = xml.Root.Element("programme");
            if (mutation == "geometry")
                programme.Add(new XElement("icon", new XAttribute("src", "https://example.com/poster.jpg"),
                    new XAttribute("width", 600), new XAttribute("height", 900)));
            else if (mutation == "orientation")
                programme.Elements("icon").First().SetAttributeValue("orient", "L");
            else if (mutation == "role")
                programme.Add(new XElement("image", new XAttribute("type", "still"),
                    new XAttribute("orient", "L"), "https://example.com/poster.jpg"));
            else if (mutation == "unsafe")
            {
                foreach (var icon in programme.Elements("icon").Where(e => (string)e.Attribute("src") == "https://example.com/poster.jpg"))
                    icon.SetAttributeValue("src", "file:///private/poster.jpg");
                programme.Elements("image").Single(e => (string)e.Attribute("type") == "poster").Value = "file:///private/poster.jpg";
            }
            else
                programme.Elements("icon").Single(e => (string)e.Attribute("width") == "500").Remove();

            foreach (var reverse in new[] { false, true })
            {
                if (reverse)
                    programme.ReplaceNodes(programme.Elements().Reverse().ToArray());
                var program = Parse(xml);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);
                Assert.Null(program.PosterUrl);
                Assert.Equal("https://example.com/backdrop.jpg", info.ImageUrl);
                Assert.Equal(0, info.ImageWidth);
                Assert.Equal(0, info.ImageHeight);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AmbiguousScalarSelections_UseStableGeometryRanking(bool reverse)
        {
            var xml = ProducerXml();
            var programme = xml.Root.Element("programme");
            programme.Add(new XElement("icon", new XAttribute("src", "https://example.com/larger.jpg"),
                new XAttribute("type", "poster"), new XAttribute("width", 600), new XAttribute("height", 900)),
                new XElement("icon", new XAttribute("src", "https://example.com/larger.jpg")));
            if (reverse)
                programme.ReplaceNodes(programme.Elements().Reverse().ToArray());

            Assert.Equal("https://example.com/larger.jpg", Parse(xml).PosterUrl);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EqualGeometryPosters_UseOrdinalUrlTieBreak(bool reverse)
        {
            var xml = ProducerXml();
            var programme = xml.Root.Element("programme");
            programme.Elements("icon").First().Remove();
            programme.Add(new XElement("icon", new XAttribute("src", "https://example.com/alternate.jpg"),
                new XAttribute("type", "poster"), new XAttribute("width", 500), new XAttribute("height", 750)));
            if (reverse)
                programme.ReplaceNodes(programme.Elements().Reverse().ToArray());

            Assert.Equal("https://example.com/alternate.jpg", Parse(xml).PosterUrl);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ScalarSelection_PrefersQualifiedPosterOverLargerAlternative(bool reverse)
        {
            var xml = ProducerXml();
            var programme = xml.Root.Element("programme");
            programme.Add(new XElement("icon", new XAttribute("src", "https://example.com/larger.jpg"),
                new XAttribute("type", "poster"), new XAttribute("width", 600), new XAttribute("height", 900)));
            if (reverse)
                programme.ReplaceNodes(programme.Elements().Reverse().ToArray());

            Assert.Equal("https://example.com/poster.jpg", Parse(xml).PosterUrl);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void IndependentQualifiedPosters_SelectLargestReportedArea(bool reverse)
        {
            var xml = ProducerXml();
            var programme = xml.Root.Element("programme");
            programme.Elements("icon").First().Remove(); // No scalar preference.
            programme.Add(new XElement("icon", new XAttribute("src", "https://example.com/larger.jpg"),
                new XAttribute("type", "poster"), new XAttribute("width", 600), new XAttribute("height", 900)));
            if (reverse)
                programme.ReplaceNodes(programme.Elements().Reverse().ToArray());

            var program = Parse(xml);
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            Assert.Equal("https://example.com/larger.jpg", info.ImageUrl);
            Assert.Equal(600, info.ImageWidth);
            Assert.Equal(900, info.ImageHeight);
            Assert.Equal("https://example.com/backdrop.jpg", info.BackdropImageUrl);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProducerScalarIcon_DoesNotInvalidateQualifiedPortrait(bool reverse)
        {
            // Actual EpgGenerateController output, including the repeated scalar icon.
            var xml = ProducerXml();
            var programme = xml.Root.Element("programme");
            if (reverse)
                programme.ReplaceNodes(programme.Elements().Reverse().ToArray());

            var program = Parse(xml);
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            Assert.Equal("https://example.com/poster.jpg", program.PosterUrl);
            Assert.Equal("https://example.com/poster.jpg", info.ImageUrl);
            Assert.Equal(500, info.ImageWidth);
            Assert.Equal(750, info.ImageHeight);
            Assert.Equal("https://example.com/backdrop.jpg", info.BackdropImageUrl);
        }
    }
}
