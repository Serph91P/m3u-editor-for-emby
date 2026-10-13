using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.M3uEditor.Plugin.Client.Models
{
    internal sealed class M3uEditorCacheControlRequest
    {
        public int IntegrationId { get; set; }
        public string MappingUuid { get; set; }
        public string Revision { get; set; }
        public string RequestId { get; set; }
        public string ConsumerId { get; set; }
        public List<M3uEditorCacheControlTarget> Targets { get; set; } = new List<M3uEditorCacheControlTarget>();
    }

    internal sealed class M3uEditorCacheControlTarget
    {
        public string CanonicalId { get; set; }
        public string VariantKey { get; set; }
    }

    internal sealed class M3uEditorCacheControlResponse
    {
        [JsonPropertyName("request_id")]
        public string RequestId { get; set; }

        [JsonPropertyName("duplicate")]
        public bool Duplicate { get; set; }

        [JsonPropertyName("results")]
        public List<M3uEditorCacheControlTargetResult> Results { get; set; } = new List<M3uEditorCacheControlTargetResult>();
    }

    internal sealed class M3uEditorCacheControlTargetResult
    {
        [JsonPropertyName("canonical_id")]
        public string CanonicalId { get; set; }

        [JsonPropertyName("variant_key")]
        public string VariantKey { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }
    }
}
