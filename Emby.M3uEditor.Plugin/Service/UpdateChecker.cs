using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Emby.M3uEditor.Plugin.Service
{
    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public bool UpdateInstalled { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public string PublishedAt { get; set; }
        public string DownloadUrl { get; set; }
        public string Error { get; set; }
        public bool IsPreRelease { get; set; }
    }

    public static class UpdateChecker
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/Serph91P/m3u-editor-for-emby/releases/latest";
        private const string GitHubAllReleasesUrl = "https://api.github.com/repos/Serph91P/m3u-editor-for-emby/releases";
        private const string DllAssetName = "Emby.M3uEditor.Plugin.dll";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        private static UpdateCheckResult _cachedResult;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly object _cacheLock = new object();
        private static bool _updateInstalled;
        private static bool? _cachedForBetaChannel;

        public static bool UpdateInstalled
        {
            get { return _updateInstalled; }
            set { _updateInstalled = value; }
        }

        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedResult = null;
                _cacheTime = DateTime.MinValue;
                _cachedForBetaChannel = null;
            }
        }

        public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool? betaOverride = null)
        {
            // betaOverride (from query string) takes precedence; falls back to saved config.
            // This avoids relying on Emby's in-memory config cache being up-to-date immediately
            // after updatePluginConfiguration returns.
            var useBeta = betaOverride ?? Plugin.Instance?.Configuration?.UseBetaChannel ?? false;

            lock (_cacheLock)
            {
                // Invalidate cache if the channel preference changed since last fetch
                if (_cachedResult != null && _cachedForBetaChannel.HasValue && _cachedForBetaChannel.Value != useBeta)
                {
                    _cachedResult = null;
                    _cacheTime = DateTime.MinValue;
                    _cachedForBetaChannel = null;
                }

                if (_cachedResult != null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
                {
                    return _cachedResult;
                }
            }

            var currentVersion = PluginVersionHelper.CurrentVersion;

            UpdateCheckResult result;
            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("M3u-Editor-for-Emby/1.0");
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    string releaseJson;
                    if (useBeta)
                    {
                        var allJson = await httpClient.GetStringAsync(GitHubAllReleasesUrl).ConfigureAwait(false);
                        releaseJson = SelectHighestRelease(allJson) ?? "{}";
                    }
                    else
                    {
                        releaseJson = await httpClient.GetStringAsync(GitHubApiUrl).ConfigureAwait(false);
                    }

                    var tagName = ExtractJsonString(releaseJson, "tag_name");
                    var htmlUrl = ExtractJsonString(releaseJson, "html_url");
                    var body = ExtractJsonString(releaseJson, "body");
                    var publishedAt = ExtractJsonString(releaseJson, "published_at");

                    result = CompareVersions(currentVersion, tagName, htmlUrl, body, publishedAt,
                        useBeta, !PluginVersionHelper.HasInformationalVersion);
                    result.DownloadUrl = ExtractDllDownloadUrl(releaseJson, DllAssetName);
                    result.UpdateInstalled = _updateInstalled;
                    result.IsPreRelease = ExtractJsonBool(releaseJson, "prerelease");

                    // Suppress update banner if this version was already installed
                    if (result.UpdateAvailable && !_updateInstalled)
                    {
                        var config = Plugin.Instance?.Configuration;
                        if (config != null &&
                            !string.IsNullOrEmpty(config.LastInstalledVersion) &&
                            string.Equals(config.LastInstalledVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            result.UpdateAvailable = false;
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                result = new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    Error = "Failed to check for updates: " + ex.Message,
                };
            }

            lock (_cacheLock)
            {
                _cachedResult = result;
                _cacheTime = DateTime.UtcNow;
                _cachedForBetaChannel = useBeta;
            }

            return result;
        }

        public static UpdateCheckResult CompareVersions(string currentVersion, string tagName, string releaseUrl, string body, string publishedAt)
        {
            return CompareVersions(currentVersion, tagName, releaseUrl, body, publishedAt, false, false);
        }

        /// <summary>
        /// Overload that takes additional context: whether the user is on the
        /// beta channel, and whether the reported current version came from a
        /// reliable SemVer source (true = unreliable, e.g. a 4-part
        /// AssemblyName.Version that strips "-beta.X" suffixes).
        ///
        /// When <paramref name="currentVersionUnreliable"/> is true and we are
        /// on the beta channel and the latest tag is a pre-release of the same
        /// numeric core (e.g. installed reports "1.2.1.0", latest is
        /// "1.2.1-beta.4"), we offer the update. The installed build could be
        /// any beta of that core and we have no way to tell. Defaulting to
        /// "no update" would leave the user stuck on whatever beta they have.
        /// Stable channel keeps strict SemVer semantics.
        /// </summary>
        public static UpdateCheckResult CompareVersions(string currentVersion, string tagName, string releaseUrl, string body, string publishedAt,
            bool useBeta, bool currentVersionUnreliable)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ReleaseUrl = releaseUrl ?? "",
                ReleaseNotes = body ?? "",
                PublishedAt = publishedAt ?? "",
            };

            if (string.IsNullOrEmpty(tagName))
            {
                result.Error = "No tag found in release data.";
                return result;
            }

            var versionStr = tagName.TrimStart('v', 'V');
            result.LatestVersion = versionStr;

            SemVer current;
            SemVer latest;

            if (!SemVer.TryParse(currentVersion, out current))
            {
                result.Error = "Could not parse current version: " + currentVersion;
                return result;
            }

            if (!SemVer.TryParse(versionStr, out latest))
            {
                result.Error = "Could not parse release tag: " + tagName;
                return result;
            }

            var cmp = SemVer.Compare(latest, current);
            result.UpdateAvailable = cmp > 0;

            // Unreliable-current-version override: on the beta channel, when the
            // installed version came from a 4-part AssemblyName.Version (no
            // "-beta.X" suffix preserved), strict SemVer says stable "1.2.1"
            // outranks pre-release "1.2.1-beta.4". But "1.2.1.0" reported by
            // an old beta build is indistinguishable from real stable "1.2.1".
            // If the latest beta has the same numeric core, offer it so the
            // user can recover from a stuck beta install.
            if (!result.UpdateAvailable
                && useBeta
                && currentVersionUnreliable
                && latest.PreRelease != null
                && current.PreRelease == null
                && latest.Major == current.Major
                && latest.Minor == current.Minor
                && latest.Patch == current.Patch)
            {
                result.UpdateAvailable = true;
            }

            return result;
        }

        /// <summary>
        /// Lightweight SemVer parser/comparer that handles pre-release suffixes
        /// like "1.2.0-beta.1" or "2.0.0-rc.3". Build metadata (after '+') is ignored.
        /// Comparison follows SemVer 2.0 precedence rules: numeric core compared
        /// numerically, then a version WITHOUT pre-release outranks one WITH; when
        /// both have pre-release, identifiers are compared dot-by-dot (numeric ids
        /// numerically, alphanumeric lexically, numeric &lt; alphanumeric).
        /// </summary>
        internal class SemVer
        {
            public int Major;
            public int Minor;
            public int Patch;
            public int Revision; // optional 4th segment, defaults to 0
            public string[] PreRelease; // null = stable release

            public static bool TryParse(string version, out SemVer result)
            {
                result = null;
                if (string.IsNullOrEmpty(version)) return false;

                // Strip leading v / V
                if (version[0] == 'v' || version[0] == 'V')
                    version = version.Substring(1);

                // Strip build metadata (+...)
                var plusIdx = version.IndexOf('+');
                if (plusIdx >= 0)
                    version = version.Substring(0, plusIdx);

                // Split off pre-release suffix
                string core;
                string[] pre = null;
                var dashIdx = version.IndexOf('-');
                if (dashIdx >= 0)
                {
                    core = version.Substring(0, dashIdx);
                    var preStr = version.Substring(dashIdx + 1);
                    if (string.IsNullOrEmpty(preStr)) return false;
                    pre = preStr.Split('.');
                    if (pre.Any(string.IsNullOrEmpty)) return false;
                }
                else
                {
                    core = version;
                }

                var parts = core.Split('.');
                if (parts.Length < 1 || parts.Length > 4) return false;

                int major = 0, minor = 0, patch = 0, revision = 0;
                if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out major))
                    return false;
                if (parts.Length > 1 && !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out minor))
                    return false;
                if (parts.Length > 2 && !int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out patch))
                    return false;
                if (parts.Length > 3 && !int.TryParse(parts[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out revision))
                    return false;

                result = new SemVer
                {
                    Major = major,
                    Minor = minor,
                    Patch = patch,
                    Revision = revision,
                    PreRelease = pre,
                };
                return true;
            }

            public static int Compare(SemVer a, SemVer b)
            {
                if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
                if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
                if (a.Patch != b.Patch) return a.Patch.CompareTo(b.Patch);
                if (a.Revision != b.Revision) return a.Revision.CompareTo(b.Revision);

                // Equal numeric core: stable > pre-release
                var aPre = a.PreRelease;
                var bPre = b.PreRelease;
                if (aPre == null && bPre == null) return 0;
                if (aPre == null) return 1;  // a is stable, b is pre-release => a > b
                if (bPre == null) return -1; // b is stable, a is pre-release => a < b

                var len = Math.Min(aPre.Length, bPre.Length);
                for (var i = 0; i < len; i++)
                {
                    var cmp = CompareIdentifier(aPre[i], bPre[i]);
                    if (cmp != 0) return cmp;
                }
                // Longer pre-release identifier list wins when prefix is equal
                return aPre.Length.CompareTo(bPre.Length);
            }

            private static int CompareIdentifier(string a, string b)
            {
                int ai, bi;
                var aIsNum = int.TryParse(a, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ai);
                var bIsNum = int.TryParse(b, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out bi);

                if (aIsNum && bIsNum) return ai.CompareTo(bi);
                if (aIsNum) return -1; // numeric identifiers always have lower precedence than alphanumeric
                if (bIsNum) return 1;
                return string.CompareOrdinal(a, b);
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            using (var document = System.Text.Json.JsonDocument.Parse(json))
                return GetString(document.RootElement, key);
        }

        private static string GetString(System.Text.Json.JsonElement element, string key)
        {
            System.Text.Json.JsonElement value;
            return element.ValueKind == System.Text.Json.JsonValueKind.Object &&
                element.TryGetProperty(key, out value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString() : null;
        }

        /// <summary>
        /// Scans the GitHub release JSON assets array for an asset matching the given name
        /// and returns its browser_download_url. Case-insensitive matching.
        /// </summary>
        public static string ExtractDllDownloadUrl(string json, string assetName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(assetName))
                return null;

            using (var document = System.Text.Json.JsonDocument.Parse(json))
            {
                System.Text.Json.JsonElement assets;
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("assets", out assets) ||
                    assets.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                foreach (var asset in assets.EnumerateArray())
                    if (string.Equals(GetString(asset, "name"), assetName, StringComparison.OrdinalIgnoreCase))
                        return GetString(asset, "browser_download_url");
            }
            return null;
        }

        /// <summary>
        /// Scans a GitHub /releases array and returns the JSON object with the
        /// highest SemVer-precedence tag_name. Skips drafts. Used by the beta
        /// channel so the newest release wins regardless of array ordering, and
        /// so a stable hotfix released after a beta still outranks the beta when
        /// SemVer says it should.
        /// </summary>
        public static string SelectHighestRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            using (var document = System.Text.Json.JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    throw new System.Text.Json.JsonException("Expected a releases array.");
                string bestJson = null;
                SemVer bestVer = null;
                foreach (var release in document.RootElement.EnumerateArray())
                {
                    if (GetBool(release, "draft")) continue;
                    SemVer version;
                    if (!SemVer.TryParse(GetString(release, "tag_name"), out version)) continue;
                    if (bestVer == null || SemVer.Compare(version, bestVer) > 0)
                    {
                        bestVer = version;
                        bestJson = release.GetRawText();
                    }
                }
                return bestJson;
            }
        }

        /// <summary>
        /// Extracts the first JSON object from a JSON array string (e.g. from /releases endpoint).
        /// Returns "{}" if the array is empty or the input is null/empty.
        /// </summary>
        public static string ExtractFirstRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return "{}";

            using (var document = System.Text.Json.JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    throw new System.Text.Json.JsonException("Expected a releases array.");
                foreach (var release in document.RootElement.EnumerateArray())
                    return release.GetRawText();
                return "{}";
            }
        }

        /// <summary>
        /// Extracts a boolean value (true/false) for the given key from a JSON string.
        /// Returns false if the key is missing or the value is not a boolean literal.
        /// </summary>
        public static bool ExtractJsonBool(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;

            using (var document = System.Text.Json.JsonDocument.Parse(json))
                return GetBool(document.RootElement, key);
        }

        private static bool GetBool(System.Text.Json.JsonElement element, string key)
        {
            System.Text.Json.JsonElement value;
            return element.ValueKind == System.Text.Json.JsonValueKind.Object &&
                element.TryGetProperty(key, out value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
    }
}
