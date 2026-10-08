using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        public void ParseProgramme_M3uEditorIdentityEpisodeNumbers_TransportContentAndSeriesIds()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""first"">
    <title>Synthetic series</title>
    <episode-num system=""m3u-editor:content-id"">episode-12</episode-num>
    <episode-num system=""m3u-editor:series-id"">series-4</episode-num>
  </programme>
</tv>";

            var program = Assert.Single(Parse(xml)["first"]);
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "first", program.Title, program.Description);

            Assert.Equal("episode-12", program.ContentId);
            Assert.Equal("series-4", program.SeriesId);
            Assert.Equal("xtream:program:episode-12", info.ShowId);
            Assert.Equal("xtream:series:series-4", info.SeriesId);
        }

        [Fact]
        public void ParseProgramme_UnknownEpisodeNumberSystem_DoesNotCreateContentIdentity()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""first"">
    <title>Synthetic series</title>
    <episode-num system=""onscreen"">S01E12</episode-num>
  </programme>
</tv>";

            var program = Assert.Single(Parse(xml)["first"]);
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "first", program.Title, program.Description);

            Assert.True(string.IsNullOrEmpty(program.ContentId));
            Assert.Equal("xtream:occurrence:1:1735732800", info.ShowId);
            Assert.Null(info.SeriesId);
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
        public void ParseProgramme_ExactCoreExportFromSerializedEnricherOutput_ReachesPrimaryProgramInfo()
        {
            var xmlPath = System.Environment.GetEnvironmentVariable("CROSS_REPO_CORE_XML");
            var enricherOutputPath = System.Environment.GetEnvironmentVariable("CROSS_REPO_ENRICHER_OUTPUT");
            if (string.IsNullOrWhiteSpace(xmlPath) || string.IsNullOrWhiteSpace(enricherOutputPath))
                return;

            using var enricherOutput = JsonDocument.Parse(File.ReadAllBytes(enricherOutputPath));
            var root = enricherOutput.RootElement;
            var expectedTitle = root.GetProperty("programme_before").GetProperty("title").GetString();
            var expectedPoster = root.GetProperty("host_changes").GetProperty("images")[0];
            var expectedUrl = expectedPoster.GetProperty("url").GetString();
            var expectedWidth = expectedPoster.GetProperty("width").GetInt32();
            var expectedHeight = expectedPoster.GetProperty("height").GetInt32();

            using var stream = File.OpenRead(xmlPath);
            var programmes = XmltvParser.Parse(stream, null, null);
            var program = Assert.Single(programmes.Values.SelectMany(value => value).Where(value => value.Title == expectedTitle));
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "synthetic", program.Title, program.Description);

            if (string.Equals(System.Environment.GetEnvironmentVariable("CROSS_REPO_EXPECT_EMBY_PRIMARY"), "false", System.StringComparison.OrdinalIgnoreCase))
            {
                Assert.Null(program.ImageUrl);
                Assert.Equal(0, program.ImageWidth);
                Assert.Equal(0, program.ImageHeight);
                Assert.Null(info.ImageUrl);
                return;
            }

            Assert.Equal(expectedUrl, program.ImageUrl);
            Assert.Equal(expectedWidth, program.ImageWidth);
            Assert.Equal(expectedHeight, program.ImageHeight);
            Assert.Equal(expectedUrl, info.ImageUrl);
            Assert.Equal(expectedWidth, info.ImageWidth);
            Assert.Equal(expectedHeight, info.ImageHeight);
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
        public void ParseProgramme_OrderedTypedBackdrops_SelectsFirstSuitableCandidateForProgramInfo()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ordered""><icon src=""https://example.com/canonical-backdrop.jpg"" type=""backdrop"" width=""1920"" height=""1080"" orient=""L"" /><icon src=""https://example.com/alternate-backdrop.jpg"" type=""backdrop"" width=""1280"" height=""720"" orient=""L"" /></programme>
</tv>";

            var program = Assert.Single(Parse(xml)["ordered"]);
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "ordered", program.Title, program.Description);

            Assert.Equal("https://example.com/canonical-backdrop.jpg", program.BackdropImageUrl);
            Assert.Equal("https://example.com/canonical-backdrop.jpg", info.BackdropImageUrl);
        }

        [Fact]
        public void ParseProgramme_OrderedTypedBackdrops_SkipsConflictsAndKeepsOtherRolePriority()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""conflict-first""><icon src=""https://example.com/rejected.jpg"" type=""backdrop"" width=""1920"" height=""1080"" orient=""L"" /><icon src=""https://example.com/rejected.jpg"" type=""poster"" width=""500"" height=""750"" orient=""P"" /><icon src=""https://example.com/eligible.jpg"" type=""backdrop"" width=""1280"" height=""720"" orient=""L"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""all-conflicting""><icon src=""https://example.com/rejected-only.jpg"" type=""backdrop"" width=""1920"" height=""1080"" orient=""L"" /><icon src=""https://example.com/rejected-only.jpg"" type=""poster"" width=""500"" height=""750"" orient=""P"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""duplicate""><icon src=""https://example.com/canonical.jpg"" type=""backdrop"" width=""1920"" height=""1080"" orient=""L"" /><icon src=""https://example.com/canonical.jpg"" type=""backdrop"" width=""1920"" height=""1080"" orient=""L"" /><icon src=""https://example.com/alternate.jpg"" type=""backdrop"" width=""1280"" height=""720"" orient=""L"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""other-roles""><image type=""backdrop"" orient=""L"">https://example.com/standard-first.jpg</image><image type=""backdrop"" orient=""L"">https://example.com/standard-last.jpg</image><image type=""still"" orient=""L"">https://example.com/still-first.jpg</image><image type=""still"" orient=""L"">https://example.com/still-last.jpg</image><icon src=""https://example.com/logo-first.png"" type=""logo"" /><icon src=""https://example.com/logo-last.png"" type=""logo"" /></programme>
</tv>";

            var programs = Parse(xml);
            Assert.Equal("https://example.com/eligible.jpg", Assert.Single(programs["conflict-first"]).BackdropImageUrl);
            Assert.Null(Assert.Single(programs["all-conflicting"]).BackdropImageUrl);
            Assert.Equal("https://example.com/canonical.jpg", Assert.Single(programs["duplicate"]).BackdropImageUrl);

            var otherRoles = Assert.Single(programs["other-roles"]);
            Assert.Equal("https://example.com/standard-first.jpg", otherRoles.BackdropImageUrl);
            Assert.Equal("https://example.com/still-last.jpg", otherRoles.ThumbImageUrl);
            Assert.Equal("https://example.com/logo-last.png", otherRoles.LogoImageUrl);
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
        public void ParseProgramme_Non16By9OrSquareUntypedIcons_DoNotReachProgramInfoPrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""non16by9""><icon src=""https://example.com/non16by9.jpg"" width=""1280"" height=""800"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""square""><icon src=""https://example.com/square.jpg"" width=""750"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""portrait""><icon src=""https://example.com/portrait.jpg"" width=""500"" height=""750"" /></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "non16by9", "square", "portrait" })
            {
                var program = Assert.Single(programs[channelId]);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, channelId, program.Title, program.Description);

                Assert.Null(program.ImageUrl);
                Assert.Null(info.ImageUrl);
            }
        }

        [Fact]
        public void ParseProgramme_Legacy16By9IconAfterSquares_ReachesPrimaryAndBackdropForAllFrozenShapes()
        {
            var cases = new[]
            {
                new { Id = "case_02", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 1400, 1400 }, new[] { 960, 540 } } },
                new { Id = "case_04", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 1400, 1400 }, new[] { 1400, 1400 }, new[] { 960, 540 } } },
                new { Id = "case_06", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 2000, 2000 }, new[] { 1400, 1400 }, new[] { 960, 540 } } },
                new { Id = "case_07", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 1400, 1400 }, new[] { 960, 540 } } },
                new { Id = "case_08", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 1400, 1400 }, new[] { 2000, 2000 }, new[] { 960, 540 } } },
                new { Id = "case_09", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 960, 540 } } },
                new { Id = "case_10", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 1400, 1400 }, new[] { 1400, 1400 }, new[] { 960, 540 } } },
                new { Id = "case_11", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 960, 540 } } },
                new { Id = "case_13", Dimensions = new[] { new[] { 1400, 1400 }, new[] { 960, 540 } } },
            };

            foreach (var testCase in cases)
            {
                var xml = new StringBuilder();
                xml.Append("<tv><programme start=\"20250101120000 +0000\" stop=\"20250101130000 +0000\" channel=\"");
                xml.Append(testCase.Id);
                xml.Append("\">");
                xml.Append("<icon src=\"https://fixture.invalid/");
                xml.Append(testCase.Id);
                xml.Append("-square-0.jpg\" />");
                for (var index = 0; index < testCase.Dimensions.Length; index++)
                {
                    var dimensions = testCase.Dimensions[index];
                    var isLandscape = dimensions[0] == 960 && dimensions[1] == 540;
                    xml.Append("<icon src=\"https://fixture.invalid/");
                    xml.Append(testCase.Id);
                    xml.Append(isLandscape ? "-landscape.jpg\"" : "-square-" + index + ".jpg\"");
                    xml.Append(" width=\"");
                    xml.Append(dimensions[0]);
                    xml.Append("\" height=\"");
                    xml.Append(dimensions[1]);
                    xml.Append("\" />");
                }
                xml.Append("</programme></tv>");

                var program = Assert.Single(Parse(xml.ToString())[testCase.Id]);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, testCase.Id, program.Title, program.Description);
                var expectedUrl = "https://fixture.invalid/" + testCase.Id + "-landscape.jpg";

                Assert.Equal(expectedUrl, program.ImageUrl);
                Assert.Equal(960, program.ImageWidth);
                Assert.Equal(540, program.ImageHeight);
                Assert.Equal(expectedUrl, program.BackdropImageUrl);
                Assert.Equal(expectedUrl, info.ImageUrl);
                Assert.Equal(960, info.ImageWidth);
                Assert.Equal(540, info.ImageHeight);
                Assert.Equal(expectedUrl, info.BackdropImageUrl);
            }
        }

        [Fact]
        public void ParseProgramme_LegacyLandscapeFallback_AbstainsOnAlternativesAndConflicts()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""duplicate""><icon src=""https://example.com/duplicate.jpg"" width=""960"" height=""540"" orient=""L"" /><icon src=""https://example.com/duplicate.jpg"" width=""960"" height=""540"" orient=""L"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""alternatives""><icon src=""https://example.com/one.jpg"" width=""960"" height=""540"" /><icon src=""https://example.com/two.jpg"" width=""1280"" height=""720"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""conflict""><icon src=""https://example.com/conflict.jpg"" width=""960"" height=""540"" orient=""L"" /><icon src=""https://example.com/conflict.jpg"" width=""960"" height=""540"" orient=""P"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""typed""><icon src=""https://example.com/wide.jpg"" width=""960"" height=""540"" /><icon src=""https://example.com/backdrop.jpg"" type=""backdrop"" width=""1920"" height=""1080"" orient=""L"" /></programme>
</tv>";

            var programs = Parse(xml);
            var duplicate = Assert.Single(programs["duplicate"]);
            Assert.Equal("https://example.com/duplicate.jpg", duplicate.ImageUrl);
            Assert.Equal("https://example.com/duplicate.jpg", duplicate.BackdropImageUrl);

            foreach (var channelId in new[] { "alternatives", "conflict", "typed" })
            {
                var program = Assert.Single(programs[channelId]);
                Assert.Null(program.ImageUrl);
            }
            Assert.Equal("https://example.com/backdrop.jpg", Assert.Single(programs["typed"]).BackdropImageUrl);
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
        public void ParseProgramme_TypedPosterAlternatives_DeduplicateOrAbstainDeterministically()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""duplicates""><icon src=""https://example.com/one.jpg"" type=""poster"" width=""500"" height=""750"" /><icon src=""https://example.com/one.jpg"" type=""poster"" width=""500"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""distinct""><icon src=""https://example.com/one.jpg"" type=""poster"" width=""500"" height=""750"" /><icon src=""https://example.com/two.jpg"" type=""poster"" width=""500"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""conflicting""><icon src=""https://example.com/one.jpg"" type=""poster"" width=""500"" height=""750"" /><icon src=""https://example.com/two.jpg"" type=""poster"" width=""600"" height=""900"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""orientation-conflict""><icon src=""https://example.com/one.jpg"" type=""poster"" width=""500"" height=""750"" orient=""P"" /><icon src=""https://example.com/one.jpg"" type=""poster"" width=""500"" height=""750"" orient=""L"" /></programme>
</tv>";

            var programs = Parse(xml);
            var duplicate = Assert.Single(programs["duplicates"]);
            Assert.Equal("https://example.com/one.jpg", duplicate.ImageUrl);
            Assert.Equal("https://example.com/one.jpg", M3uEditorTunerHost.BuildProgramInfo(duplicate, 1, "duplicates", duplicate.Title, duplicate.Description).ImageUrl);

            foreach (var channelId in new[] { "distinct", "conflicting", "orientation-conflict" })
            {
                var program = Assert.Single(programs[channelId]);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, channelId, program.Title, program.Description);

                Assert.Null(program.ImageUrl);
                Assert.Null(info.ImageUrl);
            }
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
        public void ParseProgramme_ConflictingStandardAndLegacyEvidenceForSameUrlNeverPromotesPrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""legacy-first""><icon src=""https://example.com/same.jpg"" width=""500"" height=""500"" /><image type=""poster"" orient=""P"">https://example.com/same.jpg</image><icon src=""https://example.com/same.jpg"" type=""poster"" width=""500"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""standard-first""><image type=""poster"" orient=""P"">https://example.com/same.jpg</image><icon src=""https://example.com/same.jpg"" type=""poster"" width=""500"" height=""750"" /><icon src=""https://example.com/same.jpg"" width=""500"" height=""500"" /></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "legacy-first", "standard-first" })
            {
                var program = Assert.Single(programs[channelId]);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, channelId, program.Title, program.Description);

                Assert.Null(program.ImageUrl);
                Assert.Null(info.ImageUrl);
            }
        }

        [Fact]
        public void ParseProgramme_ConflictingStandardRolesForSameUrlNeverPromoteEitherRole()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""first""><image type=""poster"" orient=""P"">https://example.com/same.jpg</image><image type=""backdrop"" orient=""L"">https://example.com/same.jpg</image><icon src=""https://example.com/same.jpg"" width=""600"" height=""900"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""last""><icon src=""https://example.com/same.jpg"" width=""600"" height=""900"" /><image type=""backdrop"" orient=""L"">https://example.com/same.jpg</image><image type=""poster"" orient=""P"">https://example.com/same.jpg</image></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "first", "last" })
            {
                var program = Assert.Single(programs[channelId]);

                Assert.Null(program.ImageUrl);
                Assert.Null(program.BackdropImageUrl);
            }
        }

        [Fact]
        public void ParseProgramme_ConflictingStandardRolesDoNotEraseIndependentBackdrop()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""valid-first""><image type=""backdrop"" orient=""L"">https://example.com/valid.jpg</image><image type=""poster"" orient=""P"">https://example.com/conflict.jpg</image><image type=""backdrop"" orient=""L"">https://example.com/conflict.jpg</image><icon src=""https://example.com/conflict.jpg"" width=""600"" height=""900"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""valid-last""><icon src=""https://example.com/conflict.jpg"" width=""600"" height=""900"" /><image type=""backdrop"" orient=""L"">https://example.com/conflict.jpg</image><image type=""poster"" orient=""P"">https://example.com/conflict.jpg</image><image type=""backdrop"" orient=""L"">https://example.com/valid.jpg</image></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "valid-first", "valid-last" })
            {
                var program = Assert.Single(programs[channelId]);
                Assert.Null(program.ImageUrl);
                Assert.Equal("https://example.com/valid.jpg", program.BackdropImageUrl);
            }
        }

        [Fact]
        public void ParseProgramme_UntypedIconWithIncompleteGeometryPreservesLegacyUrlWithoutClaimingPortrait()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""width""><icon src=""https://example.com/width.jpg"" width=""500"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""height""><icon src=""https://example.com/height.jpg"" height=""750"" /></programme>
</tv>";

            var programs = Parse(xml);
            Assert.Equal("https://example.com/width.jpg", Assert.Single(programs["width"]).ImageUrl);
            Assert.Equal("https://example.com/height.jpg", Assert.Single(programs["height"]).ImageUrl);
        }

        [Fact]
        public void ParseProgramme_StandardOrientationConflictPersistsWhileIndependentPortraitSurvives()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""conflict-first""><image type=""poster"" orient=""P"">https://example.com/conflict.jpg</image><image type=""poster"" orient=""L"">https://example.com/conflict.jpg</image><icon src=""https://example.com/conflict.jpg"" width=""600"" height=""900"" /><image type=""poster"" orient=""P"">https://example.com/valid.jpg</image><icon src=""https://example.com/valid.jpg"" width=""500"" height=""750"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""conflict-last""><icon src=""https://example.com/valid.jpg"" width=""500"" height=""750"" /><image type=""poster"" orient=""P"">https://example.com/valid.jpg</image><icon src=""https://example.com/conflict.jpg"" width=""600"" height=""900"" /><image type=""poster"" orient=""L"">https://example.com/conflict.jpg</image><image type=""poster"" orient=""P"">https://example.com/conflict.jpg</image></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "conflict-first", "conflict-last" })
            {
                var program = Assert.Single(programs[channelId]);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, channelId, program.Title, program.Description);

                Assert.Equal("https://example.com/valid.jpg", program.ImageUrl);
                Assert.Equal(500, program.ImageWidth);
                Assert.Equal(750, program.ImageHeight);
                Assert.Equal("https://example.com/valid.jpg", info.ImageUrl);
            }
        }

        [Fact]
        public void ParseProgramme_RejectedMixedDialectUrlCannotHealButDoesNotBlockAlternative()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""first""><image type=""poster"" orient=""P"">https://example.com/conflict.jpg</image><icon src=""https://example.com/conflict.jpg"" width=""500"" height=""500"" /><icon src=""https://example.com/conflict.jpg"" type=""poster"" width=""500"" height=""750"" /><icon src=""https://example.com/valid.jpg"" type=""poster"" width=""400"" height=""600"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""last""><icon src=""https://example.com/valid.jpg"" type=""poster"" width=""400"" height=""600"" /><icon src=""https://example.com/conflict.jpg"" type=""poster"" width=""500"" height=""750"" /><icon src=""https://example.com/conflict.jpg"" width=""500"" height=""500"" /><image type=""poster"" orient=""P"">https://example.com/conflict.jpg</image></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var channelId in new[] { "first", "last" })
            {
                var program = Assert.Single(programs[channelId]);
                Assert.Equal("https://example.com/valid.jpg", program.ImageUrl);
                Assert.Equal(400, program.ImageWidth);
                Assert.Equal(600, program.ImageHeight);
            }
        }

        [Fact]
        public void ParseProgramme_ConsistentStandardAndLegacyPortraitEvidencePromotesPrimary()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1""><image type=""poster"" orient=""P"">https://example.com/poster.jpg</image><icon src=""https://example.com/poster.jpg"" width=""500"" height=""750"" /><icon src=""https://example.com/poster.jpg"" type=""poster"" width=""500"" height=""750"" /></programme>
</tv>";

            var program = Assert.Single(Parse(xml)["ch1"]);
            var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, "ch1", program.Title, program.Description);

            Assert.Equal("https://example.com/poster.jpg", program.ImageUrl);
            Assert.Equal(500, program.ImageWidth);
            Assert.Equal(750, program.ImageHeight);
            Assert.Equal("https://example.com/poster.jpg", info.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_UntypedIconWithIncompleteGeometryPreservesDimensionsThroughProgramInfo()
        {
            const string xml = @"<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""none""><icon src=""https://example.com/none.jpg"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""width""><icon src=""https://example.com/width.jpg"" width=""500"" /></programme>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""height""><icon src=""https://example.com/height.jpg"" height=""750"" /></programme>
</tv>";

            var programs = Parse(xml);
            foreach (var expected in new[]
            {
                new { Channel = "none", Url = "https://example.com/none.jpg", Width = 0, Height = 0 },
                new { Channel = "width", Url = "https://example.com/width.jpg", Width = 500, Height = 0 },
                new { Channel = "height", Url = "https://example.com/height.jpg", Width = 0, Height = 750 },
            })
            {
                var program = Assert.Single(programs[expected.Channel]);
                var info = M3uEditorTunerHost.BuildProgramInfo(program, 1, expected.Channel, program.Title, program.Description);

                Assert.Equal(expected.Url, program.ImageUrl);
                Assert.Equal(expected.Width, program.ImageWidth);
                Assert.Equal(expected.Height, program.ImageHeight);
                Assert.Equal(expected.Url, info.ImageUrl);
                Assert.Equal(expected.Width, info.ImageWidth);
                Assert.Equal(expected.Height, info.ImageHeight);
            }
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
