using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    internal sealed class M3uEditorResponse<T>
    {
        [JsonPropertyName("api_version")]
        public int ApiVersion { get; set; }

        [JsonPropertyName("data")]
        public T Data { get; set; }
    }

    internal sealed class M3uEditorCatalog
    {
        [JsonPropertyName("api_version")]
        public int ApiVersion { get; set; }

        [JsonPropertyName("full_snapshot")]
        public bool FullSnapshot { get; set; }

        [JsonPropertyName("mappings")]
        public List<M3uEditorMapping> Mappings { get; set; } = new List<M3uEditorMapping>();

        [JsonPropertyName("revision")]
        public string Revision { get; set; }
    }

    internal sealed class M3uEditorMapping
    {
        [JsonPropertyName("mapping_uuid")]
        public string MappingUuid { get; set; }

        [JsonPropertyName("integration_id")]
        public int IntegrationId { get; set; }

        [JsonPropertyName("target_library")]
        public M3uEditorTargetLibrary TargetLibrary { get; set; }

        [JsonPropertyName("options")]
        public M3uEditorMappingOptions Options { get; set; }

        [JsonPropertyName("full_snapshot")]
        public bool FullSnapshot { get; set; }

        [JsonPropertyName("items")]
        public List<M3uEditorCatalogItem> Items { get; set; } = new List<M3uEditorCatalogItem>();

        [JsonPropertyName("revision")]
        public string Revision { get; set; }
    }

    internal sealed class M3uEditorTargetLibrary
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("collection_type")]
        public string CollectionType { get; set; }

        [JsonPropertyName("output_path")]
        public string OutputPath { get; set; }

        [JsonPropertyName("managed")]
        public bool Managed { get; set; }
    }

    internal sealed class M3uEditorMappingOptions
    {
        [JsonPropertyName("naming")]
        public string Naming { get; set; }

        [JsonPropertyName("nfo")]
        public bool Nfo { get; set; }

        [JsonPropertyName("versions")]
        public bool Versions { get; set; }

        [JsonPropertyName("cleanup")]
        public string Cleanup { get; set; }

        [JsonPropertyName("refresh")]
        public bool Refresh { get; set; }
    }

    internal sealed class M3uEditorCatalogItem
    {
        [JsonPropertyName("canonical_id")]
        public string CanonicalId { get; set; }

        [JsonPropertyName("series_canonical_id")]
        public string SeriesCanonicalId { get; set; }

        [JsonPropertyName("media_type")]
        public string MediaType { get; set; }

        [JsonPropertyName("display_title")]
        public string DisplayTitle { get; set; }

        [JsonPropertyName("display_title_source")]
        public string DisplayTitleSource { get; set; }

        [JsonPropertyName("original_title")]
        public string OriginalTitle { get; set; }

        [JsonPropertyName("original_title_source")]
        public string OriginalTitleSource { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("season_number")]
        public int? SeasonNumber { get; set; }

        [JsonPropertyName("episode_number")]
        public int? EpisodeNumber { get; set; }

        [JsonPropertyName("ids")]
        public M3uEditorProviderIds Ids { get; set; }

        [JsonPropertyName("groups")]
        public List<string> Groups { get; set; } = new List<string>();

        [JsonPropertyName("relative_folder")]
        public string RelativeFolder { get; set; }

        [JsonPropertyName("base_filename")]
        public string BaseFilename { get; set; }

        [JsonPropertyName("nfo")]
        public M3uEditorNfo Nfo { get; set; }

        [JsonPropertyName("variants")]
        public List<M3uEditorVariant> Variants { get; set; } = new List<M3uEditorVariant>();

        [JsonPropertyName("episodes")]
        public List<M3uEditorCatalogItem> Episodes { get; set; } = new List<M3uEditorCatalogItem>();
    }

    internal sealed class M3uEditorProviderIds
    {
        [JsonPropertyName("tmdb")]
        public int? Tmdb { get; set; }

        [JsonPropertyName("tvdb")]
        public int? Tvdb { get; set; }

        [JsonPropertyName("imdb")]
        public string Imdb { get; set; }
    }

    internal sealed class M3uEditorNfo
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("original_title")]
        public string OriginalTitle { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("plot")]
        public string Plot { get; set; }

        [JsonPropertyName("genres")]
        public JsonElement Genres { get; set; }

        [JsonPropertyName("season_number")]
        public int? SeasonNumber { get; set; }

        [JsonPropertyName("episode_number")]
        public int? EpisodeNumber { get; set; }

        [JsonPropertyName("ids")]
        public M3uEditorProviderIds Ids { get; set; }
    }

    internal sealed class M3uEditorVariant
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("preferred")]
        public M3uEditorSource Preferred { get; set; }

        [JsonPropertyName("failover")]
        public List<M3uEditorSource> Failover { get; set; } = new List<M3uEditorSource>();

        [JsonPropertyName("technical_metadata")]
        public JsonElement TechnicalMetadata { get; set; }
    }

    internal sealed class M3uEditorSource
    {
        [JsonPropertyName("source_id")]
        public int SourceId { get; set; }

        [JsonPropertyName("playback_url")]
        public string PlaybackUrl { get; set; }

        [JsonPropertyName("playlist_id")]
        public int? PlaylistId { get; set; }
    }

    internal sealed class M3uEditorSyncResult
    {
        [JsonPropertyName("applied")]
        public bool Applied { get; set; }

        [JsonPropertyName("duplicate")]
        public bool Duplicate { get; set; }

        [JsonPropertyName("mapping_uuid")]
        public string MappingUuid { get; set; }

        [JsonPropertyName("revision")]
        public string Revision { get; set; }
    }
}
