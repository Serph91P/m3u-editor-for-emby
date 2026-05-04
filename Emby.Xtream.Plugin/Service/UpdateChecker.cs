using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
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
        private const string GitHubApiUrl = "https://api.github.com/repos/Serph91P/emby-xtream/releases/latest";
        private const string GitHubAllReleasesUrl = "https://api.github.com/repos/Serph91P/emby-xtream/releases";
        private const string DllAssetName = "Emby.Xtream.Plugin.dll";
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

            var currentVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0";

            UpdateCheckResult result;
            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Emby-Xtream-Plugin/1.0");
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    string releaseJson;
                    if (useBeta)
                    {
                        var allJson = await httpClient.GetStringAsync(GitHubAllReleasesUrl).ConfigureAwait(false);
                        releaseJson = SelectHighestRelease(allJson) ?? ExtractFirstRelease(allJson);
                    }
                    else
                    {
                        releaseJson = await httpClient.GetStringAsync(GitHubApiUrl).ConfigureAwait(false);
                    }

                    var tagName = ExtractJsonString(releaseJson, "tag_name");
                    var htmlUrl = ExtractJsonString(releaseJson, "html_url");
                    var body = ExtractJsonString(releaseJson, "body");
                    var publishedAt = ExtractJsonString(releaseJson, "published_at");

                    result = CompareVersions(currentVersion, tagName, htmlUrl, body, publishedAt);
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
            catch (Exception ex)
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

            result.UpdateAvailable = SemVer.Compare(latest, current) > 0;
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
                    foreach (var id in pre)
                    {
                        if (string.IsNullOrEmpty(id)) return false;
                    }
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
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            return ExtractJsonStringAt(json, idx);
        }

        /// <summary>
        /// Extracts a JSON string value starting from the given position (after the key).
        /// Skips whitespace/colon, reads quoted string with escape handling.
        /// </summary>
        internal static string ExtractJsonStringAt(string json, int idx)
        {
            // Skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':' || json[idx] == '\t' || json[idx] == '\n' || json[idx] == '\r'))
                idx++;

            if (idx >= json.Length) return null;

            if (json[idx] == 'n') return null; // null value

            if (json[idx] != '"') return null;
            idx++; // skip opening quote

            var sb = new System.Text.StringBuilder();
            while (idx < json.Length)
            {
                var c = json[idx];
                if (c == '\\' && idx + 1 < json.Length)
                {
                    var next = json[idx + 1];
                    if (next == '"') { sb.Append('"'); idx += 2; continue; }
                    if (next == '\\') { sb.Append('\\'); idx += 2; continue; }
                    if (next == 'n') { sb.Append('\n'); idx += 2; continue; }
                    if (next == 'r') { sb.Append('\r'); idx += 2; continue; }
                    if (next == 't') { sb.Append('\t'); idx += 2; continue; }
                    sb.Append(c);
                    idx++;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                idx++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Scans the GitHub release JSON assets array for an asset matching the given name
        /// and returns its browser_download_url. Case-insensitive matching.
        /// </summary>
        public static string ExtractDllDownloadUrl(string json, string assetName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(assetName))
                return null;

            // Find the "assets" array
            var assetsKey = "\"assets\"";
            var assetsIdx = json.IndexOf(assetsKey, StringComparison.Ordinal);
            if (assetsIdx < 0) return null;

            // Find the opening bracket of the assets array
            var bracketIdx = json.IndexOf('[', assetsIdx + assetsKey.Length);
            if (bracketIdx < 0) return null;

            // Find the closing bracket of the assets array
            var closingBracket = FindMatchingBracket(json, bracketIdx);
            if (closingBracket < 0) return null;

            var assetsSection = json.Substring(bracketIdx, closingBracket - bracketIdx + 1);

            // Scan through each "name" in the assets section
            var searchFrom = 0;
            while (searchFrom < assetsSection.Length)
            {
                var nameKey = "\"name\"";
                var nameIdx = assetsSection.IndexOf(nameKey, searchFrom, StringComparison.Ordinal);
                if (nameIdx < 0) break;

                var valueStart = nameIdx + nameKey.Length;
                var name = ExtractJsonStringAt(assetsSection, valueStart);

                if (name != null && string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    // Found the matching asset - now find browser_download_url near this position
                    // Look backwards for the start of this object (the '{' before this "name")
                    var objStart = assetsSection.LastIndexOf('{', nameIdx);
                    // Look forwards for the end of this object
                    var objEnd = FindMatchingBrace(assetsSection, objStart);
                    if (objStart >= 0 && objEnd > objStart)
                    {
                        var assetObj = assetsSection.Substring(objStart, objEnd - objStart + 1);
                        var urlKey = "\"browser_download_url\"";
                        var urlIdx = assetObj.IndexOf(urlKey, StringComparison.Ordinal);
                        if (urlIdx >= 0)
                        {
                            return ExtractJsonStringAt(assetObj, urlIdx + urlKey.Length);
                        }
                    }
                }

                searchFrom = valueStart + 1;
            }

            return null;
        }

        private static int FindMatchingBracket(string json, int openIdx)
        {
            var depth = 0;
            for (var i = openIdx; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int FindMatchingBrace(string json, int openIdx)
        {
            if (openIdx < 0) return -1;
            var depth = 0;
            for (var i = openIdx; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
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

            // Walk top-level objects in the array. We rely on FindMatchingBrace
            // to skip over nested objects (e.g. assets/uploader) safely.
            var arrayStart = json.IndexOf('[');
            if (arrayStart < 0) return null;
            var arrayEnd = FindMatchingBracket(json, arrayStart);
            if (arrayEnd < 0) return null;

            string bestJson = null;
            SemVer bestVer = null;

            var i = arrayStart + 1;
            while (i < arrayEnd)
            {
                // Find next top-level '{'
                while (i < arrayEnd && json[i] != '{') i++;
                if (i >= arrayEnd) break;

                var objEnd = FindMatchingBrace(json, i);
                if (objEnd < 0 || objEnd > arrayEnd) break;

                var obj = json.Substring(i, objEnd - i + 1);
                i = objEnd + 1;

                // Skip drafts
                if (ExtractJsonBool(obj, "draft")) continue;

                var tag = ExtractJsonString(obj, "tag_name");
                if (string.IsNullOrEmpty(tag)) continue;

                SemVer ver;
                if (!SemVer.TryParse(tag, out ver)) continue;

                if (bestVer == null || SemVer.Compare(ver, bestVer) > 0)
                {
                    bestVer = ver;
                    bestJson = obj;
                }
            }

            return bestJson;
        }

        /// <summary>
        /// Extracts the first JSON object from a JSON array string (e.g. from /releases endpoint).
        /// Returns "{}" if the array is empty or the input is null/empty.
        /// </summary>
        public static string ExtractFirstRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return "{}";

            var openBrace = json.IndexOf('{');
            if (openBrace < 0) return "{}";

            var closeBrace = FindMatchingBrace(json, openBrace);
            if (closeBrace < 0) return "{}";

            return json.Substring(openBrace, closeBrace - openBrace + 1);
        }

        /// <summary>
        /// Extracts a boolean value (true/false) for the given key from a JSON string.
        /// Returns false if the key is missing or the value is not a boolean literal.
        /// </summary>
        public static bool ExtractJsonBool(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;

            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return false;

            idx += search.Length;

            // Skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':' || json[idx] == '\t' || json[idx] == '\n' || json[idx] == '\r'))
                idx++;

            if (idx >= json.Length) return false;

            if (idx + 4 <= json.Length && json.Substring(idx, 4) == "true") return true;
            return false;
        }
    }
}
