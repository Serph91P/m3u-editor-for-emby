using System;
using System.IO;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedOutputRootSettingsTests
    {
        [Fact]
        public void FreshRoot_IsCanonicalWritableAndRequiresNewConfirmation()
        {
            using (var temp = new TempDirectory())
            {
                var owner = Path.Combine(temp.Path, "plugin-state");
                Directory.CreateDirectory(owner);
                var current = new PluginConfiguration { ManagedSetupReady = true, ManagedPublishingIntegrationId = 17 };
                var root = Path.Combine(temp.Path, "media");
                var requested = new PluginConfiguration
                {
                    ManagedPublishingOutputRoot = Path.Combine(root, "."),
                    ManagedSetupReady = true,
                    ManagedPublishingIntegrationId = 17,
                    ManagedApprovedOutputRoots = Path.Combine(owner, "managed-publishing")
                };

                ManagedSetupService.ValidateConfigurationChange(current, requested, owner);

                Assert.Equal(root, requested.ManagedPublishingOutputRoot);
                Assert.True(Directory.Exists(root));
                Assert.Empty(Directory.GetFileSystemEntries(root));
                Assert.False(requested.ManagedSetupReady);
                Assert.Empty(requested.ManagedApprovedOutputRoots);
                Assert.Equal(17, requested.ManagedPublishingIntegrationId);
                Assert.Empty(current.ManagedPublishingOutputRoot);
                Assert.True(current.ManagedSetupReady);
            }
        }

        [Theory]
        [InlineData("ManagedMappingsJson", "[{\"mapping_id\":1}]")]
        [InlineData("ManagedMappingsJson", "not-json")]
        [InlineData("ManagedMappingsJson", "null")]
        [InlineData("ManagedDirectoryOwnershipJson", "[{}]")]
        [InlineData("ManagedDirectoryOwnershipJson", "not-json")]
        [InlineData("ManagedActiveGeneration", "synthetic-active")]
        [InlineData("ManagedPreviousGeneration", "synthetic-previous")]
        [InlineData("ManagedCatalogRevision", "synthetic-revision")]
        public void ExistingOrUnreadableState_BlocksRootChange(string property, string value)
        {
            using (var temp = new TempDirectory())
            {
                var current = new PluginConfiguration();
                typeof(PluginConfiguration).GetProperty(property).SetValue(current, value);
                var requested = new PluginConfiguration { ManagedPublishingOutputRoot = Path.Combine(temp.Path, "new-media") };

                var error = Assert.Throws<InvalidOperationException>(() =>
                    ManagedSetupService.ValidateConfigurationChange(current, requested, temp.Path));

                Assert.Contains("existing managed", error.Message);
                Assert.False(Directory.Exists(requested.ManagedPublishingOutputRoot));
                Assert.Equal(value, typeof(PluginConfiguration).GetProperty(property).GetValue(current));
            }
        }

        [Fact]
        public void ExistingOutputFiles_BlockChangeAndRemainUntouched()
        {
            using (var temp = new TempDirectory())
            {
                var oldRoot = Path.Combine(temp.Path, "managed-publishing");
                Directory.CreateDirectory(oldRoot);
                var sentinel = Path.Combine(oldRoot, "synthetic.strm");
                File.WriteAllText(sentinel, "synthetic preserved content");
                var requested = new PluginConfiguration { ManagedPublishingOutputRoot = Path.Combine(temp.Path, "new-media") };

                Assert.Throws<InvalidOperationException>(() => ManagedSetupService.ValidateConfigurationChange(
                    new PluginConfiguration(), requested, temp.Path));

                Assert.Equal("synthetic preserved content", File.ReadAllText(sentinel));
                Assert.False(Directory.Exists(requested.ManagedPublishingOutputRoot));
            }
        }

        [Fact]
        public void SamePhysicalRoot_WithExistingState_RemainsReady()
        {
            using (var temp = new TempDirectory())
            {
                var root = Path.Combine(temp.Path, "managed-publishing");
                var current = new PluginConfiguration { ManagedMappingsJson = "[{}]", ManagedSetupReady = true };
                var requested = new PluginConfiguration
                {
                    ManagedPublishingOutputRoot = Path.Combine(root, "."),
                    ManagedMappingsJson = "[{}]",
                    ManagedSetupReady = true,
                    ManagedApprovedOutputRoots = root
                };

                ManagedSetupService.ValidateConfigurationChange(current, requested, temp.Path);

                Assert.Equal(root, requested.ManagedPublishingOutputRoot);
                Assert.True(requested.ManagedSetupReady);
                Assert.Equal(root, requested.ManagedApprovedOutputRoots);
            }
        }

        [Fact]
        public void FileInsteadOfDirectory_IsRejectedWithoutChangingIt()
        {
            using (var temp = new TempDirectory())
            {
                var target = Path.Combine(temp.Path, "existing-file");
                File.WriteAllText(target, "synthetic preserved content");
                var requested = new PluginConfiguration { ManagedPublishingOutputRoot = target };

                Assert.Throws<InvalidOperationException>(() => ManagedSetupService.ValidateConfigurationChange(
                    new PluginConfiguration(), requested, temp.Path));

                Assert.Equal("synthetic preserved content", File.ReadAllText(target));
            }
        }

        [Fact]
        public void RelativeRoot_IsRejectedWithoutCreatingIt()
        {
            using (var temp = new TempDirectory())
            {
                var requested = new PluginConfiguration { ManagedPublishingOutputRoot = "relative-media" };
                Assert.Throws<InvalidOperationException>(() => ManagedSetupService.ValidateConfigurationChange(
                    new PluginConfiguration(), requested, temp.Path));
            }
        }
    }
}
