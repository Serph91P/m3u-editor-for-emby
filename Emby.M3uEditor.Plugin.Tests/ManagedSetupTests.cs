using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Api;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedSetupTests : IDisposable
    {
        private readonly TempDirectory _owner = new TempDirectory();

        [Fact]
        public void SetupRoute_IsVersionedAndAdministratorAuthenticatedForGetAndPut()
        {
            var requestType = typeof(ManagedSetupRequest);
            var route = Assert.Single(requestType.GetCustomAttributes(typeof(RouteAttribute), true)
                .Cast<RouteAttribute>());
            var auth = Assert.Single(requestType.GetCustomAttributes(typeof(AuthenticatedAttribute), true)
                .Cast<AuthenticatedAttribute>());

            Assert.Equal("/M3uEditor/Managed/Setup/V1", route.Path);
            Assert.Contains("GET", route.Verbs);
            Assert.Contains("PUT", route.Verbs);
            Assert.Equal("Admin", auth.Roles);
            Assert.NotNull(typeof(M3uEditorApi).GetMethod("Get", new[] { requestType }));
            Assert.NotNull(typeof(M3uEditorApi).GetMethod("Put", new[] { requestType }));
        }

        [Fact]
        public void Setup_PositiveBinding_CreatesAndPersistsOneCanonicalWritableRoot()
        {
            var mappings = "[{\"mapping_uuid\":\"existing\"}]";
            var config = new PluginConfiguration
            {
                ManagedApprovedOutputRoots = Path.Combine(_owner.Path, "legacy-approved"),
                ManagedMappingsJson = mappings
            };
            var saves = 0;
            var service = new ManagedSetupService(_owner.Path);

            var result = service.Put(config, 71, () => saves++);

            Assert.True(result.Ready, result.Result);
            Assert.Equal(ManagedSetupService.ApiVersion, result.CapabilityVersion);
            Assert.Equal(71, result.IntegrationId);
            Assert.Equal(Path.Combine(_owner.Path, "managed-publishing"), result.ConfirmedRoot);
            Assert.True(Directory.Exists(result.ConfirmedRoot));
            Assert.Equal(result.ConfirmedRoot, config.ManagedApprovedOutputRoots);
            Assert.Equal(mappings, config.ManagedMappingsJson);
            Assert.True(config.ManagedSetupReady);
            Assert.Equal("Ready", config.ManagedSetupLastResult);
            Assert.Equal(1, saves);
        }

        [Fact]
        public void Setup_RepeatedRequest_IsIdempotent()
        {
            var config = new PluginConfiguration();
            var saves = 0;
            var service = new ManagedSetupService(_owner.Path);

            var first = service.Put(config, 17, () => saves++);
            var second = service.Put(config, 17, () => saves++);

            Assert.True(first.Ready);
            Assert.True(second.Ready);
            Assert.Equal(first.ConfirmedRoot, second.ConfirmedRoot);
            Assert.Equal(1, saves);
        }

        [Fact]
        public async Task Setup_ConcurrentConflictingBindings_OnlyOneCommits()
        {
            var config = new PluginConfiguration();
            var serviceA = new ManagedSetupService(_owner.Path);
            var serviceB = new ManagedSetupService(_owner.Path);

            var results = await Task.WhenAll(
                Task.Run(() => serviceA.Put(config, 41, () => { })),
                Task.Run(() => serviceB.Put(config, 42, () => { })));

            var accepted = Assert.Single(results.Where(result => result.Ready));
            var rejected = Assert.Single(results.Where(result => !result.Ready));
            Assert.Equal(accepted.IntegrationId, config.ManagedPublishingIntegrationId);
            Assert.Equal(0, rejected.IntegrationId);
            Assert.Contains("conflict", rejected.Result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(string.Empty, config.ManagedMappingsJson);
        }

        [Fact]
        public void Setup_EnabledLegacyWriterOverlap_LeavesConfigurationUnchanged()
        {
            var originalRoot = Path.Combine(_owner.Path, "old-root");
            var config = new PluginConfiguration
            {
                ManagedPublishingIntegrationId = 9,
                ManagedApprovedOutputRoots = originalRoot,
                ManagedMappingsJson = "existing mappings",
                SyncMovies = true,
                StrmLibraryPath = _owner.Path
            };

            var result = new ManagedSetupService(_owner.Path).Put(config, 9, () => { });

            Assert.False(result.Ready);
            Assert.Contains("legacy", result.Result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(9, config.ManagedPublishingIntegrationId);
            Assert.Equal(originalRoot, config.ManagedApprovedOutputRoots);
            Assert.Equal("existing mappings", config.ManagedMappingsJson);
            Assert.False(config.ManagedSetupReady);
        }

        [Fact]
        public void Setup_OverlappingExistingApproval_LeavesConfigurationUnchanged()
        {
            var config = new PluginConfiguration
            {
                ManagedApprovedOutputRoots = _owner.Path,
                ManagedMappingsJson = "existing mappings"
            };

            var result = new ManagedSetupService(_owner.Path).Put(config, 3, () => { });

            Assert.False(result.Ready);
            Assert.Contains("overlap", result.Result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(_owner.Path, config.ManagedApprovedOutputRoots);
            Assert.Equal("existing mappings", config.ManagedMappingsJson);
        }

        [Fact]
        public void Setup_FileSystemOwnerRoot_IsRejectedWithoutExposingPath()
        {
            var ownerRoot = Path.GetPathRoot(_owner.Path);
            var config = new PluginConfiguration();

            var result = new ManagedSetupService(ownerRoot).Put(config, 3, () => { });

            Assert.False(result.Ready);
            Assert.Null(result.ConfirmedRoot);
            Assert.DoesNotContain(ownerRoot, result.Result);
            Assert.Equal(0, config.ManagedPublishingIntegrationId);
            Assert.Equal(string.Empty, config.ManagedApprovedOutputRoots);
        }

        [Fact]
        public void Setup_ReparseTraversal_IsRejectedWithoutChangingConfiguration()
        {
            if (Path.DirectorySeparatorChar != '/')
            {
                return;
            }

            using (var outside = new TempDirectory())
            {
                var link = Path.Combine(_owner.Path, "linked-owner");
                Directory.CreateSymbolicLink(link, outside.Path);
                var config = new PluginConfiguration();

                var result = new ManagedSetupService(link).Put(config, 3, () => { });

                Assert.False(result.Ready);
                Assert.Contains("safe", result.Result, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, config.ManagedPublishingIntegrationId);
                Assert.Equal(string.Empty, config.ManagedApprovedOutputRoots);
            }
        }

        [Fact]
        public void Setup_SaveFailure_RollsBackReadinessBindingAndRoot()
        {
            var originalRoot = Path.Combine(_owner.Path, "old-root");
            var config = new PluginConfiguration
            {
                ManagedPublishingIntegrationId = 8,
                ManagedApprovedOutputRoots = originalRoot,
                ManagedMappingsJson = "existing mappings"
            };

            var result = new ManagedSetupService(_owner.Path).Put(
                config,
                8,
                () => throw new IOException("secret /path/to/config"));

            Assert.False(result.Ready);
            Assert.Equal(0, result.IntegrationId);
            Assert.Null(result.ConfirmedRoot);
            Assert.DoesNotContain("secret", result.Result);
            Assert.DoesNotContain("/path", result.Result);
            Assert.Equal(8, config.ManagedPublishingIntegrationId);
            Assert.Equal(originalRoot, config.ManagedApprovedOutputRoots);
            Assert.Equal("existing mappings", config.ManagedMappingsJson);
            Assert.False(config.ManagedSetupReady);
        }

        public void Dispose()
        {
            _owner.Dispose();
        }
    }
}
