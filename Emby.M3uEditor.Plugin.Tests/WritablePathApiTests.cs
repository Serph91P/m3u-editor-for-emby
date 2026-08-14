using System.IO;
using System.Reflection;
using Emby.M3uEditor.Plugin.Api;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class WritablePathApiTests
    {
        [Fact]
        public void IsWritableDirectory_PreexistingProbeSymlink_PreservesTarget()
        {
            using (var directory = new Fakes.TempDirectory())
            using (var outside = new Fakes.TempDirectory())
            {
                var targetPath = Path.Combine(outside.Path, "target.bin");
                var expected = new byte[] { 0, 1, 2, 255 };
                File.WriteAllBytes(targetPath, expected);
                var linkPath = Path.Combine(directory.Path, ".xtream_write_test");
                File.CreateSymbolicLink(linkPath, targetPath);

                var probe = typeof(M3uEditorApi).GetMethod(
                    "IsWritableDirectory",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var writable = (bool)probe.Invoke(null, new object[] { directory.Path });

                Assert.Equal(expected, File.ReadAllBytes(targetPath));
                Assert.True(File.Exists(linkPath));
                Assert.Equal(new[] { linkPath }, Directory.GetFiles(directory.Path));
                Assert.True(writable);
            }
        }
    }
}
