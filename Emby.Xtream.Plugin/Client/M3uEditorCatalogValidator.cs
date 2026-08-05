using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Client
{
    internal static class M3uEditorCatalogValidator
    {
        internal const int MaximumMappings = 100;
        internal const int MaximumCatalogItems = 10000;
        internal const int MaximumVariantsPerItem = 64;
        internal const int MaximumGeneratedFiles = 50000;
        private static readonly Regex RevisionPattern = new Regex("^[a-f0-9]{64}$", RegexOptions.Compiled);
        private static readonly Regex InvalidFilenamePattern = new Regex("[<>:\"/\\\\|?*\\x00-\\x1F]", RegexOptions.Compiled);

        public static void Validate(M3uEditorCatalog catalog)
        {
            if (catalog.ApiVersion != 1)
            {
                Fail("Managed catalog API version is not supported.");
            }

            if (!catalog.FullSnapshot)
            {
                Fail("Managed catalog must be a full snapshot.");
            }

            ValidateRevision(catalog.Revision, "Managed catalog revision is invalid.");
            if (catalog.Mappings == null || catalog.Mappings.Count > MaximumMappings)
            {
                Fail("Managed catalog mappings are invalid.");
            }

            var mappingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalItems = 0;
            long totalFiles = 0;
            foreach (var mapping in catalog.Mappings)
            {
                CountOutput(mapping, ref totalItems, ref totalFiles);
                if (totalItems > MaximumCatalogItems || totalFiles > MaximumGeneratedFiles)
                {
                    Fail("Managed catalog exceeds item or generated file limits.");
                }

                ValidateMapping(mapping);
                if (!mappingIds.Add(mapping.MappingUuid))
                {
                    Fail("Managed catalog contains a duplicate mapping identity.");
                }
            }
        }

        private static void ValidateMapping(M3uEditorMapping mapping)
        {
            Guid mappingId;
            if (mapping == null || !Guid.TryParse(mapping.MappingUuid, out mappingId) || mapping.IntegrationId < 1)
            {
                Fail("Managed catalog mapping identity is invalid.");
            }

            if (!mapping.FullSnapshot)
            {
                Fail("Managed mapping must be a full snapshot.");
            }

            ValidateRevision(mapping.Revision, "Managed mapping revision is invalid.");
            if (mapping.TargetLibrary == null || string.IsNullOrWhiteSpace(mapping.TargetLibrary.Name))
            {
                Fail("Managed target library is invalid.");
            }

            var collectionType = mapping.TargetLibrary.CollectionType;
            if (!string.Equals(collectionType, "movies", StringComparison.Ordinal) &&
                !string.Equals(collectionType, "tvshows", StringComparison.Ordinal))
            {
                Fail("Managed target library collection type is invalid.");
            }

            if (!IsAbsolutePath(mapping.TargetLibrary.OutputPath))
            {
                Fail("Managed target library output path is invalid.");
            }

            if (mapping.Options == null ||
                (!string.Equals(mapping.Options.Cleanup, "replace", StringComparison.Ordinal) &&
                 !string.Equals(mapping.Options.Cleanup, "keep", StringComparison.Ordinal) &&
                 !string.Equals(mapping.Options.Cleanup, "disabled", StringComparison.Ordinal)))
            {
                Fail("Managed mapping options are invalid.");
            }

            if (mapping.Items == null)
            {
                Fail("Managed mapping items are invalid.");
            }

            var canonicalIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in mapping.Items)
            {
                var expectedType = string.Equals(collectionType, "movies", StringComparison.Ordinal)
                    ? "movie"
                    : "series";
                ValidateItem(item, expectedType);
                if (!canonicalIds.Add(item.CanonicalId))
                {
                    Fail("Managed mapping contains a duplicate canonical item.");
                }
            }
        }

        private static void ValidateItem(M3uEditorCatalogItem item, string expectedType)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.CanonicalId) ||
                !string.Equals(item.MediaType, expectedType, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.DisplayTitle))
            {
                Fail("Managed catalog item media type or identity is invalid.");
            }

            if (!IsSafeRelativePath(item.RelativeFolder))
            {
                Fail("Managed catalog item relative path is invalid.");
            }

            if (!IsSafeFilename(item.BaseFilename))
            {
                Fail("Managed catalog item filename is invalid.");
            }

            ValidateProviderIds(item.Ids);
            ValidateProviderIds(item.Nfo == null ? null : item.Nfo.Ids);
            ValidateNfo(item.Nfo);

            if (string.Equals(expectedType, "series", StringComparison.Ordinal))
            {
                if (item.Episodes == null)
                {
                    Fail("Managed series episodes are invalid.");
                }

                var episodeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var episode in item.Episodes)
                {
                    ValidateItem(episode, "episode");
                    if (!string.Equals(episode.SeriesCanonicalId, item.CanonicalId, StringComparison.Ordinal) ||
                        !episode.SeasonNumber.HasValue || episode.SeasonNumber.Value < 1 ||
                        !episode.EpisodeNumber.HasValue || episode.EpisodeNumber.Value < 1 ||
                        !episodeIds.Add(episode.CanonicalId))
                    {
                        Fail("Managed episode identity is invalid.");
                    }
                }
            }
            else
            {
                ValidateVariants(item.Variants);
            }
        }

        private static void ValidateVariants(List<M3uEditorVariant> variants)
        {
            if (variants == null || variants.Count == 0 || variants.Count > MaximumVariantsPerItem)
            {
                Fail("Managed item variants are invalid.");
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variant in variants)
            {
                if (variant == null || !IsSafeFilename(variant.Key) || !keys.Add(variant.Key))
                {
                    Fail("Managed item contains a duplicate or invalid variant filename.");
                }

                var sourceIds = new HashSet<int>();
                ValidateSource(variant.Preferred, sourceIds);
                if (variant.Failover == null)
                {
                    Fail("Managed variant failover list is invalid.");
                }

                foreach (var failover in variant.Failover)
                {
                    ValidateSource(failover, sourceIds);
                }
            }
        }

        private static void CountOutput(M3uEditorMapping mapping, ref long itemCount, ref long fileCount)
        {
            if (mapping == null || mapping.Items == null)
            {
                return;
            }

            foreach (var item in mapping.Items)
            {
                CountItemOutput(item, ref itemCount, ref fileCount);
            }
        }

        private static void CountItemOutput(M3uEditorCatalogItem item, ref long itemCount, ref long fileCount)
        {
            itemCount++;
            if (item == null)
            {
                return;
            }

            if (item.Nfo != null)
            {
                fileCount++;
            }

            if (item.Variants != null)
            {
                fileCount += Math.Min(8, item.Variants.Count);
            }

            if (item.Episodes == null)
            {
                return;
            }

            foreach (var episode in item.Episodes)
            {
                CountItemOutput(episode, ref itemCount, ref fileCount);
            }
        }

        private static void ValidateSource(M3uEditorSource source, HashSet<int> sourceIds)
        {
            Uri uri;
            if (source == null || source.SourceId < 1 || !sourceIds.Add(source.SourceId) ||
                string.IsNullOrWhiteSpace(source.PlaybackUrl) ||
                !Uri.TryCreate(source.PlaybackUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Fail("Managed variant playback URL or source identity is invalid.");
            }
        }

        private static void ValidateProviderIds(M3uEditorProviderIds ids)
        {
            if (ids == null ||
                (ids.Tmdb.HasValue && ids.Tmdb.Value < 1) ||
                (ids.Tvdb.HasValue && ids.Tvdb.Value < 1) ||
                (!string.IsNullOrEmpty(ids.Imdb) &&
                 (ids.Imdb.Length > 64 || ids.Imdb.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != '.'))))
            {
                Fail("Managed catalog provider ID is invalid.");
            }
        }

        private static void ValidateNfo(M3uEditorNfo nfo)
        {
            if (nfo == null || string.IsNullOrWhiteSpace(nfo.Title) ||
                !HasValidXmlCharacters(nfo.Title) ||
                !HasValidXmlCharacters(nfo.OriginalTitle) ||
                !HasValidXmlCharacters(nfo.Plot))
            {
                Fail("Managed catalog NFO metadata is invalid.");
            }

            if (nfo.Genres.ValueKind != JsonValueKind.Undefined &&
                nfo.Genres.ValueKind != JsonValueKind.Null &&
                nfo.Genres.ValueKind != JsonValueKind.String &&
                nfo.Genres.ValueKind != JsonValueKind.Array)
            {
                Fail("Managed catalog NFO genres are invalid.");
            }

            if (nfo.Genres.ValueKind == JsonValueKind.Array &&
                nfo.Genres.EnumerateArray().Any(genre => genre.ValueKind != JsonValueKind.String ||
                    !HasValidXmlCharacters(genre.GetString())))
            {
                Fail("Managed catalog NFO genres are invalid.");
            }

            if (nfo.Genres.ValueKind == JsonValueKind.String &&
                !HasValidXmlCharacters(nfo.Genres.GetString()))
            {
                Fail("Managed catalog NFO genres are invalid.");
            }
        }

        private static bool HasValidXmlCharacters(string value)
        {
            if (value == null)
            {
                return true;
            }

            try
            {
                XmlConvert.VerifyXmlChars(value);
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.IndexOf('\0') >= 0)
            {
                return false;
            }

            if (!string.Equals(path, path.Trim(), StringComparison.Ordinal) || path == "/" || path == "\\" ||
                (path.Length == 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                 (path[2] == '/' || path[2] == '\\')))
            {
                return false;
            }

            return Path.IsPathRooted(path);
        }

        internal static bool IsSafeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.IndexOf('\0') >= 0 ||
                path[0] == '/' || path[0] == '\\' ||
                (path.Length >= 2 && path[1] == ':'))
            {
                return false;
            }

            return path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .All(segment => segment != "." && segment != ".." && IsSafeFilename(segment));
        }

        internal static bool IsSafeFilename(string filename)
        {
            return !string.IsNullOrWhiteSpace(filename) && filename.Length <= 240 &&
                   filename != "." && filename != ".." && !InvalidFilenamePattern.IsMatch(filename);
        }

        private static void ValidateRevision(string revision, string message)
        {
            if (string.IsNullOrEmpty(revision) || !RevisionPattern.IsMatch(revision))
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            throw new InvalidOperationException(message);
        }
    }
}
