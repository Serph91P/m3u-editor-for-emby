using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.M3uEditor.Plugin.Client.Models
{
    internal sealed class PreservedPrimaryProjection
    {
        [JsonPropertyName("api_version")] public int ApiVersion { get; set; }
        [JsonPropertyName("plugin_id")] public string PluginId { get; set; }
        [JsonPropertyName("query")] public string Query { get; set; }
        [JsonPropertyName("data")] public PreservedPrimaryPage Data { get; set; }
    }

    internal sealed class PreservedPrimaryPage
    {
        [JsonPropertyName("snapshot_id")] public string SnapshotId { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("next_cursor")] public string NextCursor { get; set; }
        [JsonPropertyName("entries")] public List<PreservedPrimaryEntry> Entries { get; set; }
    }

    internal sealed class PreservedPrimaryEntry
    {
        [JsonPropertyName("stream_id")] public int StreamId { get; set; }
        [JsonPropertyName("start_timestamp")] public long StartTimestamp { get; set; }
        [JsonPropertyName("stop_timestamp")] public long StopTimestamp { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("subtitle")] public string Subtitle { get; set; }
        [JsonPropertyName("guide")] public PreservedPrimaryGuide Guide { get; set; }
        [JsonPropertyName("primary")] public PreservedPrimaryImage Primary { get; set; }
    }

    internal sealed class PreservedPrimaryGuide
    {
        [JsonPropertyName("primary_icon_url")] public string PrimaryIconUrl { get; set; }
        [JsonPropertyName("poster_url")] public string PosterUrl { get; set; }
        [JsonPropertyName("poster_width")] public int PosterWidth { get; set; }
        [JsonPropertyName("poster_height")] public int PosterHeight { get; set; }
        [JsonPropertyName("backdrop_url")] public string BackdropUrl { get; set; }
        [JsonPropertyName("still_url")] public string StillUrl { get; set; }
        [JsonPropertyName("still_width")] public int StillWidth { get; set; }
        [JsonPropertyName("still_height")] public int StillHeight { get; set; }
    }

    internal sealed class PreservedPrimaryImage
    {
        [JsonPropertyName("url")] public string Url { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
    }
}
