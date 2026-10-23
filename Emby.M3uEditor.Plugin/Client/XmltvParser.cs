using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Emby.M3uEditor.Plugin.Client.Models;

namespace Emby.M3uEditor.Plugin.Client
{
    /// <summary>
    /// Streaming XMLTV parser. Parses &lt;programme&gt; elements from an XMLTV feed
    /// and extracts title, description, and status flags (live, new, repeat, premiere).
    /// Uses XmlReader to avoid loading the entire document into memory.
    /// </summary>
    internal static class XmltvParser
    {
        /// <summary>
        /// Parses an XMLTV stream and returns programs grouped by XMLTV channel ID.
        /// </summary>
        /// <param name="xmlStream">The XMLTV XML stream to parse.</param>
        /// <param name="filterStartUnix">Exclude programs that stop at or before this Unix timestamp.</param>
        /// <param name="filterEndUnix">Exclude programs that start at or after this Unix timestamp.</param>
        internal static Dictionary<string, List<EpgProgram>> Parse(
            Stream xmlStream,
            long? filterStartUnix,
            long? filterEndUnix)
        {
            var result = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);

            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Ignore,
            };

            using (var reader = XmlReader.Create(xmlStream, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (!string.Equals(reader.Name, "programme", StringComparison.OrdinalIgnoreCase)) continue;

                    var program = ParseProgramme(reader, filterStartUnix, filterEndUnix);
                    if (program == null) continue;

                    List<EpgProgram> list;
                    if (!result.TryGetValue(program.ChannelId, out list))
                    {
                        list = new List<EpgProgram>();
                        result[program.ChannelId] = list;
                    }
                    list.Add(program);
                }
            }

            return result;
        }

        private static EpgProgram ParseProgramme(
            XmlReader reader,
            long? filterStartUnix,
            long? filterEndUnix)
        {
            var startAttr = reader.GetAttribute("start");
            var stopAttr = reader.GetAttribute("stop");
            var channelAttr = reader.GetAttribute("channel");

            if (string.IsNullOrEmpty(startAttr) || string.IsNullOrEmpty(stopAttr) || string.IsNullOrEmpty(channelAttr))
                return null;

            var startUnix = ParseXmltvTimestamp(startAttr);
            var stopUnix = ParseXmltvTimestamp(stopAttr);

            if (startUnix == 0 && stopUnix == 0)
                return null;

            // Apply date range filter
            if (filterEndUnix.HasValue && startUnix >= filterEndUnix.Value)
                return null;
            if (filterStartUnix.HasValue && stopUnix <= filterStartUnix.Value)
                return null;

            var program = new EpgProgram
            {
                ChannelId = channelAttr,
                StartTimestamp = startUnix,
                StopTimestamp = stopUnix,
                IsPlainText = true,
            };

            if (reader.IsEmptyElement)
                return program;

            // Read child elements at depth + 1; break when we return to the parent's end element
            var depth = reader.Depth;
            var hasTypedArtworkRole = false;
            string legacyImageUrl = null;
            var legacyImageWidth = 0;
            var legacyImageHeight = 0;
            var legacyBackdropCandidates = new List<string>();
            var legacyThumbCandidates = new List<string>();
            var legacyLogoCandidates = new List<string>();
            var standardBackdropCandidates = new List<string>();
            var standardThumbCandidates = new List<string>();
            var standardPosterCandidates = new List<ArtworkCandidate>();
            var untypedIcons = new List<ArtworkCandidate>();
            var typedPosterCandidates = new List<ArtworkCandidate>();
            var artworkByUrl = new Dictionary<string, ArtworkEvidence>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;

                var name = reader.Name;

                if (string.Equals(name, "title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.Title = ReadText(reader);
                }
                else if (string.Equals(name, "desc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.Description = ReadText(reader);
                }
                else if (string.Equals(name, "live", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsLive = true;
                }
                else if (string.Equals(name, "new", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsNew = true;
                }
                else if (string.Equals(name, "previously-shown", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsPreviouslyShown = true;
                }
                else if (string.Equals(name, "premiere", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsPremiere = true;
                }
                else if (string.Equals(name, "category", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                    {
                        var cat = ReadText(reader);
                        if (!string.IsNullOrWhiteSpace(cat))
                        {
                            if (program.Categories == null)
                                program.Categories = new List<string>();
                            program.Categories.Add(cat);
                        }
                    }
                }
                else if (string.Equals(name, "sub-title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.SubTitle = ReadText(reader);
                }
                else if (string.Equals(name, "episode-num", StringComparison.OrdinalIgnoreCase))
                {
                    // XMLTV's episode-num element is extensible through its standard
                    // system attribute. Only accept m3u-editor's explicit producer
                    // contract; onscreen/xmltv_ns values are display/ordering metadata,
                    // not proof that two programmes are the same content.
                    var system = (reader.GetAttribute("system") ?? string.Empty).Trim();
                    var value = reader.IsEmptyElement ? null : ReadText(reader).Trim();
                    if (string.Equals(system, "m3u-editor:content-id", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(value))
                    {
                        program.ContentId = value;
                    }
                    else if (string.Equals(system, "m3u-editor:series-id", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(value))
                    {
                        program.SeriesId = value;
                    }
                }
                else if (string.Equals(name, "icon", StringComparison.OrdinalIgnoreCase))
                {
                    var imageType = (reader.GetAttribute("type") ?? string.Empty).Trim();
                    var isPoster = string.Equals(imageType, "poster", StringComparison.OrdinalIgnoreCase);
                    var isBackdrop = string.Equals(imageType, "backdrop", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(imageType, "fanart", StringComparison.OrdinalIgnoreCase);
                    var isThumb = string.Equals(imageType, "screenshot", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(imageType, "episode-still", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(imageType, "still", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(imageType, "thumb", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(imageType, "thumbnail", StringComparison.OrdinalIgnoreCase);
                    var isLogo = string.Equals(imageType, "logo", StringComparison.OrdinalIgnoreCase);
                    if (isPoster || isBackdrop || isThumb || isLogo)
                        hasTypedArtworkRole = true;

                    var src = reader.GetAttribute("src");
                    var sanitized = Util.UrlValidator.SanitizeHttpUrl(src);
                    if (sanitized == null)
                        continue;

                    AddArtworkEvidence(
                        artworkByUrl,
                        sanitized,
                        isPoster ? "poster" : isBackdrop ? "backdrop" : isThumb ? "still" : isLogo ? "logo" : null,
                        reader.GetAttribute("orient"),
                        ParsePositiveDimension(reader.GetAttribute("width")),
                        ParsePositiveDimension(reader.GetAttribute("height")));

                    if (isPoster)
                    {
                        var width = ParsePositiveDimension(reader.GetAttribute("width"));
                        var height = ParsePositiveDimension(reader.GetAttribute("height"));
                        typedPosterCandidates.Add(new ArtworkCandidate
                        {
                            Url = sanitized,
                            Width = width,
                            Height = height,
                            Orient = reader.GetAttribute("orient"),
                        });
                    }
                    else if (isBackdrop)
                    {
                        legacyBackdropCandidates.Add(sanitized);
                    }
                    else if (isThumb)
                    {
                        legacyThumbCandidates.Add(sanitized);
                    }
                    else if (isLogo)
                    {
                        legacyLogoCandidates.Add(sanitized);
                    }
                    else
                    {
                        // Delay the legacy fallback until the programme has been fully read.
                        // A later typed role, even one with unusable poster geometry, must not
                        // let a generic icon occupy Emby's portrait-primary slot.
                        legacyImageUrl = sanitized;
                        legacyImageWidth = ParsePositiveDimension(reader.GetAttribute("width"));
                        legacyImageHeight = ParsePositiveDimension(reader.GetAttribute("height"));
                        untypedIcons.Add(new ArtworkCandidate
                        {
                            Url = sanitized,
                            Width = legacyImageWidth,
                            Height = legacyImageHeight,
                            Orient = reader.GetAttribute("orient"),
                        });
                    }
                }
                else if (string.Equals(name, "image", StringComparison.OrdinalIgnoreCase))
                {
                    // XMLTV's standard <image> has a text URL and role attributes, but no
                    // width/height. It is only valid as a direct programme child; nested
                    // images (for example inside <rating> or <credits>) are ignored above.
                    var imageType = (reader.GetAttribute("type") ?? string.Empty).Trim();
                    var isPoster = string.Equals(imageType, "poster", StringComparison.OrdinalIgnoreCase);
                    var isBackdrop = string.Equals(imageType, "backdrop", StringComparison.OrdinalIgnoreCase);
                    var isStill = string.Equals(imageType, "still", StringComparison.OrdinalIgnoreCase);
                    if (!isPoster && !isBackdrop && !isStill)
                        continue;

                    hasTypedArtworkRole = true;
                    var orient = (reader.GetAttribute("orient") ?? string.Empty).Trim();
                    var sanitized = reader.IsEmptyElement
                        ? null
                        : Util.UrlValidator.SanitizeHttpUrl(ReadText(reader));
                    if (sanitized == null)
                        continue;

                    AddArtworkEvidence(
                        artworkByUrl,
                        sanitized,
                        isPoster ? "poster" : isBackdrop ? "backdrop" : "still",
                        orient,
                        0,
                        0);

                    if (isBackdrop && string.Equals(orient, "L", StringComparison.OrdinalIgnoreCase))
                    {
                        standardBackdropCandidates.Add(sanitized);
                    }
                    else if (isStill && string.Equals(orient, "L", StringComparison.OrdinalIgnoreCase))
                    {
                        standardThumbCandidates.Add(sanitized);
                    }
                    else if (isPoster && string.Equals(orient, "P", StringComparison.OrdinalIgnoreCase))
                    {
                        standardPosterCandidates.Add(new ArtworkCandidate { Url = sanitized });
                    }
                    // A standard poster alone is not promoted. XMLTV DTD image
                    // elements do not carry intrinsic dimensions, and this parser does not
                    // fetch remote image bytes. It can be promoted only below when an
                    // untyped direct-child icon reports matching portrait geometry.
                }
            }

            var conflictingArtworkUrls = FindConflictingArtworkUrls(artworkByUrl);
            var pairedPosters = new List<ArtworkCandidate>();
            foreach (var standardPoster in standardPosterCandidates)
            {
                var pairedPoster = FindConsistentPortraitPair(
                    standardPoster.Url,
                    untypedIcons,
                    typedPosterCandidates,
                    conflictingArtworkUrls);
                if (pairedPoster != null && !ContainsUrl(pairedPosters, pairedPoster.Url))
                    pairedPosters.Add(pairedPoster);
            }

            foreach (var typedPoster in typedPosterCandidates)
            {
                if (!conflictingArtworkUrls.Contains(typedPoster.Url)
                    && IsPortraitPoster(typedPoster.Width, typedPoster.Height, typedPoster.Orient)
                    && !ContainsUrl(pairedPosters, typedPoster.Url))
                    pairedPosters.Add(typedPoster);
            }

            var selectedPoster = SelectQualifiedPoster(pairedPosters, untypedIcons);
            if (selectedPoster != null)
            {
                program.PosterUrl = selectedPoster.Url;
                program.PosterWidth = selectedPoster.Width;
                program.PosterHeight = selectedPoster.Height;
            }
            // Keep each qualified artwork role separate. A portrait poster is Emby's
            // primary programme image; backdrop and episode still retain their own
            // slots. If no poster is available, primary falls back through those roles.
            var orderedBackdrops = SelectDistinctNonConflictingArtwork(
                standardBackdropCandidates,
                conflictingArtworkUrls);
            if (orderedBackdrops.Count == 0)
            {
                orderedBackdrops = SelectDistinctNonConflictingArtwork(
                    legacyBackdropCandidates,
                    conflictingArtworkUrls);
            }
            if (orderedBackdrops.Count > 0)
            {
                program.BackdropImageUrl = orderedBackdrops[0];
            }
            program.ThumbImageUrl = SelectLastNonConflictingArtwork(
                standardThumbCandidates,
                conflictingArtworkUrls)
                ?? SelectLastNonConflictingArtwork(legacyThumbCandidates, conflictingArtworkUrls);
            program.LogoImageUrl = SelectLastNonConflictingArtwork(legacyLogoCandidates, conflictingArtworkUrls);
            if (program.PosterUrl != null)
            {
                program.ImageUrl = program.PosterUrl;
                program.ImageWidth = program.PosterWidth;
                program.ImageHeight = program.PosterHeight;
            }
            else
            {
                program.ImageUrl = program.BackdropImageUrl
                    ?? program.ThumbImageUrl
                    ?? program.LogoImageUrl;
                program.ImageWidth = 0;
                program.ImageHeight = 0;
            }

            if (!hasTypedArtworkRole)
            {
                var legacyLandscape = FindSingleValidLegacyLandscape(untypedIcons, conflictingArtworkUrls);
                if (legacyLandscape != null)
                {
                    // Some legacy XMLTV producers provide only direct, untyped icons.
                    // A unique 16:9 candidate with complete geometry is a usable
                    // programme image rather than an unclassified generic icon.
                    program.ImageUrl = legacyLandscape.Url;
                    program.ImageWidth = legacyLandscape.Width;
                    program.ImageHeight = legacyLandscape.Height;
                    if (program.BackdropImageUrl == null)
                        program.BackdropImageUrl = legacyLandscape.Url;
                }
                else if (legacyImageUrl != null
                    && (legacyImageWidth == 0 || legacyImageHeight == 0))
                {
                    // Preserve the historical fallback only for feeds that offer no
                    // recognized artwork role and incomplete geometry. Without both
                    // dimensions, the icon's aspect ratio is unknown, so it is not a
                    // positive landscape classification.
                    program.ImageUrl = legacyImageUrl;
                    program.ImageWidth = legacyImageWidth;
                    program.ImageHeight = legacyImageHeight;
                }
            }

            return program;
        }

        private static bool IsLegacyLandscape(int width, int height, string orient)
        {
            if (width <= 0 || height <= 0 || (long)width * 9 != (long)height * 16)
                return false;

            var normalizedOrient = (orient ?? string.Empty).Trim();
            return normalizedOrient.Length == 0
                || string.Equals(normalizedOrient, "L", StringComparison.OrdinalIgnoreCase);
        }

        private static ArtworkCandidate FindSingleValidLegacyLandscape(
            List<ArtworkCandidate> untypedIcons,
            HashSet<string> conflictingArtworkUrls)
        {
            ArtworkCandidate candidate = null;
            foreach (var icon in untypedIcons)
            {
                if (conflictingArtworkUrls.Contains(icon.Url)
                    || !IsLegacyLandscape(icon.Width, icon.Height, icon.Orient))
                    continue;

                if (candidate == null)
                {
                    candidate = icon;
                }
                else if (!string.Equals(candidate.Url, icon.Url, StringComparison.Ordinal)
                    || candidate.Width != icon.Width
                    || candidate.Height != icon.Height)
                {
                    // Multiple distinct direct landscape icons are alternatives with
                    // no role signal, so abstain rather than relying on element order.
                    return null;
                }
            }

            return candidate;
        }

        private static bool IsPortraitPoster(int width, int height, string orient)
        {
            // Geometry is mandatory because role and orient can be falsely labelled.
            // An explicitly landscape or unknown orientation is rejected rather than
            // trusting it over portrait dimensions; omitted orient is allowed.
            if (width <= 0 || height <= width)
                return false;

            var normalizedOrient = (orient ?? string.Empty).Trim();
            return normalizedOrient.Length == 0
                || string.Equals(normalizedOrient, "P", StringComparison.OrdinalIgnoreCase);
        }

        private static ArtworkCandidate FindConsistentPortraitPair(
            string posterUrl,
            List<ArtworkCandidate> untypedIcons,
            List<ArtworkCandidate> typedPosterCandidates,
            HashSet<string> conflictingArtworkUrls)
        {
            if (conflictingArtworkUrls.Contains(posterUrl))
                return null;

            ArtworkCandidate pair = null;
            foreach (var icon in untypedIcons)
            {
                if (!string.Equals(icon.Url, posterUrl, StringComparison.Ordinal))
                    continue;

                // A scalar icon repeats the selected URL without adding geometry.
                // Missing evidence must not contradict a complete portrait pair.
                if (icon.Width == 0 && icon.Height == 0)
                    continue;

                if (!IsPortraitPoster(icon.Width, icon.Height, icon.Orient))
                    return null;

                if (pair == null)
                    pair = icon;
                else if (pair.Width != icon.Width || pair.Height != icon.Height)
                    return null;
            }

            if (pair == null)
                return null;

            foreach (var typedPoster in typedPosterCandidates)
            {
                if (!string.Equals(typedPoster.Url, posterUrl, StringComparison.Ordinal))
                    continue;

                if (!IsPortraitPoster(typedPoster.Width, typedPoster.Height, typedPoster.Orient)
                    || typedPoster.Width != pair.Width
                    || typedPoster.Height != pair.Height)
                    return null;
            }

            return pair;
        }

        private static ArtworkCandidate SelectQualifiedPoster(
            List<ArtworkCandidate> candidates, List<ArtworkCandidate> untypedIcons)
        {
            // A unique scalar URL is the producer's explicit selection, but only
            // among posters whose role and portrait geometry are already attested.
            ArtworkCandidate scalarSelection = null;
            var ambiguousSelection = false;
            foreach (var icon in untypedIcons)
            {
                if (icon.Width != 0 || icon.Height != 0)
                    continue;
                foreach (var candidate in candidates)
                {
                    if (!string.Equals(icon.Url, candidate.Url, StringComparison.Ordinal))
                        continue;
                    if (scalarSelection != null
                        && !string.Equals(scalarSelection.Url, candidate.Url, StringComparison.Ordinal))
                        ambiguousSelection = true;
                    scalarSelection = candidate;
                }
            }
            if (scalarSelection != null && !ambiguousSelection)
                return scalarSelection;

            // Otherwise rank reported pixel area, height, then URL for stable ties.
            // No dimensions are inferred from role, orientation, title or URL.
            ArtworkCandidate selected = null;
            foreach (var candidate in candidates)
            {
                var area = (long)candidate.Width * candidate.Height;
                var selectedArea = selected == null ? 0 : (long)selected.Width * selected.Height;
                if (selected == null || area > selectedArea
                    || (area == selectedArea && candidate.Height > selected.Height)
                    || (area == selectedArea && candidate.Height == selected.Height
                        && string.CompareOrdinal(candidate.Url, selected.Url) < 0))
                    selected = candidate;
            }

            return selected;
        }

        private static string SelectLastNonConflictingArtwork(
            List<string> candidates,
            HashSet<string> conflictingArtworkUrls)
        {
            for (var index = candidates.Count - 1; index >= 0; index--)
            {
                if (!conflictingArtworkUrls.Contains(candidates[index]))
                    return candidates[index];
            }

            return null;
        }

        private static string SelectFirstNonConflictingArtwork(
            List<string> candidates,
            HashSet<string> conflictingArtworkUrls)
        {
            foreach (var candidate in candidates)
            {
                if (!conflictingArtworkUrls.Contains(candidate))
                    return candidate;
            }

            return null;
        }

        private static List<string> SelectDistinctNonConflictingArtwork(
            List<string> candidates,
            HashSet<string> conflictingArtworkUrls)
        {
            var selected = new List<string>();
            foreach (var candidate in candidates)
            {
                if (!conflictingArtworkUrls.Contains(candidate) && !selected.Contains(candidate))
                    selected.Add(candidate);
            }

            return selected;
        }

        private static void AddArtworkEvidence(
            Dictionary<string, ArtworkEvidence> artworkByUrl,
            string url,
            string role,
            string orient,
            int width,
            int height)
        {
            ArtworkEvidence evidence;
            if (!artworkByUrl.TryGetValue(url, out evidence))
            {
                evidence = new ArtworkEvidence();
                artworkByUrl[url] = evidence;
            }

            evidence.Add(role, orient, width, height);
        }

        private static HashSet<string> FindConflictingArtworkUrls(
            Dictionary<string, ArtworkEvidence> artworkByUrl)
        {
            var conflicts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in artworkByUrl)
            {
                if (entry.Value.IsConflicting)
                    conflicts.Add(entry.Key);
            }

            return conflicts;
        }

        private static bool ContainsUrl(List<ArtworkCandidate> candidates, string url)
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Url, url, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private sealed class ArtworkCandidate
        {
            public string Url { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Orient { get; set; }
        }

        private sealed class ArtworkEvidence
        {
            private string _role;
            private string _orient;
            private int _width;
            private int _height;
            private bool _hasCompleteGeometry;

            public bool IsConflicting { get; private set; }

            public void Add(string role, string orient, int width, int height)
            {
                if (role != null)
                {
                    if (_role == null)
                        _role = role;
                    else if (!string.Equals(_role, role, StringComparison.OrdinalIgnoreCase))
                        IsConflicting = true;
                }

                var normalizedOrient = (orient ?? string.Empty).Trim();
                if (normalizedOrient.Length > 0)
                {
                    if (_orient == null)
                        _orient = normalizedOrient;
                    else if (!string.Equals(_orient, normalizedOrient, StringComparison.OrdinalIgnoreCase))
                        IsConflicting = true;
                }

                if (width > 0 && height > 0)
                {
                    if (!_hasCompleteGeometry)
                    {
                        _width = width;
                        _height = height;
                        _hasCompleteGeometry = true;
                    }
                    else if (_width != width || _height != height)
                    {
                        IsConflicting = true;
                    }
                }
            }
        }

        private static int ParsePositiveDimension(string value)
        {
            int dimension;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out dimension)
                && dimension > 0
                ? dimension
                : 0;
        }

        /// <summary>
        /// Reads text/CDATA content from the current element, leaving the reader
        /// positioned at the element's end tag. Called only when IsEmptyElement is false.
        /// </summary>
        private static string ReadText(XmlReader reader)
        {
            var sb = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement) break;
                if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                    sb.Append(reader.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses an XMLTV timestamp ("YYYYMMDDHHmmss +HHMM") to a Unix timestamp.
        /// Returns 0 on parse failure.
        /// </summary>
        internal static long ParseXmltvTimestamp(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            value = value.Trim();
            var spaceIdx = value.IndexOf(' ');
            var datePart = spaceIdx > 0 ? value.Substring(0, spaceIdx) : value;
            var tzPart = spaceIdx > 0 ? value.Substring(spaceIdx + 1).Trim() : null;

            if (datePart.Length < 14)
                return 0;

            int year, month, day, hour, minute, second;
            if (!int.TryParse(datePart.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)) return 0;
            if (!int.TryParse(datePart.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)) return 0;
            if (!int.TryParse(datePart.Substring(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day)) return 0;
            if (!int.TryParse(datePart.Substring(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour)) return 0;
            if (!int.TryParse(datePart.Substring(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute)) return 0;
            if (!int.TryParse(datePart.Substring(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out second)) return 0;

            int offsetMinutes = 0;
            if (!string.IsNullOrEmpty(tzPart) && tzPart.Length >= 5)
            {
                int sign = tzPart[0] == '-' ? -1 : 1;
                int tzHour, tzMin;
                if (int.TryParse(tzPart.Substring(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out tzHour)
                    && int.TryParse(tzPart.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out tzMin))
                {
                    offsetMinutes = sign * (tzHour * 60 + tzMin);
                }
            }

            try
            {
                var dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                dt = dt.AddMinutes(-offsetMinutes);
                return new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }
    }
}
