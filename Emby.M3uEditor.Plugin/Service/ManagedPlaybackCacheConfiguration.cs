using System;
using System.Linq;

namespace Emby.M3uEditor.Plugin.Service
{
    internal static class ManagedPlaybackCacheConfiguration
    {
        public static bool IsValid(PluginConfiguration configuration)
        {
            if (configuration == null ||
                !new[] { "off", "next", "next_n", "remaining_season", "whole_season" }
                    .Contains(configuration.ManagedPlaybackCacheMode ?? string.Empty, StringComparer.Ordinal) ||
                configuration.ManagedPlaybackCacheNextCount < 1 ||
                configuration.ManagedPlaybackCacheNextCount > 10 ||
                configuration.ManagedPlaybackCacheDeleteGraceHours < 0 ||
                configuration.ManagedPlaybackCacheDeleteGraceHours > 168)
            {
                return false;
            }

            var userIds = configuration.ManagedPlaybackCacheUserIds ?? new string[0];
            if (userIds.Length > 50 || userIds.Any(string.IsNullOrWhiteSpace) ||
                userIds.Distinct(StringComparer.Ordinal).Count() != userIds.Length)
            {
                return false;
            }

            return !configuration.ManagedPlaybackCacheDeleteAfterWatched || userIds.Length > 0;
        }
    }
}