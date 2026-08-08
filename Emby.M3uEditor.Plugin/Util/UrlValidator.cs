using System;

namespace Emby.M3uEditor.Plugin.Util
{
    /// <summary>
    /// Centralized validation/sanitization for image URLs that flow into Emby
    /// (channel logos, programme posters) and into the locally generated XMLTV file.
    /// Single source of truth so program and channel pipelines cannot diverge.
    /// </summary>
    public static class UrlValidator
    {
        /// <summary>
        /// Returns the trimmed URL when it is an absolute http/https URL,
        /// otherwise null. Null/empty/whitespace input returns null.
        /// </summary>
        public static string SanitizeHttpUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var trimmed = url.Trim();
            Uri uri;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
            {
                return null;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            return trimmed;
        }

        /// <summary>
        /// Convenience predicate. Prefer SanitizeHttpUrl when the validated value is needed.
        /// </summary>
        public static bool IsValidHttpUrl(string url)
        {
            return SanitizeHttpUrl(url) != null;
        }
    }
}
