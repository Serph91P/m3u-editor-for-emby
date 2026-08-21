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
            var html = File.ReadAllText(Path.Combine(
                root, "Emby.M3uEditor.Plugin", "Configuration", "Web", "config.html"));
            var javascript = File.ReadAllText(Path.Combine(
                root, "Emby.M3uEditor.Plugin", "Configuration", "Web", "config.js"));

            Assert.Contains("Managed by m3u-editor", html);
            Assert.DoesNotContain("txtManagedPublishingIntegrationId", html);
            Assert.DoesNotContain("txtManagedApprovedOutputRoots", html);
            Assert.DoesNotContain("ManagedPublishingIntegrationId =", javascript);
            Assert.DoesNotContain("ManagedApprovedOutputRoots =", javascript);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null &&
                   !Directory.Exists(Path.Combine(current.FullName, "Emby.M3uEditor.Plugin")))
            {
                current = current.Parent;
            }

            return current == null ? Directory.GetCurrentDirectory() : current.FullName;
        }
    }
}
