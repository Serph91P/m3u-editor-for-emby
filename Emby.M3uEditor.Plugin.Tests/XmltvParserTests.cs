using System.Collections.Generic;
using System.IO;
using System.Text;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class XmltvParserTests
    {
        private static Dictionary<string, List<EpgProgram>> Parse(string xml)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
                return XmltvParser.Parse(stream, null, null);
        }

        [Fact]
        public void ParseProgramme_WithIcon_SetsImageUrl()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Test Show</title>
    <icon src=""https://example.com/poster.jpg"" />
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch1"));
            var prog = Assert.Single(result["ch1"]);
            Assert.Equal("https://example.com/poster.jpg", prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_WithM3uEditorArtworkRoles_KeepsImagesSeparate()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20260903064000 +0000"" stop=""20260903073000 +0000"" channel=""kabel-eins"">
    <title>MacGyver</title>
    <icon src=""https://image.tmdb.org/backdrop.jpg"" />
    <icon src=""https://image.tmdb.org/poster.jpg"" type=""poster"" width=""500"" height=""750"" orient=""P"" size=""2"" />
    <icon src=""https://image.tmdb.org/still.jpg"" type=""screenshot"" width=""1280"" height=""720"" orient=""L"" size=""1"" />
    <icon src=""https://image.tmdb.org/logo.png"" type=""logo"" width=""500"" height=""281"" orient=""L"" size=""3"" />
    <icon src=""https://image.tmdb.org/backdrop.jpg"" type=""backdrop"" width=""1920"" height=""1080"" orient=""L"" size=""1"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["kabel-eins"]);

            Assert.Equal("https://image.tmdb.org/poster.jpg", prog.ImageUrl);
            Assert.Equal(500, prog.ImageWidth);
            Assert.Equal(750, prog.ImageHeight);
            Assert.Equal("https://image.tmdb.org/backdrop.jpg", prog.BackdropImageUrl);
            Assert.Equal("https://image.tmdb.org/still.jpg", prog.ThumbImageUrl);
            Assert.Equal("https://image.tmdb.org/logo.png", prog.LogoImageUrl);
        }

        [Fact]
        public void ParseProgramme_WithMultipleUntypedIcons_PreservesLastValidFallback()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Legacy Show</title>
    <icon src=""https://example.com/first.jpg"" />
    <icon src=""/relative-invalid.jpg"" />
    <icon src=""https://example.com/last.jpg"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Equal("https://example.com/last.jpg", prog.ImageUrl);
            Assert.Null(prog.BackdropImageUrl);
            Assert.Null(prog.ThumbImageUrl);
            Assert.Null(prog.LogoImageUrl);
        }

        [Fact]
        public void ParseProgramme_TypedPosterWinsRegardlessOfIconOrder()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Ordered Show</title>
    <icon src=""https://example.com/poster.jpg"" type=""POSTER"" width=""500"" height=""750"" />
    <icon src=""https://example.com/legacy-last.jpg"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Equal("https://example.com/poster.jpg", prog.ImageUrl);
            Assert.Equal(500, prog.ImageWidth);
            Assert.Equal(750, prog.ImageHeight);
        }

        [Fact]
        public void ParseProgramme_WithoutIcon_ImageUrlIsNull()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Test Show</title>
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch1"));
            var prog = Assert.Single(result["ch1"]);
            Assert.Null(prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_IconWithEmptySrc_ImageUrlIsNull()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Test Show</title>
    <icon src="""" />
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch1"));
            var prog = Assert.Single(result["ch1"]);
            Assert.Null(prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_WithTitleDescriptionAndIcon_ParsesAll()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch2"">
    <title>Documentary Night</title>
    <desc>A fascinating documentary.</desc>
    <icon src=""https://cdn.example.com/doc-thumb.png"" />
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch2"));
            var prog = Assert.Single(result["ch2"]);
            Assert.Equal("Documentary Night", prog.Title);
            Assert.Equal("A fascinating documentary.", prog.Description);
            Assert.Equal("https://cdn.example.com/doc-thumb.png", prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_WithMultipleCategories_ParsesAllIntoList()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Game</title>
    <category>Sports</category>
    <category>Basketball</category>
  </programme>
</tv>";

            var result = Parse(xml);

            var prog = Assert.Single(result["ch1"]);
            Assert.NotNull(prog.Categories);
            Assert.Equal(2, prog.Categories.Count);
            Assert.Contains("Sports", prog.Categories);
            Assert.Contains("Basketball", prog.Categories);
        }

        [Fact]
        public void ParseProgramme_WithNoCategory_CategoriesIsNull()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Show</title>
  </programme>
</tv>";

            var result = Parse(xml);

            var prog = Assert.Single(result["ch1"]);
            Assert.Null(prog.Categories);
        }

        [Fact]
        public void ParseProgramme_WithSubTitle_ParsesSubTitle()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>The Show</title>
    <sub-title>Episode One</sub-title>
  </programme>
</tv>";

            var result = Parse(xml);

            var prog = Assert.Single(result["ch1"]);
            Assert.Equal("Episode One", prog.SubTitle);
        }

        [Fact]
        public void ParseProgramme_WithEmptyCategory_CategoryIsSkipped()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Show</title>
    <category>   </category>
    <category>News</category>
  </programme>
</tv>";

            var result = Parse(xml);

            var prog = Assert.Single(result["ch1"]);
            var cat = Assert.Single(prog.Categories);
            Assert.Equal("News", cat);
        }
    }
}
