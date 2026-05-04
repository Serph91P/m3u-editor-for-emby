using System;
using System.Reflection;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Returns the plugin's display version, preferring AssemblyInformationalVersion
    /// (which carries SemVer pre-release tags like "-beta.1") over the 4-part
    /// AssemblyVersion that strips them. Falls back to "0.0.0" when nothing is set.
    /// </summary>
    internal static class PluginVersionHelper
    {
        private static readonly string _cached = Resolve();

        public static string CurrentVersion => _cached;

        private static string Resolve()
        {
            var asm = typeof(Plugin).Assembly;

            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                // strip git metadata appended by the SDK (e.g. "1.2.1-beta.1+abcdef")
                var v = info.InformationalVersion;
                var plus = v.IndexOf('+');
                return plus >= 0 ? v.Substring(0, plus) : v;
            }

            var name = asm.GetName().Version;
            return name?.ToString() ?? "0.0.0";
        }
    }
}
