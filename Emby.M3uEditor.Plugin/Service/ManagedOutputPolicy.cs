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
