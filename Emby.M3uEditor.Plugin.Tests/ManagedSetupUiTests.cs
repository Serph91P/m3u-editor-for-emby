using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedSetupUiTests
    {
        [Fact]
        public void NormalUi_ShowsManagedStatusWithoutEditingBindingOrApprovedRoot()
        {
            var root = FindRepositoryRoot();
            var html = File.ReadAllText(Path.Join(
                root, "Emby.M3uEditor.Plugin", "Configuration", "Web", "config.html"));
            var javascript = File.ReadAllText(Path.Join(
                root, "Emby.M3uEditor.Plugin", "Configuration", "Web", "config.js"));

            Assert.Contains("Managed by m3u-editor", html);
            Assert.DoesNotContain("txtManagedPublishingIntegrationId", html);
            Assert.DoesNotContain("txtManagedApprovedOutputRoots", html);
            Assert.DoesNotContain("ManagedPublishingIntegrationId =", javascript);
            Assert.DoesNotContain("ManagedApprovedOutputRoots =", javascript);
        }

        [Fact]
        public void LiveTvTunerLimitUi_RunsSaveAndAuthenticatedImportRegressionHarness()
        {
            var root = FindRepositoryRoot();
            var htmlPath = Path.Join(
                root, "Emby.M3uEditor.Plugin", "Configuration", "Web", "config.html");
            var javascriptPath = Path.Join(
                root, "Emby.M3uEditor.Plugin", "Configuration", "Web", "config.js");
            var harnessPath = Path.Join(
                root, "Emby.M3uEditor.Plugin.Tests", "Issue85UiHarness.js");
            var html = File.ReadAllText(htmlPath);

            Assert.Contains("txtLiveTvTunerCount", html);
            Assert.Contains("btnImportXtreamLimit", html);

            var startInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add(javascriptPath);

            using (var process = Process.Start(startInfo))
            {
                Assert.NotNull(process);
                var standardOutput = process.StandardOutput.ReadToEnd();
                var standardError = process.StandardError.ReadToEnd();
                Assert.True(process.WaitForExit(30000), "Node UI harness timed out.");
                Assert.True(
                    process.ExitCode == 0,
                    "Node UI harness failed." + Environment.NewLine + standardOutput + standardError);
                Assert.Contains("20 scenarios passed", standardOutput);
            }
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null &&
                    !Directory.Exists(Path.Join(current.FullName, "Emby.M3uEditor.Plugin")))
            {
                current = current.Parent;
            }

            return current == null ? Directory.GetCurrentDirectory() : current.FullName;
        }
    }
}
