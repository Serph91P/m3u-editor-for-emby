using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    public class LiveStreamInfo
    {
        [JsonPropertyName("num")]
        public int Num { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream_type")]
        public string StreamType { get; set; } = string.Empty;

        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }

        [JsonPropertyName("stream_icon")]
        public string StreamIcon { get; set; } = string.Empty;

        [JsonPropertyName("epg_channel_id")]
        public string EpgChannelId { get; set; } = string.Empty;

        [JsonPropertyName("added")]
        public long Added { get; set; }

        [JsonPropertyName("category_id")]
        [JsonConverter(typeof(StringOrNumberAsNullableIntConverter))]
        public int? CategoryId { get; set; }

        [JsonPropertyName("custom_sid")]
        public string CustomSid { get; set; } = string.Empty;

        [JsonPropertyName("tv_archive")]
        [JsonConverter(typeof(IntAsBoolConverter))]
        public bool TvArchive { get; set; }

        [JsonPropertyName("direct_source")]
        public string DirectSource { get; set; } = string.Empty;

        [JsonPropertyName("tv_archive_duration")]
        public int TvArchiveDuration { get; set; }

        [JsonPropertyName("is_adult")]
        [JsonConverter(typeof(IntAsBoolConverter))]
        public bool IsAdult { get; set; }

        // Optional: some Xtream-compatible backends (notably m3u-editor) attach
        // a probe-data block to each channel. When present, this lets Emby skip
        // the FFprobe pass on stream start, identical to the Dispatcharr+Streamflow
        // path. Field is null when the backend does not supply it.
        [JsonPropertyName("stream_stats")]
        public StreamStatsInfo StreamStats { get; set; }

        public bool HasTvArchive => TvArchive;
        public bool IsAdultChannel => IsAdult;
    }
}
