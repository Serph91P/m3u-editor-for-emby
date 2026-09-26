using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Api;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedLibraryProvisioningTests : IDisposable
    {
        private readonly TempDirectory _owner = new TempDirectory();
        private readonly PluginConfiguration _config = new PluginConfiguration();

        public ManagedLibraryProvisioningTests()
        {
            var setup = new ManagedSetupService(_owner.Path).Put(_config, 71, () => { });
            Assert.True(setup.Ready, setup.Result);
        }

        [Theory]
        [InlineData(typeof(PrepareManagedLibrary), "/M3uEditor/Managed/Libraries/V1/Prepare", "PUT")]
        [InlineData(typeof(CommitManagedLibrary), "/M3uEditor/Managed/Libraries/V1/Commit", "POST")]
        [InlineData(typeof(AbortManagedLibrary), "/M3uEditor/Managed/Libraries/V1/Abort", "POST")]
        public void LibraryRoutes_AreVersionedAndAdministratorAuthenticated(Type requestType, string path, string verb)
        {
            var route = Assert.Single(requestType.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>());
            var auth = Assert.Single(requestType.GetCustomAttributes(typeof(AuthenticatedAttribute), true).Cast<AuthenticatedAttribute>());

            Assert.Equal(path, route.Path);
            Assert.Contains(verb, route.Verbs);
            Assert.Equal("Admin", auth.Roles);
        }

        [Fact]
        public void Prepare_ValidSemanticRequest_CreatesOwnedDirectChildAndPersists()
        {
            var service = new ManagedLibraryProvisioningService(_owner.Path);
            var operation = Guid.NewGuid().ToString("D");
            var saves = 0;

            var result = service.Prepare(_config, 71, operation, "My Movies", "movies", () => saves++);

            Assert.True(result.Success, result.Message);
            Assert.Equal("prepared", result.State);
            Assert.Equal(ManagedLibraryProvisioningService.ApiVersion, result.CapabilityVersion);
            Assert.Equal(71, result.IntegrationId);
            Assert.Equal(operation, result.OperationId);
            Assert.True(Directory.Exists(result.PreparedPath));
            Assert.Equal(Path.GetDirectoryName(result.PreparedPath), _config.ManagedApprovedOutputRoots);
            Assert.False(string.IsNullOrWhiteSpace(_config.ManagedDirectoryOwnershipJson));
            Assert.Equal(1, saves);
        }

        [Fact]
        public void Prepare_RepeatedOperation_IsIdempotentAcrossServiceRestart()
        {
            var operation = Guid.NewGuid().ToString("D");
            var first = new ManagedLibraryProvisioningService(_owner.Path)
                .Prepare(_config, 71, operation, "My Movies", "movies", () => { });
            var saves = 0;

            var repeated = new ManagedLibraryProvisioningService(_owner.Path)
                .Prepare(_config, 71, operation, "My Movies", "movies", () => saves++);

            Assert.True(first.Success, first.Message);
            Assert.True(repeated.Success, repeated.Message);
            Assert.True(repeated.Duplicate);
            Assert.Equal(first.PreparedPath, repeated.PreparedPath);
            Assert.Equal(0, saves);
        }

        [Fact]
        public void Prepare_ConflictingOperationOrBinding_FailsWithoutWriting()
        {
            var operation = Guid.NewGuid().ToString("D");
            var service = new ManagedLibraryProvisioningService(_owner.Path);
            Assert.True(service.Prepare(_config, 71, operation, "My Movies", "movies", () => { }).Success);
            var before = Directory.GetDirectories(_config.ManagedApprovedOutputRoots).OrderBy(value => value).ToArray();

            var conflict = service.Prepare(_config, 71, operation, "Other", "tvshows", () => { });
            var wrongBinding = service.Prepare(_config, 72, Guid.NewGuid().ToString("D"), "Other", "tvshows", () => { });

            Assert.False(conflict.Success);
            Assert.False(wrongBinding.Success);
            Assert.Equal(before, Directory.GetDirectories(_config.ManagedApprovedOutputRoots).OrderBy(value => value).ToArray());
        }

        [Theory]
        [InlineData("../escape", "movies")]
        [InlineData("Movies", "../../tv")]
        [InlineData("", "movies")]
        [InlineData("Movies", "music")]
        public void Prepare_InvalidSemanticRequest_FailsWithoutWriting(string name, string collectionType)
        {
            var result = new ManagedLibraryProvisioningService(_owner.Path).Prepare(
                _config,
                71,
                Guid.NewGuid().ToString("D"),
                name,
                collectionType,
                () => { });

            Assert.False(result.Success);
            Assert.Empty(Directory.GetDirectories(_config.ManagedApprovedOutputRoots));
            Assert.DoesNotContain(_owner.Path, result.Message ?? string.Empty);
        }

        [Fact]
        public void Prepare_ExistingUnownedTarget_FailsClosed()
        {
            var service = new ManagedLibraryProvisioningService(_owner.Path);
            var target = service.GetExpectedLibraryPath(_config, "My Movies", "movies");
            Directory.CreateDirectory(target);

            var result = service.Prepare(
                _config,
                71,
                Guid.NewGuid().ToString("D"),
                "My Movies",
                "movies",
                () => { });

            Assert.False(result.Success);
            Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(target));
        }

        [Fact]
        public void Prepare_SymlinkLeaf_FailsClosed()
        {
            if (Path.DirectorySeparatorChar != '/') return;
            using (var outside = new TempDirectory())
            {
                var service = new ManagedLibraryProvisioningService(_owner.Path);
                var target = service.GetExpectedLibraryPath(_config, "My Movies", "movies");
                Directory.CreateSymbolicLink(target, outside.Path);

                var result = service.Prepare(
                    _config,
                    71,
                    Guid.NewGuid().ToString("D"),
                    "My Movies",
                    "movies",
                    () => { });

                Assert.False(result.Success);
                Assert.Empty(Directory.GetFileSystemEntries(outside.Path));
            }
        }

        [Fact]
        public async Task Prepare_ConcurrentSameOperation_HasSingleOwnerAndNoCollision()
        {
            var operation = Guid.NewGuid().ToString("D");
            var saves = 0;
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                new ManagedLibraryProvisioningService(_owner.Path).Prepare(
                    _config,
                    71,
                    operation,
                    "My Movies",
                    "movies",
                    () => saves++))));

            Assert.All(results, result => Assert.True(result.Success, result.Message));
            Assert.Single(results.Where(result => result.Duplicate));
            Assert.Equal(1, saves);
            Assert.Single(Directory.GetDirectories(_config.ManagedApprovedOutputRoots));
        }

        [Fact]
        public void Prepare_SaveFailure_RemovesNewEmptyDirectoryAndRestoresState()
        {
            var original = _config.ManagedDirectoryOwnershipJson;

            var result = new ManagedLibraryProvisioningService(_owner.Path).Prepare(
                _config,
                71,
                Guid.NewGuid().ToString("D"),
                "My Movies",
                "movies",
                () => throw new IOException("secret path"));

            Assert.False(result.Success);
            Assert.Equal(original, _config.ManagedDirectoryOwnershipJson);
            Assert.Empty(Directory.GetDirectories(_config.ManagedApprovedOutputRoots));
            Assert.DoesNotContain("secret", result.Message ?? string.Empty);
        }

        [Fact]
        public void CommitAndAbort_RequireOwnedOperationAndAbortOnlyEmptyPreparedDirectory()
        {
            var service = new ManagedLibraryProvisioningService(_owner.Path);
            var committedOperation = Guid.NewGuid().ToString("D");
            var committed = service.Prepare(_config, 71, committedOperation, "Movies", "movies", () => { });
            Assert.True(service.Commit(_config, 71, committedOperation, () => { }).Success);
            Assert.False(service.Abort(_config, 71, committedOperation, () => { }).Success);
            Assert.True(Directory.Exists(committed.PreparedPath));

            var abortOperation = Guid.NewGuid().ToString("D");
            var prepared = service.Prepare(_config, 71, abortOperation, "Series", "tvshows", () => { });
            File.WriteAllText(Path.Combine(prepared.PreparedPath, "foreign.txt"), "keep");
            Assert.False(service.Abort(_config, 71, abortOperation, () => { }).Success);
            Assert.True(Directory.Exists(prepared.PreparedPath));
            File.Delete(Path.Combine(prepared.PreparedPath, "foreign.txt"));
            Assert.True(service.Abort(_config, 71, abortOperation, () => { }).Success);
            Assert.False(Directory.Exists(prepared.PreparedPath));
        }

        public void Dispose()
        {
            _owner.Dispose();
        }
    }
}
