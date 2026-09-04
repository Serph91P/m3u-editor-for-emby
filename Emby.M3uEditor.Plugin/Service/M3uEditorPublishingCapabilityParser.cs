using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class M3uEditorPublishingCapability
    {
        public int ApiVersion { get; set; }
        public string RegisterPublisherAction { get; set; }
        public string CatalogAction { get; set; }
        public string SyncResultAction { get; set; }
        public string SnapshotMode { get; set; }
        public IReadOnlyList<string> Features { get; set; }
    }

    internal static class M3uEditorPublishingCapabilityParser
    {
        private static readonly string[] RequiredPublishingFeatures =
        {
            "library_mappings",
            "variants",
            "provider_failover",
            "local_nfo",
            "revision_metadata"
        };

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

            var registerPublisherAction = TryGetString(actions, "register_publisher");
            var catalogAction = TryGetString(actions, "catalog");
            var syncResultAction = TryGetString(actions, "sync_result");
            var snapshotMode = TryGetString(publishing, "snapshot_mode");
            if (!string.Equals(registerPublisherAction, "m3u_editor_register_publisher", StringComparison.Ordinal) ||
                !string.Equals(catalogAction, "m3u_editor_catalog", StringComparison.Ordinal) ||
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
                RegisterPublisherAction = registerPublisherAction,
                CatalogAction = catalogAction,
                SyncResultAction = syncResultAction,
                SnapshotMode = snapshotMode,
                Features = advertisedFeatures
            };
            return true;
        }

        private static string TryGetString(JsonElement obj, string propertyName)
        {
            if (!obj.TryGetProperty(propertyName, out var value))
            {
                return string.Empty;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind == JsonValueKind.Number ||
                value.ValueKind == JsonValueKind.True ||
                value.ValueKind == JsonValueKind.False)
            {
                return value.ToString();
            }

            return string.Empty;
        }
    }
}
