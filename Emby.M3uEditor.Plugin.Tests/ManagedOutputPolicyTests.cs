using System;
using System.IO;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedOutputPolicyTests : IDisposable
    {
        private readonly TempDirectory _temp = new TempDirectory();

        [Fact]
        public void IsApproved_ChildOfApprovedRoot_Allows()
        {
            var target = Path.Join(_temp.Path, "movies");

            Assert.True(ManagedOutputPolicy.IsApproved(target, _temp.Path, out var error), error);
        }

        [Fact]
        public void IsApproved_PrefixConfusion_Rejects()
        {
            var target = _temp.Path + "-outside";

            Assert.False(ManagedOutputPolicy.IsApproved(target, _temp.Path, out _));
        }

        [Fact]
        public void IsApproved_FileSystemRootApproval_Rejects()
        {
            Assert.False(ManagedOutputPolicy.IsApproved(_temp.Path, Path.GetPathRoot(_temp.Path), out _));
        }

        [Fact]
        public void IsApproved_OverlappingApprovals_Rejects()
        {
            var nested = Path.Join(_temp.Path, "nested");

            Assert.False(ManagedOutputPolicy.IsApproved(
                nested,
                _temp.Path + Environment.NewLine + nested,
                out var error));
            Assert.Contains("overlap", error);
        }

        [Fact]
        public void IsApproved_AmbiguousSeparator_Rejects()
        {
            if (Path.DirectorySeparatorChar == '/')
            {
                Assert.False(ManagedOutputPolicy.IsApproved(_temp.Path + "\\child", _temp.Path, out _));
            }
        }

        [Fact]
        public void IsApproved_SymlinkTarget_Rejects()
        {
            if (Path.DirectorySeparatorChar != '/')
            {
                return;
            }

            using (var outside = new TempDirectory())
            {
                try
                {
                    var link = Path.Join(_temp.Path, "link");
                    File.CreateSymbolicLink(link, outside.Path);

                    Assert.False(ManagedOutputPolicy.IsApproved(Path.Join(link, "movies"), _temp.Path, out _));
                }
                catch (PlatformNotSupportedException)
                {
                    return;
                }
            }
        }

        [Fact]
        public void TryJoinUnderRoot_RootedOrTraversingLaterPath_Rejects()
        {
            var rootedPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "managed-path-escape"));

            Assert.False(ManagedOutputPolicy.TryJoinUnderRoot(_temp.Path, rootedPath, out var rootedResult));
            Assert.Null(rootedResult);
            Assert.False(ManagedOutputPolicy.TryJoinUnderRoot(_temp.Path, "..", out var traversalResult));
            Assert.Null(traversalResult);
        }

        [Fact]
        public void TryJoinUnderRoot_RelativeLaterPath_ReturnsCanonicalChild()
        {
            Assert.True(ManagedOutputPolicy.TryJoinUnderRoot(_temp.Path, "managed-child", out var result));

            Assert.Equal(Path.Join(_temp.Path, "managed-child"), result);
        }

        [Fact]
        public void TryJoinUnderRoot_SymlinkedChild_Rejects()
        {
            if (Path.DirectorySeparatorChar != '/')
            {
                return;
            }

            using (var outside = new TempDirectory())
            {
                var link = Path.Join(_temp.Path, "link");
                Directory.CreateSymbolicLink(link, outside.Path);

                Assert.False(ManagedOutputPolicy.TryJoinUnderRoot(_temp.Path, "link/managed-child", out var result));
                Assert.Null(result);
            }
        }

        [Fact]
        public void IsLocallyWritableRoot_ProbesChildLifecycleWithoutLeavingArtifacts()
        {
            Assert.True(ManagedOutputPolicy.IsLocallyWritableRoot(_temp.Path));
            Assert.Empty(Directory.GetFileSystemEntries(_temp.Path));
        }

        [Fact]
        public void IsLocallyWritableRoot_ReadOnlyMountLikePermissions_FailsClosed()
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
            var originalMode = File.GetUnixFileMode(_temp.Path);
            try
            {
                File.SetUnixFileMode(_temp.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Assert.False(ManagedOutputPolicy.IsLocallyWritableRoot(_temp.Path));
            }
            finally
            {
                File.SetUnixFileMode(_temp.Path, originalMode);
            }
        }

        public void Dispose()
        {
            _temp.Dispose();
        }
    }
}
