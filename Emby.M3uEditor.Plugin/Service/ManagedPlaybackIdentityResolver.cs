using System;
using System.IO;
using System.Linq;
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
                    !selectedRelativePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
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
                        PlaylistId = GetOptionalInt32(selectedFile, "PlaylistId")
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

        private static bool IsActiveManifest(JsonElement manifest)
        {
            if (manifest.ValueKind != JsonValueKind.Object ||
                !manifest.TryGetProperty("FormatVersion", out var version) || version.GetInt32() != 1 ||
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
    }
}
