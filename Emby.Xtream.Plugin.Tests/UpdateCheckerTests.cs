using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class UpdateCheckerTests
    {
        [Fact]
        public void NewerVersionAvailable()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.1.0", "https://github.com/release", "Bug fixes", "2025-01-01");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.0.0", result.CurrentVersion);
            Assert.Equal("1.1.0", result.LatestVersion);
            Assert.Equal("https://github.com/release", result.ReleaseUrl);
            Assert.Equal("Bug fixes", result.ReleaseNotes);
        }

        [Fact]
        public void SameVersionNotAvailable()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0", "https://github.com/release", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Equal("1.0.0", result.LatestVersion);
        }

        [Fact]
        public void OlderVersionNotAvailable()
        {
            var result = UpdateChecker.CompareVersions("2.0.0", "v1.5.0", "https://github.com/release", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void StripLeadingV()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.2.3", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.2.3", result.LatestVersion);
        }

        [Fact]
        public void StripLeadingUpperV()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "V2.0.0", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("2.0.0", result.LatestVersion);
        }

        [Fact]
        public void TagWithoutPrefix()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "1.2.0", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.2.0", result.LatestVersion);
        }

        [Fact]
        public void MalformedTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "not-a-version", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.False(string.IsNullOrEmpty(result.Error));
            Assert.Contains("Could not parse", result.Error);
        }

        [Fact]
        public void NullTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", null, "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Contains("No tag found", result.Error);
        }

        [Fact]
        public void EmptyTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Contains("No tag found", result.Error);
        }

        [Fact]
        public void ThreePartVersionComparison()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void FourPartVersionComparison()
        {
            var result = UpdateChecker.CompareVersions("1.0.0.0", "v1.0.0.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void MajorVersionBump()
        {
            var result = UpdateChecker.CompareVersions("1.9.9", "v2.0.0", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void CurrentVersionHigherMajor()
        {
            var result = UpdateChecker.CompareVersions("3.0.0", "v2.9.9", "", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void PreservesReleaseUrlAndNotes()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0",
                "https://github.com/org/repo/releases/tag/v1.0.0",
                "Release notes here",
                "2025-06-15T10:00:00Z");
            Assert.Equal("https://github.com/org/repo/releases/tag/v1.0.0", result.ReleaseUrl);
            Assert.Equal("Release notes here", result.ReleaseNotes);
            Assert.Equal("2025-06-15T10:00:00Z", result.PublishedAt);
        }

        [Fact]
        public void NullReleaseFieldsDefaultToEmpty()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0", null, null, null);
            Assert.Equal("", result.ReleaseUrl);
            Assert.Equal("", result.ReleaseNotes);
            Assert.Equal("", result.PublishedAt);
        }

        [Fact]
        public void TwoPartVersionParsedCorrectly()
        {
            var result = UpdateChecker.CompareVersions("1.0", "v1.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        // ---- Beta channel: unreliable current version override ----
        //
        // When the installed plugin reports a 4-part System.Version like
        // "1.2.1.0" (because that build did not surface
        // AssemblyInformationalVersion and Emby strips pre-release suffixes),
        // we cannot tell apart "1.2.1" stable from "1.2.1-beta.X". Strict
        // SemVer would say stable > pre-release, leaving a stuck beta install
        // without an update path. On the beta channel we offer the update.

        [Fact]
        public void BetaChannel_UnreliableCurrent_OffersBetaSameCore()
        {
            // Installed reports "1.2.1.0" (unreliable, could be beta.1).
            // Latest beta is 1.2.1-beta.4. Beta channel: offer it.
            var result = UpdateChecker.CompareVersions(
                "1.2.1.0", "v1.2.1-beta.4", "", "", "",
                useBeta: true, currentVersionUnreliable: true);
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void BetaChannel_UnreliableCurrent_DifferentCore_FallsBackToSemver()
        {
            // Installed "1.2.1.0", latest beta "1.3.0-beta.1": SemVer already
            // says update available, override is irrelevant.
            var result = UpdateChecker.CompareVersions(
                "1.2.1.0", "v1.3.0-beta.1", "", "", "",
                useBeta: true, currentVersionUnreliable: true);
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void BetaChannel_UnreliableCurrent_OlderBetaCore_NoUpdate()
        {
            // Installed "1.3.0.0", latest beta "1.2.1-beta.4": don't downgrade.
            var result = UpdateChecker.CompareVersions(
                "1.3.0.0", "v1.2.1-beta.4", "", "", "",
                useBeta: true, currentVersionUnreliable: true);
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void StableChannel_UnreliableCurrent_DoesNotOfferBeta()
        {
            // Same situation but user is NOT on beta channel: keep strict
            // SemVer, do not offer the pre-release.
            var result = UpdateChecker.CompareVersions(
                "1.2.1.0", "v1.2.1-beta.4", "", "", "",
                useBeta: false, currentVersionUnreliable: true);
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void BetaChannel_ReliableCurrent_KeepsSemverSemantics()
        {
            // Helper had a real InformationalVersion: trust it. Stable 1.2.1
            // is genuinely stable, so 1.2.1-beta.4 is older.
            var result = UpdateChecker.CompareVersions(
                "1.2.1", "v1.2.1-beta.4", "", "", "",
                useBeta: true, currentVersionUnreliable: false);
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void BetaChannel_UnreliableCurrent_StableLatest_StrictSemver()
        {
            // Latest is stable; nothing to override (current also has no
            // pre-release tag so SemVer says they are equal numeric core,
            // no update). Don't accidentally offer the same version.
            var result = UpdateChecker.CompareVersions(
                "1.2.1.0", "v1.2.1", "", "", "",
                useBeta: true, currentVersionUnreliable: true);
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void OldOverload_KeepsExistingBehavior()
        {
            // Backward compat: the 5-arg overload must still apply strict
            // SemVer (no override path).
            var result = UpdateChecker.CompareVersions(
                "1.2.1.0", "v1.2.1-beta.4", "", "", "");
            Assert.False(result.UpdateAvailable);
        }

        // ---- SemVer pre-release tests ----

        [Fact]
        public void PreReleaseIsLowerThanStableSameCore()
        {
            // Installed stable 1.2.0 should NOT be offered 1.2.0-beta.1
            var result = UpdateChecker.CompareVersions("1.2.0", "v1.2.0-beta.1", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Null(result.Error);
        }

        [Fact]
        public void StableHigherThanPreReleaseOfPriorCore()
        {
            // Installed 1.0.0-beta.4 should be offered stable 1.1.3
            var result = UpdateChecker.CompareVersions("1.0.0-beta.4", "v1.1.3", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void OldStableNotOfferedOlderBeta()
        {
            // Installed stable 1.1.3 should NOT be offered older 1.0.0-beta.4
            var result = UpdateChecker.CompareVersions("1.1.3", "v1.0.0-beta.4", "", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void NewerBetaCoreOutranksOlderStable()
        {
            // Installed stable 1.1.3 should be offered 1.2.0-beta.1
            var result = UpdateChecker.CompareVersions("1.1.3", "v1.2.0-beta.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void HigherBetaNumberWins()
        {
            var result = UpdateChecker.CompareVersions("1.2.0-beta.1", "v1.2.0-beta.2", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void RcRanksHigherThanBetaSameCore()
        {
            // alphanumeric identifier comparison: "beta" < "rc"
            var result = UpdateChecker.CompareVersions("1.2.0-beta.5", "v1.2.0-rc.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void EqualPreReleaseNoUpdate()
        {
            var result = UpdateChecker.CompareVersions("1.2.0-beta.1", "v1.2.0-beta.1", "", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void SemverParsesEmbyFourPartAssemblyVersion()
        {
            // Emby reports plugin version as 4-part System.Version (e.g. 1.1.3.0)
            // Comparing it against a tag "v1.1.3" must NOT report an update.
            var result = UpdateChecker.CompareVersions("1.1.3.0", "v1.1.3", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Null(result.Error);
        }

        [Fact]
        public void SelectHighestRelease_PicksHighestSemverNotFirst()
        {
            // GitHub returns releases sorted by published_at desc, but a stable
            // hotfix released after a beta may have a LOWER tag (e.g. 1.1.3
            // after 1.2.0-beta.1). On the beta channel we still want the highest
            // SemVer-precedence release as the "latest".
            var json = @"[
                {""tag_name"":""v1.1.3"",""prerelease"":false,""draft"":false,""assets"":[]},
                {""tag_name"":""v1.2.0-beta.1"",""prerelease"":true,""draft"":false,""assets"":[]},
                {""tag_name"":""v1.0.0-beta.4"",""prerelease"":true,""draft"":false,""assets"":[]}
            ]";

            var picked = UpdateChecker.SelectHighestRelease(json);
            Assert.NotNull(picked);
            Assert.Contains("v1.2.0-beta.1", picked);
        }

        [Fact]
        public void SelectHighestRelease_SkipsDrafts()
        {
            var json = @"[
                {""tag_name"":""v9.9.9"",""prerelease"":false,""draft"":true,""assets"":[]},
                {""tag_name"":""v1.2.0"",""prerelease"":false,""draft"":false,""assets"":[]}
            ]";

            var picked = UpdateChecker.SelectHighestRelease(json);
            Assert.NotNull(picked);
            Assert.Contains("v1.2.0", picked);
            Assert.DoesNotContain("v9.9.9", picked);
        }

        [Fact]
        public void SelectHighestRelease_ReturnsNullForEmptyArray()
        {
            Assert.Null(UpdateChecker.SelectHighestRelease("[]"));
        }

        // ---- ExtractDllDownloadUrl tests ----

        [Fact]
        public void ExtractDllDownloadUrl_FindsCorrectAsset()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": [
                    {
                        ""name"": ""source.zip"",
                        ""browser_download_url"": ""https://github.com/example/source.zip""
                    },
                    {
                        ""name"": ""Emby.Xtream.Plugin.dll"",
                        ""browser_download_url"": ""https://github.com/example/Emby.Xtream.Plugin.dll""
                    },
                    {
                        ""name"": ""README.md"",
                        ""browser_download_url"": ""https://github.com/example/README.md""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Equal("https://github.com/example/Emby.Xtream.Plugin.dll", url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullWhenNoMatch()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": [
                    {
                        ""name"": ""source.zip"",
                        ""browser_download_url"": ""https://github.com/example/source.zip""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullWhenAssetsEmpty()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": []
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_CaseInsensitiveMatch()
        {
            var json = @"{
                ""assets"": [
                    {
                        ""name"": ""emby.xtream.plugin.dll"",
                        ""browser_download_url"": ""https://github.com/example/plugin.dll""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Equal("https://github.com/example/plugin.dll", url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullForNullJson()
        {
            var url = UpdateChecker.ExtractDllDownloadUrl(null, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullForNullAssetName()
        {
            var json = @"{ ""assets"": [] }";
            var url = UpdateChecker.ExtractDllDownloadUrl(json, null);
            Assert.Null(url);
        }

        // ---- ExtractFirstRelease tests ----

        [Fact]
        public void ExtractFirstRelease_ReturnsFirstObject()
        {
            var json = @"[
                {""tag_name"":""v1.2.0"",""prerelease"":true,""assets"":[]},
                {""tag_name"":""v1.1.0"",""prerelease"":false,""assets"":[]}
            ]";
            var first = UpdateChecker.ExtractFirstRelease(json);
            Assert.Contains("v1.2.0", first);
            Assert.DoesNotContain("v1.1.0", first);
        }

        [Fact]
        public void ExtractFirstRelease_EmptyArray()
        {
            var json = @"[]";
            var first = UpdateChecker.ExtractFirstRelease(json);
            Assert.Equal("{}", first);
        }

        [Fact]
        public void ExtractFirstRelease_NullInput()
        {
            var first = UpdateChecker.ExtractFirstRelease(null);
            Assert.Equal("{}", first);
        }

        [Fact]
        public void ExtractFirstRelease_EmptyInput()
        {
            var first = UpdateChecker.ExtractFirstRelease("");
            Assert.Equal("{}", first);
        }

        // ---- ExtractJsonBool tests ----

        [Fact]
        public void ExtractJsonBool_True()
        {
            var json = @"{""prerelease"": true, ""draft"": false}";
            Assert.True(UpdateChecker.ExtractJsonBool(json, "prerelease"));
        }

        [Fact]
        public void ExtractJsonBool_False()
        {
            var json = @"{""prerelease"": false}";
            Assert.False(UpdateChecker.ExtractJsonBool(json, "prerelease"));
        }

        [Fact]
        public void ExtractJsonBool_Missing()
        {
            var json = @"{""tag_name"": ""v1.0.0""}";
            Assert.False(UpdateChecker.ExtractJsonBool(json, "prerelease"));
        }
    }
}
