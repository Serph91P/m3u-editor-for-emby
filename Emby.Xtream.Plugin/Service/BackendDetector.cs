using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    internal static class BackendTypes
    {
        public const string Unknown = "unknown";
        public const string Xtream = "xtream";
        public const string Dispatcharr = "dispatcharr";
        public const string M3uEditor = "m3u-editor";

        public static string ToDisplayName(string backendType)
        {
            if (string.Equals(backendType, M3uEditor, StringComparison.OrdinalIgnoreCase))
            {
                return "m3u-editor";
            }

            if (string.Equals(backendType, Dispatcharr, StringComparison.OrdinalIgnoreCase))
            {
                return "Dispatcharr";
            }

            if (string.Equals(backendType, Xtream, StringComparison.OrdinalIgnoreCase))
            {
                return "Xtream-compatible server";
            }

            return "Unknown";
        }
    }

    internal sealed class M3uEditorPublishingCapability
    {
        public int ApiVersion { get; set; }
        public string CatalogAction { get; set; }
        public string SyncResultAction { get; set; }
        public string SnapshotMode { get; set; }
        public IReadOnlyList<string> Features { get; set; }
    }

    internal static class BackendDetector
    {
        private static readonly string[] RequiredPublishingFeatures =
        {
            "library_mappings",
            "variants",
            "provider_failover",
            "local_nfo",
            "revision_metadata"
        };

        public static string DetectFromBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return BackendTypes.Unknown;
            }

            Uri uri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri))
            {
                return BackendTypes.Unknown;
            }

            var probe = ((uri.Host ?? string.Empty) + " " + (uri.AbsolutePath ?? string.Empty)).ToLowerInvariant();

            if (probe.Contains("m3u-editor") || probe.Contains("m3ueditor"))
            {
                return BackendTypes.M3uEditor;
            }

            if (probe.Contains("dispatcharr"))
            {
                return BackendTypes.Dispatcharr;
            }

            return BackendTypes.Unknown;
        }

        public static string DetectFromXtreamResponse(string baseUrl, JsonElement root)
        {
            var hinted = DetectFromBaseUrl(baseUrl);

            if (root.ValueKind == JsonValueKind.Object)
            {
                JsonElement m3uEditor;
                if (root.TryGetProperty("m3u_editor", out m3uEditor))
                {
                    return BackendTypes.M3uEditor;
                }

                JsonElement serverInfo;
                if (root.TryGetProperty("server_info", out serverInfo) && serverInfo.ValueKind == JsonValueKind.Object)
                {
                    var serverSoftware = TryGetString(serverInfo, "server_software");
                    if (ContainsM3uEditorMarker(serverSoftware))
                    {
                        return BackendTypes.M3uEditor;
                    }

                    var serverUrl = TryGetString(serverInfo, "url");
                    if (ContainsM3uEditorMarker(serverUrl))
                    {
                        return BackendTypes.M3uEditor;
                    }
                }

                JsonElement userInfo;
                if (root.TryGetProperty("user_info", out userInfo) && userInfo.ValueKind == JsonValueKind.Object)
                {
                    if (string.Equals(hinted, BackendTypes.Dispatcharr, StringComparison.OrdinalIgnoreCase))
                    {
                        return BackendTypes.Dispatcharr;
                    }

                    return BackendTypes.Xtream;
                }
            }

            return hinted;
        }

        public static bool TryGetPublishingCapability(
            JsonElement root,
            out M3uEditorPublishingCapability capability)
        {
            capability = null;
            JsonElement m3uEditor;
            JsonElement publishing;
            JsonElement apiVersion;
            JsonElement actions;
            JsonElement features;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("m3u_editor", out m3uEditor) ||
                m3uEditor.ValueKind != JsonValueKind.Object ||
                !m3uEditor.TryGetProperty("library_publishing", out publishing) ||
                publishing.ValueKind != JsonValueKind.Object ||
                !publishing.TryGetProperty("api_version", out apiVersion) ||
                apiVersion.ValueKind != JsonValueKind.Number ||
                !apiVersion.TryGetInt32(out var version) ||
                version != 1 ||
                !publishing.TryGetProperty("actions", out actions) ||
                actions.ValueKind != JsonValueKind.Object ||
                !publishing.TryGetProperty("features", out features) ||
                features.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var catalogAction = TryGetString(actions, "catalog");
            var syncResultAction = TryGetString(actions, "sync_result");
            var snapshotMode = TryGetString(publishing, "snapshot_mode");
            if (!string.Equals(catalogAction, "m3u_editor_catalog", StringComparison.Ordinal) ||
                !string.Equals(syncResultAction, "m3u_editor_sync_result", StringComparison.Ordinal) ||
                !string.Equals(snapshotMode, "full", StringComparison.Ordinal))
            {
                return false;
            }

            var advertisedFeatures = new List<string>();
            foreach (var feature in features.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                advertisedFeatures.Add(feature.GetString());
            }

            if (RequiredPublishingFeatures.Any(required => !advertisedFeatures.Contains(required)))
            {
                return false;
            }

            capability = new M3uEditorPublishingCapability
            {
                ApiVersion = version,
                CatalogAction = catalogAction,
                SyncResultAction = syncResultAction,
                SnapshotMode = snapshotMode,
                Features = advertisedFeatures
            };
            return true;
        }

        public static async Task<string> DetectDispatcharrProbeAsync(
            HttpClient httpClient,
            string baseUrl,
            CancellationToken cancellationToken)
        {
            if (httpClient == null || string.IsNullOrWhiteSpace(baseUrl))
            {
                return BackendTypes.Unknown;
            }

            try
            {
                var probeUrl = baseUrl.TrimEnd('/') + "/api/channels/channels/?limit=1";
                using (var request = new HttpRequestMessage(HttpMethod.Get, probeUrl))
                using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!LooksLikeDispatcharrStatus(response.StatusCode))
                    {
                        return BackendTypes.Unknown;
                    }

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var contentType = response.Content.Headers.ContentType != null
                        ? response.Content.Headers.ContentType.MediaType ?? string.Empty
                        : string.Empty;

                    if (contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        body.IndexOf("detail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        body.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        body.StartsWith("[", StringComparison.Ordinal) ||
                        body.StartsWith("{", StringComparison.Ordinal))
                    {
                        return BackendTypes.Dispatcharr;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Probe errors are non-fatal: keep backend unknown when detection cannot be confirmed.
            }
            catch (TaskCanceledException)
            {
                // Probe errors are non-fatal: keep backend unknown when detection cannot be confirmed.
            }
            catch (OperationCanceledException)
            {
                // Probe errors are non-fatal: keep backend unknown when detection cannot be confirmed.
            }

            return BackendTypes.Unknown;
        }

        private static bool LooksLikeDispatcharrStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.OK ||
                   statusCode == HttpStatusCode.Unauthorized ||
                   statusCode == HttpStatusCode.Forbidden ||
                   statusCode == HttpStatusCode.MethodNotAllowed ||
                   statusCode == HttpStatusCode.BadRequest;
        }

        private static string TryGetString(JsonElement obj, string propertyName)
        {
            JsonElement value;
            if (!obj.TryGetProperty(propertyName, out value))
            {
                return string.Empty;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                return value.ToString();
            }

            return string.Empty;
        }

        private static bool ContainsM3uEditorMarker(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf("m3u-editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("m3u editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("m3u proxy editor", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
