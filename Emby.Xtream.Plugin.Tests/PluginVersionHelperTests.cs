using System.Reflection;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class PluginVersionHelperTests
    {
        [Fact]
        public void CurrentVersion_IsNotNullOrEmpty()
        {
            var v = PluginVersionHelper.CurrentVersion;
            Assert.False(string.IsNullOrWhiteSpace(v));
        }

        [Fact]
        public void CurrentVersion_PrefersInformationalVersion_OverFourPartAssemblyVersion()
        {
            // Sanity: when an InformationalVersion attribute is present on the
            // plugin assembly, the helper must surface that exact string (modulo
            // any "+gitsha" build metadata).  We do not assert a specific value
            // because the test build is not version-stamped, but we do assert
            // shape: the helper must never return the bare 4-int form
            // ("X.Y.Z.0") if a SemVer informational tag exists.
            var asm = typeof(Plugin).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var helper = PluginVersionHelper.CurrentVersion;

            if (!string.IsNullOrWhiteSpace(info))
            {
                var expected = info;
                var plus = expected.IndexOf('+');
                if (plus >= 0) expected = expected.Substring(0, plus);
                Assert.Equal(expected, helper);
            }
            else
            {
                Assert.Equal(asm.GetName().Version?.ToString() ?? "0.0.0", helper);
            }
        }
        [Fact]
        public void HasInformationalVersion_ReflectsPresenceOfSemverInfo()
        {
            // Mirrors the resolver logic: HasInformationalVersion is true iff
            // the helper actually used a SemVer InformationalVersion that
            // carries pre-release info ("X.Y.Z-suffix"). A bare 4-part version
            // (whether from AssemblyName.Version or a plain numeric
            // InformationalVersion) must read as unreliable (false).
            var asm = typeof(Plugin).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var resolved = PluginVersionHelper.CurrentVersion;

            var expected = false;
            if (!string.IsNullOrWhiteSpace(info) && resolved.IndexOf('-') >= 0)
            {
                expected = true;
            }

            Assert.Equal(expected, PluginVersionHelper.HasInformationalVersion);
        }
    }
}
