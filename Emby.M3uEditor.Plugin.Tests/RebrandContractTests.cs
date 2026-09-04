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
        public void ActiveConfigurationSerializationContract_IsManagedAndLiveTvOnly()
        {
            var expected = new[]
            {
                "BaseUrl", "CachedLiveCategories", "ChannelRemoveTerms", "CustomEpgUrl",
                "EnableChannelNameCleaning", "EnableDiagnosticsLogging", "EnableEpg", "EnableLiveTv",
                "EnableLiveTvDiagnostics", "EpgCacheMinutes", "EpgDaysToFetch", "EpgSource",
                "HttpUserAgent", "IncludeAdultChannels", "LastChannelListHash", "LastInstalledVersion",
                "LiveTvOutputFormat", "M3UCacheMinutes",
                "ManagedActiveGeneration", "ManagedApprovedOutputRoots", "ManagedCatalogRevision",
                "ManagedDryRunSummary", "ManagedLastError", "ManagedLastSuccessTicks", "ManagedMappingsJson",
                "ManagedOmittedVersions", "ManagedPreviousGeneration", "ManagedPublishingApiVersion",
                "ManagedPublishingEnabled", "ManagedPublishingIntegrationId", "ManagedSetupLastResult",
                "ManagedSetupReady", "Password", "SelectedLiveCategoryIds", "UseBetaChannel",
                "UseM3uLogoForAllChannelImages", "Username",
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
            Assert.Contains("var pluginsDir = Plugin.Instance.ApplicationPaths.PluginsPath;", api);
            Assert.Contains("Path.DirectorySeparatorChar", api);
            Assert.Contains("\"Emby.M3uEditor.Plugin.dll\";", api);
            Assert.DoesNotContain("Path.Combine(pluginsDir", api);
            Assert.Contains("File.Move(tempPath, currentDll);", api);
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
                var trackedPath = ResolveTrackedPath(relativePath);
                if (!File.Exists(trackedPath))
                {
                    continue;
                }
                Assert.DoesNotMatch(forbidden, relativePath);
                var bytes = File.ReadAllBytes(trackedPath);
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

        [Fact]
        public void ResolveTrackedPath_RootedPath_IsRejected()
        {
            var rootedPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "outside-repository.txt"));

            Assert.Throws<InvalidDataException>(() => ResolveTrackedPath(rootedPath));
        }

        [Fact]
        public void ResolveTrackedPath_ParentTraversal_IsRejected()
        {
            Assert.Throws<InvalidDataException>(() => ResolveTrackedPath(Path.Join("..", "outside-repository.txt")));
        }

        [Fact]
        public void ResolveTrackedPath_SymbolicLink_IsRejected()
        {
            var root = Path.Join(Path.GetTempPath(), "hermes-verify-" + Guid.NewGuid().ToString("N"));
            var outsidePath = Path.Join(Path.GetTempPath(), "hermes-verify-" + Guid.NewGuid().ToString("N") + ".txt");
            Directory.CreateDirectory(root);
            File.WriteAllText(outsidePath, "outside repository");

            try
            {
                var linkPath = Path.Join(root, "escape-link.txt");
                try
                {
                    File.CreateSymbolicLink(linkPath, outsidePath);
                }
                catch (PlatformNotSupportedException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }

                Assert.Throws<InvalidDataException>(() => ResolveTrackedPath(root, "escape-link.txt"));
            }
            finally
            {
                Directory.Delete(root, true);
                File.Delete(outsidePath);
            }
        }

        private static string ResolveTrackedPath(string relativePath)
        {
            return ResolveTrackedPath(RepositoryRoot, relativePath);
        }

        private static string ResolveTrackedPath(string repositoryRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException("Tracked paths must be repository-relative.");
            }

            var canonicalRoot = Path.GetFullPath(repositoryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Join(canonicalRoot, relativePath));
            var rootPrefix = canonicalRoot
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Tracked path escapes the repository root.");
            }

            EnsureNoReparsePoints(canonicalRoot, fullPath);
            return fullPath;
        }

        private static void EnsureNoReparsePoints(string canonicalRoot, string fullPath)
        {
            var relativePath = fullPath.Substring(canonicalRoot.Length);
            var currentPath = canonicalRoot;
            var segments = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                currentPath = Path.Join(currentPath, segment);
                if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
                {
                    return;
                }

                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Tracked paths must not traverse symbolic links.");
                }
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
