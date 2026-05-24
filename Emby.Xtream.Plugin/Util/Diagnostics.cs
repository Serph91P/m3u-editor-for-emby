namespace Emby.Xtream.Plugin.Util
{
    internal static class Diagnostics
    {
        public static bool IsEnabled
        {
            get
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return false;
                }

#pragma warning disable 0618
                var legacyLiveTvDiagnostics = config.EnableLiveTvDiagnostics;
#pragma warning restore 0618

                return config.EnableDiagnosticsLogging || legacyLiveTvDiagnostics;
            }
        }
    }
}
