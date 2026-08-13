using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class RebrandContractTests
    {
        private static readonly string RepositoryRoot = FindRepositoryRoot();

        [Fact]
        public void ProjectAndReleasePaths_UseNewIdentity()
        {
            Assert.True(File.Exists(Path.Combine(RepositoryRoot,
                "Emby.M3uEditor.Plugin", "Emby.M3uEditor.Plugin.csproj")));
            Assert.True(File.Exists(Path.Combine(RepositoryRoot,
                "Emby.M3uEditor.Plugin.Tests", "Emby.M3uEditor.Plugin.Tests.csproj")));

            var releaseConfig = ReadText(".releaserc.json");
            Assert.Contains("artifacts/Emby.M3uEditor.Plugin.dll", releaseConfig);
            Assert.Contains("m3u-editor-for-emby-${nextRelease.version}.zip", releaseConfig);
            Assert.Contains("m3u-editor-for-emby-${nextRelease.version}.sha256", releaseConfig);
            Assert.Contains("m3u-editor-for-emby-${nextRelease.version}.md5", releaseConfig);

            var package = ReadText("package.json");
            Assert.Contains("\"name\": \"m3u-editor-for-emby-release\"", package);
            Assert.Contains("github.com/Serph91P/m3u-editor-for-emby.git", package);
        }

        [Fact]
        public void ReleaseScripts_UseNewProjectDllAndArtifactNames()
        {
            var build = ReadText("scripts/release/build-artifacts.sh");
            Assert.Contains("Emby.M3uEditor.Plugin/Emby.M3uEditor.Plugin.csproj", build);
            Assert.Contains("DLL_NAME=\"Emby.M3uEditor.Plugin.dll\"", build);
            Assert.Contains("m3u-editor-for-emby-${VERSION}.zip", build);

            var manifest = ReadText("scripts/release/update-manifest.py");
            Assert.Contains("PLUGIN_NAME = \"m3u-editor for Emby\"", manifest);
            Assert.Contains("DLL_ASSET_NAME = \"Emby.M3uEditor.Plugin.dll\"", manifest);
            Assert.Contains("Serph91P/m3u-editor-for-emby", manifest);
            Assert.Contains("b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5", manifest);
        }

        [Fact]
        public void ConfigurationSerializationContract_IsPreserved()
        {
            var expected = new[]
            {
                "AutoSyncDailyTime", "AutoSyncEnabled", "AutoSyncIntervalHours", "AutoSyncMode",
                "BaseUrl", "CachedDispatcharrProfiles", "CachedLiveCategories", "CachedSeriesCategories",
                "CachedVodCategories", "ChannelRemoveTerms", "CleanupOrphans", "ContentRemoveTerms",
                "CustomEpgUrl", "DeferEpgToGuideData", "DetectedBackendName", "DetectedBackendType",
                "DispatcharrFallbackToXtream", "DispatcharrPass", "DispatcharrUrl", "DispatcharrUser",
                "EnableChannelNameCleaning", "EnableContentNameCleaning", "EnableDiagnosticsLogging",
                "EnableDispatcharr", "EnableEpg", "EnableLiveTv", "EnableLiveTvDiagnostics",
                "EnableNfoFiles", "EnableSeriesIdFolderNaming", "EnableSeriesMetadataLookup",
                "EnableTmdbFallbackLookup", "EnableTmdbFolderNaming", "EpgCacheMinutes", "EpgDaysToFetch",
                "EpgSource", "ForceAudioTranscode", "HttpUserAgent", "IncludeAdultChannels",
                "LastBackendDetectionTicks", "LastChannelListHash", "LastInstalledVersion",
                "LastMovieSyncTimestamp", "LastSeriesSyncTimestamp", "LiveTvOutputFormat", "M3UCacheMinutes",
                "ManagedActiveGeneration", "ManagedApprovedOutputRoots", "ManagedCatalogRevision",
                "ManagedDryRunSummary", "ManagedLastError", "ManagedLastSuccessTicks", "ManagedMappingsJson",
                "ManagedOmittedVersions", "ManagedPreviousGeneration", "ManagedPublishingApiVersion",
                "ManagedPublishingEnabled", "ManagedPublishingIntegrationId", "MovieFolderMappings", "MovieFolderMode", "OrphanSafetyThreshold",
                "Password", "SelectedDispatcharrProfileIds", "SelectedLiveCategoryIds",
                "SelectedSeriesCategoryIds", "SelectedVodCategoryIds", "SeriesEpisodeHashesJson",
                "SeriesFolderMappings", "SeriesFolderMode", "SmartSkipExisting", "StrmLibraryPath",
                "StrmNamingVersion", "SyncHistoryJson", "SyncMovies", "SyncParallelism", "SyncSeries",
                "TvdbFolderIdOverrides", "UseBetaChannel", "UseM3uLogoForAllChannelImages", "Username",
            };

            var actual = typeof(PluginConfiguration)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual);
        }

        [Fact]
        public void BuiltInUpdater_ReplacesTheLoadedDllPathAcrossTheAssemblyRename()
        {
            var api = ReadText("Emby.M3uEditor.Plugin/Api/M3uEditorApi.cs");

            Assert.Contains("var currentDll = typeof(Plugin).Assembly.Location;", api);
            Assert.Contains("File.Move(tempPath, currentDll);", api);
            Assert.Contains("Path.Combine(pluginsDir, \"Emby.M3uEditor.Plugin.dll\")", api);
        }

        [Fact]
        public void TrackedTree_ContainsNoPriorProductIdentityExceptUpgradeCompatibility()
        {
            var first = "Xtr" + "eam";
            var expressions = new[]
            {
                first + @"[^A-Za-z0-9]*(?:Tuner|Plugin)",
                @"Emby[^A-Za-z0-9]*" + first,
            };
            var forbidden = new Regex(string.Join("|", expressions), RegexOptions.IgnoreCase);

            foreach (var relativePath in GetTrackedFiles())
            {
                Assert.DoesNotMatch(forbidden, relativePath);
                var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot, relativePath));
                var content = Encoding.Latin1.GetString(bytes);
                if (relativePath == "Emby.M3uEditor.Plugin/Plugin.cs")
                {
                    var compatibilityIdentifiers = new[]
                    {
                        "Emby.Xtr" + "eam.Plugin.xml",
                        "Xtr" + "eamTuner",
                        "xtr" + "eamconfig",
                    };
                    foreach (var identifier in compatibilityIdentifiers)
                    {
                        content = content.Replace(identifier, string.Empty);
                    }
                }
                Assert.DoesNotMatch(forbidden, content);
            }
        }

        private static string ReadText(string relativePath)
        {
            return File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));
        }

        private static IEnumerable<string> GetTrackedFiles()
        {
            var startInfo = new ProcessStartInfo("git", "ls-files -z")
            {
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using (var process = Process.Start(startInfo))
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
                return output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                    && Directory.Exists(Path.Combine(directory.FullName, "scripts", "release")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
