using System;
using System.Reflection;

namespace Emby.M3uEditor.Plugin.Service
{
    /// <summary>
    /// Returns the plugin's display version, preferring AssemblyInformationalVersion
    /// (which carries SemVer pre-release tags like "-beta.1") over the 4-part
    /// AssemblyVersion that strips them. Falls back to "0.0.0" when nothing is set.
    /// </summary>
    internal static class PluginVersionHelper
    {
        private static readonly string _cached;
        private static readonly bool _hasInfo;

        static PluginVersionHelper()
        {
            string resolved;
            bool hasInfo;
            ResolveInternal(out resolved, out hasInfo);
            _cached = resolved;
            _hasInfo = hasInfo;
        }

        public static string CurrentVersion { get { return _cached; } }

        /// <summary>
        /// True when the plugin assembly carried a SemVer
        /// AssemblyInformationalVersion (e.g. "1.2.1-beta.1") and we used it.
        /// False when we had to fall back to AssemblyName.Version (4-part form
        /// like "1.2.1.0"), which strips pre-release suffixes and therefore
        /// cannot be trusted to tell apart "1.2.1" stable from "1.2.1-beta.X".
        /// </summary>
        public static bool HasInformationalVersion { get { return _hasInfo; } }

        private static void ResolveInternal(out string version, out bool hasInfo)
        {
            var asm = typeof(Plugin).Assembly;

            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                // strip git metadata appended by the SDK (e.g. "1.2.1-beta.1+abcdef")
                var v = info.InformationalVersion;
                var plus = v.IndexOf('+');
                version = plus >= 0 ? v.Substring(0, plus) : v;

                // Only treat as reliable if the string actually carries SemVer
                // information beyond the 4-part numeric core that
                // AssemblyName.Version would also produce. Some SDK builds set
                // InformationalVersion to the bare numeric "1.2.1.0", which
                // does not help us distinguish a beta from stable.
                hasInfo = version.IndexOf('-') >= 0;
                return;
            }

            var name = asm.GetName().Version;
            version = name != null ? name.ToString() : "0.0.0";
            hasInfo = false;
        }
    }
}
