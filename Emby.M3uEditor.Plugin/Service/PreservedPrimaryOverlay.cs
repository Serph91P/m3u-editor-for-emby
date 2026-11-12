using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;

namespace Emby.M3uEditor.Plugin.Service
{
    internal static class PreservedPrimaryOverlay
    {
        internal static List<EpgProgram> Apply(IEnumerable<EpgProgram> programs, int streamId,
            IEnumerable<PreservedPrimaryEntry> entries, string format)
        {
            var source = programs.ToList();
            if (entries == null) return source;
            var bySlot = entries.Where(e => e != null && e.StreamId == streamId)
                .ToLookup(e => (e.StartTimestamp, e.StopTimestamp, e.Title));
            var programmes = source.ToLookup(p => (p.StartTimestamp, p.StopTimestamp, Title(p)));
            var result = new List<EpgProgram>(source.Count);
            foreach (var p in source)
            {
                var key = (p.StartTimestamp, p.StopTimestamp, Title(p));
                var matches = bySlot[key].ToList();
                var entry = matches.FirstOrDefault();
                // Compare every rival, not just the first matching artwork tuple.
                if (entry == null || matches.Select(e => JsonSerializer.Serialize(e)).Distinct(StringComparer.Ordinal).Count() != 1
                    || programmes[key].Any(other => !SameEvidence(p, other, format)) || !Matches(p, entry, format)
                    || (p.PreservedPrimaryUrl != null && p.PreservedPrimaryUrl != entry.Primary.Url))
                {
                    result.Add(p);
                    continue;
                }
                var copy = p.Copy();
                copy.PreservedPrimaryUrl = entry.Primary.Url;
                Geometry(p, entry, out var width, out var height);
                copy.ProjectedPrimaryWidth = width;
                copy.ProjectedPrimaryHeight = height;
                result.Add(copy);
            }
            return result;
        }

        private static string Title(EpgProgram p) => p.IsPlainText ? p.Title : LiveTvService.DecodeBase64(p.Title);

        private static bool SameEvidence(EpgProgram a, EpgProgram b, string format)
        {
            return a.SubTitle == b.SubTitle && a.SourcePrimaryIconUrl == b.SourcePrimaryIconUrl
                && a.SourcePrimaryIconWidth == b.SourcePrimaryIconWidth && a.SourcePrimaryIconHeight == b.SourcePrimaryIconHeight
                && a.PosterUrl == b.PosterUrl && a.PosterWidth == b.PosterWidth && a.PosterHeight == b.PosterHeight
                && (a.BackdropUrl ?? a.BackdropImageUrl) == (b.BackdropUrl ?? b.BackdropImageUrl)
                && a.ThumbImageUrl == b.ThumbImageUrl && a.ThumbWidth == b.ThumbWidth && a.ThumbHeight == b.ThumbHeight
                && a.PreservedPrimaryUrl == b.PreservedPrimaryUrl;
        }

        private static bool Matches(EpgProgram p, PreservedPrimaryEntry e, string format)
        {
            var g = e.Guide;
            if (g == null || e.Primary == null || !PreservedPrimaryClient.ExactHttpUrl(e.Primary.Url)) return false;
            if (format == "xmltv")
            {
                if (p.SubTitle != e.Subtitle || p.SourcePrimaryIconUrl == null || p.SourcePrimaryIconUrl != g.PrimaryIconUrl
                    || e.Primary.Url != g.PrimaryIconUrl) return false;
            }
            else if (format != "json" || g.PrimaryIconUrl != null || e.Subtitle != null) return false;
            return p.PosterUrl == g.PosterUrl && p.PosterWidth == g.PosterWidth && p.PosterHeight == g.PosterHeight
                && (p.BackdropUrl ?? p.BackdropImageUrl) == g.BackdropUrl
                && p.ThumbImageUrl == g.StillUrl && p.ThumbWidth == g.StillWidth && p.ThumbHeight == g.StillHeight;
        }

        private static void Geometry(EpgProgram p, PreservedPrimaryEntry e, out int width, out int height)
        {
            width = 0; height = 0;
            if (e.Primary.Width <= 0 || e.Primary.Height <= 0) return;
            foreach (var evidence in new[] {
                (Url: p.SourcePrimaryIconUrl, Width: p.SourcePrimaryIconWidth, Height: p.SourcePrimaryIconHeight),
                (Url: p.PosterUrl, Width: p.PosterWidth, Height: p.PosterHeight),
                (Url: p.ThumbImageUrl, Width: p.ThumbWidth, Height: p.ThumbHeight) })
            {
                if (evidence.Url == e.Primary.Url && evidence.Width > 0 && evidence.Height > 0
                    && (evidence.Width != e.Primary.Width || evidence.Height != e.Primary.Height)) return;
            }
            width = e.Primary.Width; height = e.Primary.Height;
        }
    }
}
