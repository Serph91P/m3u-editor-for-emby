using System.Collections.Generic;
using System.IO;
using System.Text;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
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

            Assert.Contains("ch1", result.Keys);
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
        public void ParseProgramme_StandardPosterWithSameUrlPortraitIcon_ReachesPrimaryAndProgramInfoRegardlessOfOrder()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""first"">
    <icon src=""https://example.com/first-landscape.jpg"" width=""1280"" height=""720"" />
    <image type=""poster"" orient=""P"">https://example.com/poster.jpg</image>
    <icon src=""https://example.com/poster.jpg"" width=""500"" height=""750"" />
    <icon src=""https://example.com/last-landscape.jpg"" width=""1920"" height=""1080"" />
  </programme>
  <programme start=""20250101130000 +0000"" stop=""20250101140000 +0000"" channel=""last"">
    <icon src=""https://example.com/first-landscape.jpg"" width=""1280"" height=""720"" />
    <icon src=""https://example.com/poster.jpg"" width=""500"" height=""750"" />
    <image type=""poster"" orient=""P"">https://example.com/poster.jpg</image>
    <icon src=""https://example.com/last-landscape.jpg"" width=""1920"" height=""1080"" />
  </programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "first", "last" })
            {
                var program = Assert.Single(programs[channelId]);
                Assert.Equal("https://example.com/poster.jpg", program.ImageUrl);
                Assert.Equal(500, program.ImageWidth);
                Assert.Equal(750, program.ImageHeight);

                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, channelId, program.Title, program.Description);
                Assert.Equal("https://example.com/poster.jpg", info.ImageUrl);
                Assert.Equal(500, info.ImageWidth);
                Assert.Equal(750, info.ImageHeight);
            }
        }

        [Fact]
        public void ParseProgramme_MultipleStandardPosterAlternatives_SelectsOnlyDimensionPairedPortrait()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <image type=""poster"" orient=""P"">https://example.com/one.jpg</image>
    <image type=""poster"" orient=""P"">https://example.com/two.jpg</image>
    <icon src=""https://example.com/one.jpg"" width=""500"" height=""750"" />
    <icon src=""https://example.com/wide-batch-icon.jpg"" width=""1280"" height=""720"" />
  </programme>
</tv>";

            var program = Assert.Single(Parse(xml)["ch1"]);

            Assert.Equal("https://example.com/one.jpg", program.ImageUrl);
            Assert.Equal(500, program.ImageWidth);
            Assert.Equal(750, program.ImageHeight);
        }

        [Fact]
        public void ParseProgramme_StandardPosterPairRejectsUnattestedOrAmbiguousPrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""square""><image type=""poster"" orient=""P"">https://example.com/square.jpg</image><icon src=""https://example.com/square.jpg"" width=""750"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""landscape""><image type=""poster"" orient=""P"">https://example.com/landscape.jpg</image><icon src=""https://example.com/landscape.jpg"" width=""1280"" height=""720"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""missing""><image type=""poster"" orient=""P"">https://example.com/missing.jpg</image><icon src=""https://example.com/missing.jpg"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""mismatch""><image type=""poster"" orient=""P"">https://example.com/poster.jpg</image><icon src=""https://example.com/other.jpg"" width=""500"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""scheme""><image type=""poster"" orient=""P"">javascript:alert(1)</image><icon src=""javascript:alert(1)"" width=""500"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""nested""><rating><image type=""poster"" orient=""P"">https://example.com/nested.jpg</image><icon src=""https://example.com/nested.jpg"" width=""500"" height=""750"" /></rating></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""orient""><image type=""poster"" orient=""L"">https://example.com/orient.jpg</image><icon src=""https://example.com/orient.jpg"" width=""500"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""conflict""><image type=""poster"" orient=""P"">https://example.com/paired.jpg</image><icon src=""https://example.com/paired.jpg"" width=""500"" height=""750"" /><icon src=""https://example.com/legacy.jpg"" type=""poster"" width=""500"" height=""750"" /></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "square", "landscape", "missing", "mismatch", "scheme", "nested", "orient", "conflict" })
            {
                var program = Assert.Single(programs[channelId]);
                Assert.Null(program.ImageUrl);
                Assert.Equal(0, program.ImageWidth);
                Assert.Equal(0, program.ImageHeight);
            }
        }

        [Fact]
        public void ParseProgramme_StandardPosterPairRejectsSameUrlSquareTypedPosterAndLeavesProgramInfoEmpty()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <image type=""poster"" orient=""P"">https://example.com/poster.jpg</image>
    <icon src=""https://example.com/poster.jpg"" width=""500"" height=""750"" />
    <icon src=""https://example.com/poster.jpg"" type=""poster"" width=""500"" height=""500"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);
            var info = M3uEditorTunerHost.BuildProgramInfo(prog, 1, "ch1", prog.Title, prog.Description);

            Assert.Null(prog.ImageUrl);
            Assert.Equal(0, prog.ImageWidth);
            Assert.Equal(0, prog.ImageHeight);
            Assert.Null(info.ImageUrl);
            Assert.Equal(0, info.ImageWidth);
            Assert.Equal(0, info.ImageHeight);
        }

        [Fact]
        public void ParseProgramme_StandardImagesWinPerRoleAndKeepPortraitGuard()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <icon src=""https://example.com/legacy-backdrop.jpg"" type=""backdrop"" />
    <image type=""still"" orient=""L"">https://example.com/standard-still.jpg</image>
    <icon src=""https://example.com/legacy-poster.jpg"" type=""poster"" width=""500"" height=""750"" />
    <image type=""poster"" orient=""P"">https://example.com/dimensionless-standard-poster.jpg</image>
    <icon src=""https://example.com/legacy-still.jpg"" type=""still"" />
    <image type=""backdrop"" orient=""L"">https://example.com/standard-backdrop.jpg</image>
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            // Standard poster URLs carry no intrinsic dimensions in the XMLTV DTD, so
            // the measured legacy portrait poster remains the safe primary candidate.
            Assert.Equal("https://example.com/legacy-poster.jpg", prog.ImageUrl);
            Assert.Equal(500, prog.ImageWidth);
            Assert.Equal(750, prog.ImageHeight);
            Assert.Equal("https://example.com/standard-backdrop.jpg", prog.BackdropImageUrl);
            Assert.Equal("https://example.com/standard-still.jpg", prog.ThumbImageUrl);
        }

        [Fact]
        public void ParseProgramme_DimensionlessStandardPosterDoesNotUseUntypedFallback()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <icon src=""https://example.com/generic-landscape.jpg"" width=""1280"" height=""720"" />
    <image type=""poster"" orient=""P"">https://example.com/dimensionless-poster.jpg</image>
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Null(prog.ImageUrl);
            Assert.Equal(0, prog.ImageWidth);
            Assert.Equal(0, prog.ImageHeight);
        }

        [Fact]
        public void ParseProgramme_StandardImagesRejectWrongOrientInvalidUrlsAndNestedArtwork()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <rating><image type=""backdrop"" orient=""L"">https://example.com/nested.jpg</image></rating>
    <credits><icon src=""https://example.com/nested-icon.jpg"" type=""still"" /></credits>
    <image type=""backdrop"" orient=""P"">https://example.com/wrong-orient.jpg</image>
    <image type=""still"" orient=""L"">/relative.jpg</image>
    <icon src=""https://example.com/legacy-backdrop.jpg"" type=""backdrop"" />
    <icon src=""https://example.com/legacy-still.jpg"" type=""still"" />
    <icon src=""https://example.com/generic.jpg"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Null(prog.ImageUrl);
            Assert.Equal("https://example.com/legacy-backdrop.jpg", prog.BackdropImageUrl);
            Assert.Equal("https://example.com/legacy-still.jpg", prog.ThumbImageUrl);
        }

        [Fact]
        public void ParseProgramme_StandardLandscapeRolesReachProgramInfoSlots()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Standard artwork</title>
    <image type=""backdrop"" orient=""L"">https://example.com/backdrop.jpg</image>
    <image type=""still"" orient=""L"">https://example.com/still.jpg</image>
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);
            var info = M3uEditorTunerHost.BuildProgramInfo(prog, 1, "ch1", prog.Title, prog.Description);

            Assert.Null(info.ImageUrl);
            Assert.Equal("https://example.com/backdrop.jpg", info.BackdropImageUrl);
            Assert.Equal("https://example.com/still.jpg", info.ThumbImageUrl);
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
        public void ParseProgramme_TypedSquarePoster_DoesNotPopulatePrimaryOrUseLegacyFallback()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <icon src=""https://example.com/generic-landscape.jpg"" width=""1280"" height=""720"" />
    <icon src=""https://example.com/square-poster.jpg"" type=""poster"" width=""1400"" height=""1400"" orient=""P"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Null(prog.ImageUrl);
            Assert.Equal(0, prog.ImageWidth);
            Assert.Equal(0, prog.ImageHeight);
        }

        [Fact]
        public void ParseProgramme_TypedPosterWithoutDimensions_DoesNotPopulatePrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <icon src=""https://example.com/generic-landscape.jpg"" width=""1280"" height=""720"" />
    <icon src=""https://example.com/poster-without-dimensions.jpg"" type=""poster"" orient=""P"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Null(prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_TypedPosterWithContradictoryOrientation_DoesNotPopulatePrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <icon src=""https://example.com/landscape-poster.jpg"" type=""poster"" width=""1280"" height=""720"" orient=""P"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Null(prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_TypedPosterWithLandscapeOrientation_DoesNotPopulatePrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <icon src=""https://example.com/portrait-with-landscape-orientation.jpg"" type=""poster"" width=""500"" height=""750"" orient=""L"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Null(prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_TypedPortraitPosterWithoutOrientation_PopulatesPrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <icon src=""https://example.com/portrait-poster.jpg"" type=""poster"" width=""500"" height=""750"" />
  </programme>
</tv>";

            var prog = Assert.Single(Parse(xml)["ch1"]);

            Assert.Equal("https://example.com/portrait-poster.jpg", prog.ImageUrl);
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

            Assert.Contains("ch1", result.Keys);
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

            Assert.Contains("ch1", result.Keys);
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

            Assert.Contains("ch2", result.Keys);
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
