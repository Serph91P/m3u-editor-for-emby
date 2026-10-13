using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class ManagedEpisodePosition
    {
        public string Id { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
    }

    internal static class ManagedPlaybackCachePolicy
    {
        public static string[] Select(
            IEnumerable<ManagedEpisodePosition> episodes,
            string currentId,
            string mode,
            int count)
        {
            var all = (episodes ?? Enumerable.Empty<ManagedEpisodePosition>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id) && x.Season > 0 && x.Episode > 0)
                .ToList();
            if (all.GroupBy(x => x.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
                all.GroupBy(x => new { x.Season, x.Episode }).Any(group => group.Count() > 1))
            {
                return new string[0];
            }

            var current = all.SingleOrDefault(x => string.Equals(x.Id, currentId, StringComparison.Ordinal));
            if (current == null || string.Equals(mode, "off", StringComparison.Ordinal))
            {
                return new string[0];
            }

            var ordered = all.OrderBy(x => x.Season).ThenBy(x => x.Episode);
            Func<ManagedEpisodePosition, bool> later = x => x.Season > current.Season ||
                (x.Season == current.Season && x.Episode > current.Episode);
            if (string.Equals(mode, "next", StringComparison.Ordinal))
            {
                return ordered.Where(later).Take(1).Select(x => x.Id).ToArray();
            }

            if (string.Equals(mode, "next_n", StringComparison.Ordinal) && count >= 1 && count <= 10)
            {
                return ordered.Where(later).Take(count).Select(x => x.Id).ToArray();
            }

            if (string.Equals(mode, "remaining_season", StringComparison.Ordinal))
            {
                return ordered.Where(x => x.Season == current.Season && x.Episode > current.Episode)
                    .Take(100).Select(x => x.Id).ToArray();
            }

            if (string.Equals(mode, "whole_season", StringComparison.Ordinal))
            {
                return ordered.Where(x => x.Season == current.Season && x.Id != current.Id)
                    .Take(100).Select(x => x.Id).ToArray();
            }

            return new string[0];
        }
    }
}
