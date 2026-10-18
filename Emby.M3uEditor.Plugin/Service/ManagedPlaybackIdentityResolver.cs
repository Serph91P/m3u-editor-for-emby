using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Emby.M3uEditor.Plugin.Service
{
    public sealed class ManagedPlaybackIdentity
    {
        public string MappingUuid { get; set; }
        public string Revision { get; set; }
        public string CanonicalId { get; set; }
        public string VariantKey { get; set; }
        public int? SourceId { get; set; }
        public int? PlaylistId { get; set; }
        public string SeriesCanonicalId { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string ManagedRoot { get; set; }
        public string ManagedPath { get; set; }
    }

    public sealed class ManagedPlaybackIdentityResolver
    {
        private const string MetadataDirectoryName = ".m3u-editor-for-emby";

        public bool TryResolve(
            string managedRoot,
            string selectedMediaSourcePath,
            string selectedMediaSourceHash,
            bool selectedMediaSourceIsConcrete,
            out ManagedPlaybackIdentity identity)
        {
            identity = null;
            if (!selectedMediaSourceIsConcrete || !IsHash(selectedMediaSourceHash) ||
                string.IsNullOrWhiteSpace(selectedMediaSourcePath))
            {
                return false;
            }

            try
            {
                string selectedRelativePath;
                string selectedPath;
                if (!TryGetConfinedRelativePath(managedRoot, selectedMediaSourcePath, out selectedRelativePath, out selectedPath) ||
                    !selectedRelativePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(selectedPath) || HasReparsePoint(managedRoot, selectedPath))
                {
                    return false;
                }

                string metadataRoot;
                string manifestPath;
                if (!ManagedOutputPolicy.TryJoinUnderRoot(managedRoot, MetadataDirectoryName, out metadataRoot) ||
                    !ManagedOutputPolicy.TryJoinUnderRoot(metadataRoot, "active.json", out manifestPath) ||
                    !File.Exists(manifestPath))
                {
                    return false;
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    var manifest = document.RootElement;
                    if (!IsActiveManifest(manifest))
                    {
                        return false;
                    }

                    var matches = manifest.GetProperty("Files").EnumerateArray()
                        .Where(file => IsMatchingFile(file, selectedRelativePath, selectedMediaSourceHash))
                        .ToList();
                    if (matches.Count != 1)
                    {
                        return false;
                    }

                    var selectedFile = matches[0];
                    string canonicalId;
                    string variantKey;
                    if (!TryGetNonBlankString(selectedFile, "CanonicalId", out canonicalId) ||
                        !TryGetNonBlankString(selectedFile, "VariantKey", out variantKey))
                    {
                        return false;
                    }

                    identity = new ManagedPlaybackIdentity
                    {
                        MappingUuid = manifest.GetProperty("MappingUuid").GetString(),
                        Revision = manifest.GetProperty("Revision").GetString(),
                        CanonicalId = canonicalId,
                        VariantKey = variantKey,
                        SourceId = GetOptionalInt32(selectedFile, "SourceId"),
                        PlaylistId = GetOptionalInt32(selectedFile, "PlaylistId"),
                        SeriesCanonicalId = GetOptionalString(selectedFile, "SeriesCanonicalId"),
                        SeasonNumber = GetOptionalInt32(selectedFile, "SeasonNumber"),
                        EpisodeNumber = GetOptionalInt32(selectedFile, "EpisodeNumber"),
                        ManagedRoot = Path.GetFullPath(managedRoot),
                        ManagedPath = selectedPath
                    };
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool TryResolveActive(
            IEnumerable<string> approvedRoots,
            string selectedMediaSourcePath,
            bool selectedMediaSourceIsConcrete,
            out ManagedPlaybackIdentity identity)
        {
            identity = null;
            if (!selectedMediaSourceIsConcrete || approvedRoots == null)
            {
                return false;
            }

            var matches = new List<ManagedPlaybackIdentity>();
            foreach (var root in approvedRoots.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(PathComparer))
            {
                try
                {
                    var manifestPath = Path.Combine(root, MetadataDirectoryName, "active.json");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                    {
                        if (!IsActiveManifest(document.RootElement) ||
                            !TryGetConfinedRelativePath(root, selectedMediaSourcePath, out var relativePath, out _))
                        {
                            continue;
                        }

                        var fileMatches = document.RootElement.GetProperty("Files").EnumerateArray()
                            .Where(file => file.ValueKind == JsonValueKind.Object &&
                                TryGetNonBlankString(file, "RelativePath", out var path) &&
                                string.Equals(path, relativePath, PathComparison) &&
                                TryGetNonBlankString(file, "Sha256", out var hash) && IsHash(hash))
                            .ToList();
                        if (fileMatches.Count != 1 ||
                            !TryGetNonBlankString(fileMatches[0], "Sha256", out var selectedHash))
                        {
                            continue;
                        }

                        if (TryResolve(root, selectedMediaSourcePath, selectedHash, true, out var candidate))
                        {
                            matches.Add(candidate);
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (matches.Count != 1)
            {
                return false;
            }

            identity = matches[0];
            return true;
        }

        public bool TryResolvePlayback(
            IEnumerable<string> approvedRoots,
            string selectedMediaSourcePath,
            bool selectedMediaSourceIsConcrete,
            string itemPath,
            out ManagedPlaybackIdentity identity)
        {
            var roots = approvedRoots?.ToList();
            if (TryResolveActive(roots, selectedMediaSourcePath, selectedMediaSourceIsConcrete, out identity))
            {
                return true;
            }

            // Native Emby resolves a STRM media source to its HTTP playback URL.
            // An item path alone is not selection evidence: require an exact URL
            // match to the bytes whose hash is owned by the active manifest.
            if (!selectedMediaSourceIsConcrete ||
                !Uri.TryCreate(selectedMediaSourcePath, UriKind.Absolute, out var selectedUri) ||
                (selectedUri.Scheme != Uri.UriSchemeHttp && selectedUri.Scheme != Uri.UriSchemeHttps) ||
                !TryResolveActive(roots, itemPath, true, out var anchor))
            {
                return false;
            }

            try
            {
                var bytes = File.ReadAllBytes(anchor.ManagedPath);
                using (var reader = new StreamReader(new MemoryStream(bytes)))
                {
                    if (!string.Equals(reader.ReadToEnd().Trim(), selectedMediaSourcePath, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                string hash;
                using (var sha = SHA256.Create())
                {
                    hash = string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
                }
                if (TryResolve(anchor.ManagedRoot, anchor.ManagedPath, hash, true, out var verified) &&
                    string.Equals(verified.MappingUuid, anchor.MappingUuid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(verified.Revision, anchor.Revision, StringComparison.Ordinal) &&
                    string.Equals(verified.CanonicalId, anchor.CanonicalId, StringComparison.Ordinal) &&
                    string.Equals(verified.VariantKey, anchor.VariantKey, StringComparison.Ordinal))
                {
                    identity = verified;
                    return true;
                }
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        internal bool HasValidActiveManifestIdentity(string managedRoot, string mappingUuid, string revision)
        {
            if (string.IsNullOrWhiteSpace(managedRoot) || !Guid.TryParse(mappingUuid, out _) || !IsHash(revision) ||
                ManagedOutputPolicy.HasReparsePointInPath(managedRoot))
            {
                return false;
            }

            try
            {
                string metadataRoot;
                string manifestPath;
                if (!ManagedOutputPolicy.TryJoinUnderRoot(managedRoot, MetadataDirectoryName, out metadataRoot) ||
                    !ManagedOutputPolicy.TryJoinUnderRoot(metadataRoot, "active.json", out manifestPath) ||
                    !File.Exists(manifestPath))
                {
                    return false;
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    var manifest = document.RootElement;
                    if (!IsActiveManifest(manifest) ||
                        !string.Equals(manifest.GetProperty("MappingUuid").GetString(), mappingUuid, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(manifest.GetProperty("Revision").GetString(), revision, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    foreach (var file in manifest.GetProperty("Files").EnumerateArray())
                    {
                        if (file.ValueKind != JsonValueKind.Object ||
                            !TryGetNonBlankString(file, "RelativePath", out var relativePath) ||
                            !TryGetNonBlankString(file, "Sha256", out var hash) || !IsHash(hash) ||
                            !TryGetNonBlankString(file, "CanonicalId", out _) ||
                            !TryGetNonBlankString(file, "VariantKey", out _) ||
                            !HasRequiredInt32(file, "SourceId") || !HasRequiredInt32(file, "PlaylistId") ||
                            !ManagedOutputPolicy.TryJoinUnderRoot(managedRoot, relativePath, out var selectedPath) ||
                            !selectedPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ||
                            !File.Exists(selectedPath) || HasReparsePoint(managedRoot, selectedPath))
                        {
                            continue;
                        }

                        if (manifest.GetProperty("Files").EnumerateArray().Count(candidate =>
                                candidate.ValueKind == JsonValueKind.Object &&
                                TryGetNonBlankString(candidate, "RelativePath", out var candidatePath) &&
                                string.Equals(candidatePath, relativePath, PathComparison)) != 1)
                        {
                            continue;
                        }

                        ManagedPlaybackIdentity identity;
                        if (TryResolve(managedRoot, selectedPath, hash, true, out identity))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (JsonException) { }
            catch (UnauthorizedAccessException) { }

            return false;
        }

        public IReadOnlyList<ManagedPlaybackIdentity> GetSeriesCandidates(ManagedPlaybackIdentity current)
        {
            var result = new List<ManagedPlaybackIdentity>();
            if (current == null || string.IsNullOrWhiteSpace(current.ManagedRoot) ||
                string.IsNullOrWhiteSpace(current.SeriesCanonicalId) ||
                !current.SeasonNumber.HasValue || !current.EpisodeNumber.HasValue)
            {
                return result;
            }

            try
            {
                var manifestPath = Path.Combine(current.ManagedRoot, MetadataDirectoryName, "active.json");
                using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    var manifest = document.RootElement;
                    if (!IsActiveManifest(manifest) ||
                        !string.Equals(manifest.GetProperty("MappingUuid").GetString(), current.MappingUuid, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(manifest.GetProperty("Revision").GetString(), current.Revision, StringComparison.Ordinal))
                    {
                        return result;
                    }

                    foreach (var file in manifest.GetProperty("Files").EnumerateArray())
                    {
                        var season = GetOptionalInt32(file, "SeasonNumber");
                        var episode = GetOptionalInt32(file, "EpisodeNumber");
                        if (!TryGetNonBlankString(file, "CanonicalId", out var canonicalId) ||
                            !TryGetNonBlankString(file, "VariantKey", out var variantKey) ||
                            !TryGetNonBlankString(file, "RelativePath", out var relativePath) ||
                            !ManagedOutputPolicy.TryJoinUnderRoot(current.ManagedRoot, relativePath, out var managedPath) ||
                            !string.Equals(GetOptionalString(file, "SeriesCanonicalId"), current.SeriesCanonicalId, StringComparison.Ordinal) ||
                            !string.Equals(variantKey, current.VariantKey, StringComparison.Ordinal) ||
                            !season.HasValue || !episode.HasValue)
                        {
                            continue;
                        }

                        result.Add(new ManagedPlaybackIdentity
                        {
                            MappingUuid = current.MappingUuid,
                            Revision = current.Revision,
                            CanonicalId = canonicalId,
                            VariantKey = variantKey,
                            SourceId = GetOptionalInt32(file, "SourceId"),
                            PlaylistId = GetOptionalInt32(file, "PlaylistId"),
                            SeriesCanonicalId = current.SeriesCanonicalId,
                            SeasonNumber = season,
                            EpisodeNumber = episode,
                            ManagedRoot = current.ManagedRoot,
                            ManagedPath = managedPath
                        });
                    }
                }
            }
            catch (IOException)
            {
                return new List<ManagedPlaybackIdentity>();
            }
            catch (JsonException)
            {
                return new List<ManagedPlaybackIdentity>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<ManagedPlaybackIdentity>();
            }

            if (result.GroupBy(value => value.CanonicalId, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
                result.GroupBy(value => new { value.SeasonNumber, value.EpisodeNumber }).Any(group => group.Count() > 1))
            {
                return new List<ManagedPlaybackIdentity>();
            }

            return result;
        }

        private static bool TryGetConfinedRelativePath(
            string root,
            string selectedPath,
            out string relativePath,
            out string confinedPath)
        {
            relativePath = null;
            confinedPath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(selectedPath))
            {
                return false;
            }

            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullSelectedPath = Path.GetFullPath(selectedPath);
            var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
            if (!fullSelectedPath.StartsWith(rootPrefix, PathComparison))
            {
                return false;
            }

            var candidateRelativePath = fullSelectedPath.Substring(rootPrefix.Length)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!ManagedOutputPolicy.TryJoinUnderRoot(fullRoot, candidateRelativePath, out confinedPath) ||
                !string.Equals(Path.GetFullPath(confinedPath), fullSelectedPath, PathComparison))
            {
                return false;
            }

            relativePath = candidateRelativePath;
            return true;
        }

        private static bool HasReparsePoint(string root, string target)
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTarget = Path.GetFullPath(target);
            var relative = fullTarget.Substring(fullRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = fullRoot;
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            foreach (var segment in relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsActiveManifest(JsonElement manifest)
        {
            if (manifest.ValueKind != JsonValueKind.Object ||
                !manifest.TryGetProperty("FormatVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var formatVersion) || formatVersion != 1 ||
                !TryGetNonBlankString(manifest, "MappingUuid", out var mappingUuid) || !Guid.TryParse(mappingUuid, out _) ||
                !TryGetNonBlankString(manifest, "Revision", out var revision) || !IsHash(revision) ||
                !manifest.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return true;
        }

        private static bool IsMatchingFile(JsonElement file, string relativePath, string selectedHash)
        {
            return file.ValueKind == JsonValueKind.Object &&
                TryGetNonBlankString(file, "RelativePath", out var manifestRelativePath) &&
                TryGetNonBlankString(file, "Sha256", out var manifestHash) &&
                string.Equals(manifestRelativePath, relativePath, PathComparison) &&
                string.Equals(manifestHash, selectedHash, StringComparison.Ordinal);
        }

        private static int? GetOptionalInt32(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
                ? result
                : (int?)null;
        }

        private static bool HasRequiredInt32(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out _);
        }

        private static string GetOptionalString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool TryGetNonBlankString(JsonElement element, string name, out string value)
        {
            value = null;
            if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool IsHash(string value)
        {
            return value != null && value.Length == 64 && value.All(character =>
                (character >= 'a' && character <= 'f') || (character >= '0' && character <= '9'));
        }

        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        private static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}
