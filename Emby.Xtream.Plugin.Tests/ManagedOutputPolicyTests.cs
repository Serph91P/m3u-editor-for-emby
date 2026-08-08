using System;
using System.IO;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class ManagedOutputPolicyTests : IDisposable
    {
        private readonly TempDirectory _temp = new TempDirectory();

        [Fact]
        public void IsApproved_ChildOfApprovedRoot_Allows()
        {
            var target = Path.Combine(_temp.Path, "movies");

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
            var nested = Path.Combine(_temp.Path, "nested");

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

            var outside = new TempDirectory();
            try
            {
                var link = Path.Combine(_temp.Path, "link");
                File.CreateSymbolicLink(link, outside.Path);

                Assert.False(ManagedOutputPolicy.IsApproved(Path.Combine(link, "movies"), _temp.Path, out _));
            }
            catch (PlatformNotSupportedException)
            {
            }
            finally
            {
                outside.Dispose();
            }
        }

        public void Dispose()
        {
            _temp.Dispose();
        }
    }
}
