using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.M3uEditor.Plugin.Service
{
    internal static class ManagedOutputPolicy
    {
        internal static bool IsApproved(string targetPath, string approvedRoots, out string error)
        {
            error = "Managed output is not within a locally approved root.";
            string target;
            if (!TryNormalize(targetPath, out target) || HasReparsePoint(target))
            {
                return false;
            }

            var roots = ParseRoots(approvedRoots);
            if (roots.Count == 0 || roots.Any(root => IsFileSystemRoot(root) || HasReparsePoint(root)))
            {
                return false;
            }

            for (var left = 0; left < roots.Count; left++)
            {
                for (var right = left + 1; right < roots.Count; right++)
                {
                    if (IsSameOrChild(roots[left], roots[right]) || IsSameOrChild(roots[right], roots[left]))
                    {
                        error = "Managed approved output roots must not overlap.";
                        return false;
                    }
                }
            }

            if (!roots.Any(root => IsSameOrChild(root, target)))
            {
                return false;
            }

            error = string.Empty;
            return true;
        }

        internal static bool TryValidateSetupRoot(
            string ownerPath,
            string candidatePath,
            string existingApprovedRoots,
            string legacyRoot,
            bool legacyWriterEnabled,
            out string normalizedCandidate,
            out string error)
        {
            normalizedCandidate = null;
            error = "The managed output root is not safe.";
            string normalizedOwner;
            if (!TryNormalize(ownerPath, out normalizedOwner) ||
                !TryNormalize(candidatePath, out normalizedCandidate) ||
                IsFileSystemRoot(normalizedOwner) || IsFileSystemRoot(normalizedCandidate) ||
                HasReparsePoint(normalizedOwner) || HasReparsePoint(normalizedCandidate) ||
                !IsSameOrChild(normalizedOwner, normalizedCandidate) ||
                string.Equals(normalizedOwner, normalizedCandidate, PathComparison))
            {
                normalizedCandidate = null;
                return false;
            }

            var existing = ParseRoots(existingApprovedRoots);
            if (!string.IsNullOrWhiteSpace(existingApprovedRoots) && existing.Count == 0)
            {
                normalizedCandidate = null;
                return false;
            }

            if (existing.Any(root => IsFileSystemRoot(root) || HasReparsePoint(root)))
            {
                normalizedCandidate = null;
                return false;
            }

            for (var left = 0; left < existing.Count; left++)
            {
                for (var right = left + 1; right < existing.Count; right++)
                {
                    if (IsSameOrChild(existing[left], existing[right]) ||
                        IsSameOrChild(existing[right], existing[left]))
                    {
                        error = "Managed approved output roots must not overlap.";
                        normalizedCandidate = null;
                        return false;
                    }
                }
            }

            var candidate = normalizedCandidate;
            if (existing.Any(root =>
                !string.Equals(root, candidate, PathComparison) &&
                (IsSameOrChild(root, candidate) || IsSameOrChild(candidate, root))))
            {
                error = "The managed output root overlaps an existing approved root.";
                normalizedCandidate = null;
                return false;
            }

            string normalizedLegacy;
            if (legacyWriterEnabled && TryNormalize(legacyRoot, out normalizedLegacy) &&
                (IsSameOrChild(normalizedLegacy, normalizedCandidate) ||
                 IsSameOrChild(normalizedCandidate, normalizedLegacy)))
            {
                error = "The managed output root overlaps an enabled legacy writer.";
                normalizedCandidate = null;
                return false;
            }

            error = string.Empty;
            return true;
        }

        internal static List<string> GetCanonicalRoots(string approvedRoots)
        {
            var roots = ParseRoots(approvedRoots);
            if (roots.Count == 0 || roots.Any(root => IsFileSystemRoot(root) || HasReparsePoint(root)))
            {
                return new List<string>();
            }

            for (var left = 0; left < roots.Count; left++)
            {
                for (var right = left + 1; right < roots.Count; right++)
                {
                    if (IsSameOrChild(roots[left], roots[right]) ||
                        IsSameOrChild(roots[right], roots[left]))
                    {
                        return new List<string>();
                    }
                }
            }

            return roots;
        }

        internal static bool IsLocallyWritableRoot(string root)
        {
            if (!Directory.Exists(root) || HasReparsePoint(root))
            {
                return false;
            }

            string probePath;
            if (!TryJoinUnderRoot(
                root,
                ".managed-write-probe-" + Guid.NewGuid().ToString("N"),
                out probePath))
            {
                return false;
            }

            try
            {
                using (new FileStream(
                    probePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static bool TryJoinUnderRoot(string root, string relativePath, out string joinedPath)
        {
            joinedPath = null;
            string normalizedRoot;
            if (!TryNormalize(root, out normalizedRoot))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(relativePath) ||
                !string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            if (Path.IsPathRooted(relativePath))
            {
                return false;
            }

            string normalizedCandidate;
            var candidate = normalizedRoot + Path.DirectorySeparatorChar + relativePath;
            if (!TryNormalize(candidate, out normalizedCandidate) ||
                string.Equals(normalizedRoot, normalizedCandidate, PathComparison) ||
                !IsSameOrChild(normalizedRoot, normalizedCandidate))
            {
                return false;
            }

            joinedPath = normalizedCandidate;
            return true;
        }

        internal static bool PathsOverlap(string left, string right)
        {
            string normalizedLeft;
            string normalizedRight;
            return TryNormalize(left, out normalizedLeft) &&
                   TryNormalize(right, out normalizedRight) &&
                   (IsSameOrChild(normalizedLeft, normalizedRight) ||
                    IsSameOrChild(normalizedRight, normalizedLeft));
        }

        private static List<string> ParseRoots(string value)
        {
            var roots = new List<string>();
            foreach (var entry in (value ?? string.Empty).Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized;
                if (!TryNormalize(entry, out normalized) ||
                    roots.Any(root => string.Equals(root, normalized, PathComparison)))
                {
                    return new List<string>();
                }

                roots.Add(normalized);
            }

            return roots;
        }

        private static bool TryNormalize(string path, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(path) || !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
                path.IndexOf('\0') >= 0 || !Path.IsPathRooted(path))
            {
                return false;
            }

            if (Path.DirectorySeparatorChar == '/' && path.IndexOf('\\') >= 0)
            {
                return false;
            }

            try
            {
                normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !string.IsNullOrEmpty(normalized);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static bool IsFileSystemRoot(string path)
        {
            var root = Path.GetPathRoot(path);
            return string.Equals(path, root == null ? null : root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar), PathComparison);
        }

        private static bool HasReparsePoint(string path)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                    return true;
                }
                catch (IOException)
                {
                    return true;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, PathComparison))
                {
                    break;
                }

                current = parent;
            }

            return false;
        }

        private static bool IsSameOrChild(string parent, string candidate)
        {
            return string.Equals(parent, candidate, PathComparison) ||
                   candidate.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);
        }

        private static StringComparison PathComparison
        {
            get
            {
                return Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            }
        }
    }
}
