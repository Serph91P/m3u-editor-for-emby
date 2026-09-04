using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;
using Emby.M3uEditor.Plugin.Util;

namespace Emby.M3uEditor.Plugin.Service
{
    /// <summary>
    /// Service for generating M3U playlists and XMLTV EPG files for Live TV.
    /// </summary>
    public class LiveTvService : IDisposable
    {
        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private readonly ILogger _logger;
        private readonly Func<int, HttpClient> _httpClientFactory;
        private readonly SemaphoreSlim _m3uLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _epgLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _xmltvLock = new SemaphoreSlim(1, 1);
        private readonly object _perChannelEpgLock = new object();

        private Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)> _perChannelEpgCache
            = new Dictionary<int, (List<EpgProgram>, DateTime)>();

        // XMLTV bulk EPG cache: epg_channel_id → programs (populated from /xmltv.php)
        private volatile Dictionary<string, List<EpgProgram>> _xmltvCache;
        private DateTime _xmltvCacheTime = DateTime.MinValue;
        // _xmltvFailed is set true after a fetch failure to avoid hammering the server.
        // It is reset either by InvalidateCache() or automatically once the cache TTL
        // has elapsed from the failure time, allowing transparent recovery.
        private volatile bool _xmltvFailed;
        private DateTime _xmltvFailedTime = DateTime.MinValue;
        private Dictionary<int, string> _epgChannelIdByStreamId = new Dictionary<int, string>();

        private volatile string _cachedM3U;
        private volatile string _cachedEpgXml;
        private DateTime _m3uCacheTime = DateTime.MinValue;
        private DateTime _epgCacheTime = DateTime.MinValue;
        private bool _disposed;

        public LiveTvService(ILogger logger)
            : this(logger, timeout => Plugin.CreateHttpClient(timeout))
        {
        }

        internal LiveTvService(ILogger logger, Func<int, HttpClient> httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        /// <summary>Exposed for unit testing only: indicates whether the last XMLTV fetch failed.</summary>
        internal bool XmltvFailed => _xmltvFailed;

        /// <summary>Exposed for unit testing only: the time the last XMLTV fetch failure occurred.</summary>
        internal DateTime XmltvFailedTime => _xmltvFailedTime;

        /// <summary>Exposed for unit testing only: whether the M3U cache is populated.</summary>
        internal bool HasCachedM3U => _cachedM3U != null;

        /// <summary>Exposed for unit testing only: whether the EPG XML cache is populated.</summary>
        internal bool HasCachedEpgXml => _cachedEpgXml != null;

        /// <summary>Exposed for unit testing only: number of entries in the per-channel EPG cache.</summary>
        internal int PerChannelEpgCacheCount
        {
            get { lock (_perChannelEpgLock) { return _perChannelEpgCache.Count; } }
        }

        /// <summary>Exposed for unit testing only: whether the bulk XMLTV cache is populated.</summary>
        internal bool HasXmltvCache => _xmltvCache != null;

        /// <summary>
        /// Gets the M3U playlist for Live TV channels.
        /// </summary>
        public async Task<string> GetM3UPlaylistAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            await _m3uLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedM3U != null && DateTime.UtcNow - _m3uCacheTime < TimeSpan.FromMinutes(config.M3UCacheMinutes))
                {
                    _logger.Debug("Returning cached M3U playlist");
                    return _cachedM3U;
                }

                _logger.Info("Generating M3U playlist");
                var channelsTask = GetFilteredChannelsAsync(cancellationToken);
                var categoriesTask = GetLiveCategoriesAsync(cancellationToken);
                Dictionary<int, string> categoryMap;
                try
                {
                    await Task.WhenAll(channelsTask, categoriesTask).ConfigureAwait(false);
                    categoryMap = categoriesTask.Result.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to fetch live categories for M3U group-title; categories will be omitted: {0}", ex.Message);
                    await channelsTask.ConfigureAwait(false);
                    categoryMap = new Dictionary<int, string>();
                }

                var channels = channelsTask.Result;
                if (IsLiveTvDiagnosticsEnabled())
                {
                    _logger.Info("[livetv-diag] m3u-build channels={0} categories={1}",
                        channels.Count, categoryMap.Count);
                }
                var m3u = GenerateM3U(channels, config, categoryMap);

                _cachedM3U = m3u;
                _m3uCacheTime = DateTime.UtcNow;

                return m3u;
            }
            finally
            {
                _m3uLock.Release();
            }
        }

        /// <summary>
        /// Gets the XMLTV EPG for Live TV channels.
        /// </summary>
        public async Task<string> GetXmltvEpgAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            await _epgLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedEpgXml != null && DateTime.UtcNow - _epgCacheTime < TimeSpan.FromMinutes(config.EpgCacheMinutes))
                {
                    _logger.Debug("Returning cached XMLTV EPG");
                    return _cachedEpgXml;
                }

                _logger.Info("Generating XMLTV EPG");
                var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                if (IsLiveTvDiagnosticsEnabled())
                {
                    _logger.Info("[livetv-diag] xmltv-build sourceChannels={0} epgSource={1} days={2}",
                        channels.Count, config.EpgSource, config.EpgDaysToFetch);
                }
                var epgXml = await GenerateXmltvAsync(channels, config, cancellationToken).ConfigureAwait(false);
                if (IsLiveTvDiagnosticsEnabled())
                {
                    _logger.Info("[livetv-diag] xmltv-build complete bytes={0}", Encoding.UTF8.GetByteCount(epgXml));
                }

                _cachedEpgXml = epgXml;
                _epgCacheTime = DateTime.UtcNow;

                return epgXml;
            }
            finally
            {
                _epgLock.Release();
            }
        }

        /// <summary>
        /// Gets the live TV categories from the Xtream API.
        /// </summary>
        public async Task<List<Category>> GetLiveCategoriesAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_live_categories",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

            using (var httpClient = _httpClientFactory(10))
            {
                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                var categories = STJ.JsonSerializer.Deserialize<List<Category>>(json, JsonOptions)
                    ?? new List<Category>();
                if (IsLiveTvDiagnosticsEnabled())
                {
                    _logger.Info("[livetv-diag] live-categories fetched count={0}", categories.Count);
                }
                return categories.OrderBy(c => c.CategoryName).ToList();
            }
        }

        /// <summary>
        /// Invalidates the M3U and EPG caches.
        /// </summary>
        public void InvalidateCache()
        {
            _cachedM3U = null;
            _cachedEpgXml = null;
            _m3uCacheTime = DateTime.MinValue;
            _epgCacheTime = DateTime.MinValue;
            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache = new Dictionary<int, (List<EpgProgram>, DateTime)>();
            }
            _xmltvCache = null;
            _xmltvCacheTime = DateTime.MinValue;
            _xmltvFailed = false;
            _xmltvFailedTime = DateTime.MinValue;
            _epgChannelIdByStreamId = new Dictionary<int, string>();
            _logger.Info("Live TV cache invalidated");
        }

        /// <summary>
        /// Gets filtered channels from the Xtream API, applying category filters,
        /// adult filtering, and channel overrides.
        /// </summary>
        internal async Task<List<LiveStreamInfo>> GetFilteredChannelsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var diagnostics = IsLiveTvDiagnosticsEnabled();
            if (diagnostics)
            {
                _logger.Info("[livetv-diag] channel-filter start selectedCategories={0} includeAdult={1} nameCleaning={2}",
                    config.SelectedLiveCategoryIds != null ? config.SelectedLiveCategoryIds.Length : 0,
                    config.IncludeAdultChannels,
                    config.EnableChannelNameCleaning);
            }

            List<LiveStreamInfo> allChannels;

            allChannels = await FetchAllChannelsAsync(cancellationToken).ConfigureAwait(false);
            if (diagnostics)
            {
                _logger.Info("[livetv-diag] all-channel-fetch rawChannels={0}", allChannels.Count);
            }

            if (config.SelectedLiveCategoryIds != null && config.SelectedLiveCategoryIds.Length > 0)
            {
                var beforeCategoryFilter = allChannels.Count;
                allChannels = FilterChannelsBySelectedCategories(allChannels, config.SelectedLiveCategoryIds);
                if (diagnostics)
                {
                    _logger.Info("[livetv-diag] category-filter before={0} after={1} removed={2}",
                        beforeCategoryFilter, allChannels.Count, beforeCategoryFilter - allChannels.Count);
                }
            }

            var beforeAdultFilter = allChannels.Count;
            // Filter adult channels
            if (!config.IncludeAdultChannels)
            {
                allChannels = allChannels.Where(c => !c.IsAdultChannel).ToList();
            }
            if (diagnostics)
            {
                _logger.Info("[livetv-diag] adult-filter before={0} after={1} removed={2}",
                    beforeAdultFilter, allChannels.Count, beforeAdultFilter - allChannels.Count);
                LogLiveTvChannelDiagnostics(allChannels, config, "filtered");
            }

            // Channel hash: detect changes and invalidate dependent caches.
            // This is the canonical drift-detection point; ALL Live-TV cache layers
            // (M3U, EPG XML, bulk XMLTV, per-channel EPG, tuner channel cache) must
            // be wiped here, otherwise renamed/added/deleted channels at the Xtream
            // provider remain invisible until each layer's TTL expires independently.
            var newHash = StrmSyncService.ComputeChannelListHash(allChannels);
            if (newHash != config.LastChannelListHash)
            {
                _logger.Info("Channel list changed (hash {0} → {1}), invalidating dependent caches",
                    string.IsNullOrEmpty(config.LastChannelListHash) ? "(none)" : config.LastChannelListHash.Substring(0, 8),
                    newHash.Substring(0, 8));
                if (diagnostics)
                {
                    _logger.Info("[livetv-diag] channel-hash changed old={0} new={1} channels={2} m3uCached={3} epgXmlCached={4} xmltvCached={5} perChannelEpgEntries={6}",
                        string.IsNullOrEmpty(config.LastChannelListHash) ? "(none)" : config.LastChannelListHash.Substring(0, 8),
                        newHash.Substring(0, 8),
                        allChannels.Count,
                        _cachedM3U != null,
                        _cachedEpgXml != null,
                        _xmltvCache != null,
                        PerChannelEpgCacheCount);
                }
                config.LastChannelListHash = newHash;
                Plugin.Instance.SaveConfiguration();

                // Wipe M3U, EPG XML, and bulk XMLTV caches so the next request rebuilds
                // them against the fresh channel list. Note: we do NOT take _m3uLock /
                // _epgLock / _xmltvLock here because GetFilteredChannelsAsync may be
                // called from inside those locked sections; simple field assignment is
                // safe (volatile fields, time-stamp pair).
                _cachedM3U = null;
                _m3uCacheTime = DateTime.MinValue;
                _cachedEpgXml = null;
                _epgCacheTime = DateTime.MinValue;
                _xmltvCache = null;
                _xmltvCacheTime = DateTime.MinValue;
                _xmltvFailed = false;
                _xmltvFailedTime = DateTime.MinValue;
                _epgChannelIdByStreamId = new Dictionary<int, string>();

                // Selectively prune the per-channel EPG cache: keep entries whose
                // StreamId is still in the new channel set (preserves perf when only
                // a few channels were added/removed), drop entries for channels that
                // no longer exist (prevents stale EPG when a StreamId is recycled by
                // the provider for a different channel).
                var liveStreamIds = new HashSet<int>();
                foreach (var ch in allChannels) liveStreamIds.Add(ch.StreamId);
                lock (_perChannelEpgLock)
                {
                    int pruned = PruneStaleEpgEntries(_perChannelEpgCache, liveStreamIds);
                    if (pruned > 0)
                        _logger.Debug("Pruned {0} stale entries from per-channel EPG cache", pruned);
                }

                // Drop the tuner channel cache so Emby's next channel scan picks up
                // the fresh channel set immediately rather than after its cache TTL.
                M3uEditorTunerHost.Instance?.OnChannelListChanged();
            }
            else
            {
                _logger.Debug("Channel list unchanged (hash {0})", newHash.Substring(0, 8));
                if (diagnostics)
                {
                    _logger.Info("[livetv-diag] channel-hash unchanged hash={0} m3uCached={1} epgXmlCached={2} xmltvCached={3} perChannelEpgEntries={4}",
                        newHash.Substring(0, 8),
                        _cachedM3U != null,
                        _cachedEpgXml != null,
                        _xmltvCache != null,
                        PerChannelEpgCacheCount);
                }
            }

            _logger.Info("Fetched {0} Live TV channels", allChannels.Count);
            return allChannels;
        }

        /// <summary>
        /// Removes per-channel EPG entries whose StreamId is no longer in the live
        /// channel set. Pulled out so unit tests can exercise the drift-prune
        /// behaviour without standing up a full HTTP/Xtream environment. Returns
        /// the number of entries removed.
        /// </summary>
        internal static int PruneStaleEpgEntries(
            Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)> cache,
            HashSet<int> liveStreamIds)
        {
            if (cache == null || cache.Count == 0) return 0;
            if (liveStreamIds == null) liveStreamIds = new HashSet<int>();
            // Materialize the keys snapshot first so we can mutate the dictionary
            // afterwards without invalidating an active enumerator.
            var stale = cache.Keys.Where(key => !liveStreamIds.Contains(key)).ToList();
            foreach (var key in stale) cache.Remove(key);
            return stale.Count;
        }

        internal static List<LiveStreamInfo> FilterChannelsBySelectedCategories(
            List<LiveStreamInfo> channels,
            int[] selectedCategoryIds)
        {
            if (channels == null) return new List<LiveStreamInfo>();
            if (selectedCategoryIds == null || selectedCategoryIds.Length == 0) return channels;

            var selected = new HashSet<int>(selectedCategoryIds);
            return channels
                .Where(c => c.CategoryId.HasValue && selected.Contains(c.CategoryId.Value))
                .ToList();
        }

        private async Task<List<LiveStreamInfo>> FetchAllChannelsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_live_streams",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

            using (var httpClient = _httpClientFactory(30))
            {
                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                var channels = STJ.JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, JsonOptions)
                    ?? new List<LiveStreamInfo>();
                if (IsLiveTvDiagnosticsEnabled())
                {
                    _logger.Info("[livetv-diag] fetch-all-channels responseBytes={0} channels={1}",
                        Encoding.UTF8.GetByteCount(json), channels.Count);
                }
                return channels;
            }
        }


        private static string GenerateM3U(
            List<LiveStreamInfo> channels,
            PluginConfiguration config,
            Dictionary<int, string> categoryNames)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");

            var extinf = new StringBuilder();
            foreach (var channel in channels.OrderBy(c => c.ChannelNumberSortKey).ThenBy(c => c.StreamId))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var epgId = !string.IsNullOrEmpty(channel.EpgChannelId)
                    ? channel.EpgChannelId
                    : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                extinf.Clear();
                extinf.Append("#EXTINF:-1");
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-id=\"{0}\"", EscapeAttribute(epgId));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-name=\"{0}\"", EscapeAttribute(cleanName));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-chno=\"{0}\"", channel.DisplayChannelNumber);

                if (!string.IsNullOrEmpty(channel.StreamIcon))
                {
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-logo=\"{0}\"", EscapeAttribute(channel.StreamIcon));
                }

                if (channel.CategoryId.HasValue
                    && categoryNames.TryGetValue(channel.CategoryId.Value, out var groupTitle)
                    && !string.IsNullOrEmpty(groupTitle))
                {
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " group-title=\"{0}\"", EscapeAttribute(groupTitle));
                }

                extinf.AppendFormat(CultureInfo.InvariantCulture, ",{0}", cleanName);

                sb.Append(extinf).AppendLine();
                sb.AppendLine(BuildStreamUrl(config, channel));
            }

            return sb.ToString();
        }

        internal static string BuildStreamUrl(PluginConfiguration config, LiveStreamInfo channel)
        {
            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}/live/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, channel.StreamId, extension);
        }

        private async Task<string> GenerateXmltvAsync(List<LiveStreamInfo> channels, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<tv generator-info-name=\"m3u-editor for Emby\">");

            // Channel definitions
            foreach (var channel in channels.OrderBy(c => c.ChannelNumberSortKey).ThenBy(c => c.StreamId))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var channelId = !string.IsNullOrEmpty(channel.EpgChannelId)
                    ? channel.EpgChannelId
                    : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                sb.AppendFormat(CultureInfo.InvariantCulture, "  <channel id=\"{0}\">\n", EscapeXml(channelId));
                sb.AppendFormat(CultureInfo.InvariantCulture, "    <display-name>{0}</display-name>\n", EscapeXml(cleanName));
                if (!string.IsNullOrEmpty(channel.StreamIcon))
                {
                    var sanitizedIcon = Util.UrlValidator.SanitizeHttpUrl(channel.StreamIcon);
                    if (sanitizedIcon != null)
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture, "    <icon src=\"{0}\" />\n", EscapeXml(sanitizedIcon));
                    }
                }
                sb.AppendLine("  </channel>");
            }

            // Fetch EPG data if enabled
            if (config.EpgSource != EpgSourceMode.Disabled)
            {
                var epgData = await FetchEpgDataAsync(channels, config, cancellationToken).ConfigureAwait(false);

                foreach (var program in epgData.OrderBy(p => p.StartTimestamp))
                {
                    var startStr = FormatXmltvTime(program.StartTimestamp);
                    var stopStr = FormatXmltvTime(program.StopTimestamp);
                    var channelId = !string.IsNullOrEmpty(program.ChannelId)
                        ? program.ChannelId
                        : program.EpgId;

                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "  <programme start=\"{0}\" stop=\"{1}\" channel=\"{2}\">\n",
                        startStr, stopStr, EscapeXml(channelId));
                    var titleText = program.IsPlainText ? program.Title : DecodeBase64(program.Title);
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "    <title>{0}</title>\n", EscapeXml(titleText));
                    var desc = program.IsPlainText ? program.Description : DecodeBase64(program.Description);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "    <desc>{0}</desc>\n", EscapeXml(desc));
                    }
                    if (program.IsLive) sb.AppendLine("    <live />");
                    if (program.IsNew) sb.AppendLine("    <new />");
                    if (program.IsPreviouslyShown) sb.AppendLine("    <previously-shown />");
                    if (program.IsPremiere) sb.AppendLine("    <premiere />");
                    sb.AppendLine("  </programme>");
                }
            }

            sb.AppendLine("</tv>");
            return sb.ToString();
        }

        private async Task<List<EpgProgram>> FetchEpgDataAsync(
            List<LiveStreamInfo> channels,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var allPrograms = new List<EpgProgram>();
            var semaphore = new SemaphoreSlim(5);

            var now = DateTimeOffset.UtcNow;
            var endTime = now.AddDays(config.EpgDaysToFetch);

            try
            {
                var tasks = channels.Select(async channel =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var epgListings = await FetchEpgForChannelAsync(channel.StreamId, cancellationToken).ConfigureAwait(false);

                        if (epgListings == null || epgListings.Listings == null)
                        {
                            if (IsLiveTvDiagnosticsEnabled())
                            {
                                _logger.Info("[livetv-diag] epg-data stream={0} listings=null", channel.StreamId);
                            }
                            return new List<EpgProgram>();
                        }

                        // Warm the per-channel cache so GetProgramsInternal finds hits without re-fetching.
                        lock (_perChannelEpgLock)
                        {
                            _perChannelEpgCache[channel.StreamId] = (epgListings.Listings, DateTime.UtcNow);
                        }

                        var channelId = !string.IsNullOrEmpty(channel.EpgChannelId)
                            ? channel.EpgChannelId
                            : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                        foreach (var program in epgListings.Listings.Where(p => string.IsNullOrEmpty(p.ChannelId)))
                        {
                            program.ChannelId = channelId;
                        }

                        var nowUnix = now.ToUnixTimeSeconds();
                        var endUnix = endTime.ToUnixTimeSeconds();
                        var filtered = epgListings.Listings
                            .Where(p => p.StopTimestamp > nowUnix && p.StartTimestamp < endUnix)
                            .ToList();
                        if (IsLiveTvDiagnosticsEnabled())
                        {
                            _logger.Info("[livetv-diag] epg-data stream={0} rawPrograms={1} windowPrograms={2} epgId='{3}' name='{4}'",
                                channel.StreamId,
                                epgListings.Listings.Count,
                                filtered.Count,
                                channelId,
                                channel.Name ?? string.Empty);
                        }
                        return filtered;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.Debug("Failed to fetch EPG for channel {0}: {1}", channel.StreamId, ex.Message);
                        return new List<EpgProgram>();
                    }
                    catch (TaskCanceledException ex)
                    {
                        _logger.Debug("Failed to fetch EPG for channel {0}: {1}", channel.StreamId, ex.Message);
                        return new List<EpgProgram>();
                    }
                    catch (STJ.JsonException ex)
                    {
                        _logger.Debug("Failed to fetch EPG for channel {0}: {1}", channel.StreamId, ex.Message);
                        return new List<EpgProgram>();
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.Debug("Failed to fetch EPG for channel {0}: {1}", channel.StreamId, ex.Message);
                        return new List<EpgProgram>();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                {
                    allPrograms.AddRange(result);
                }
            }
            finally
            {
                semaphore.Dispose();
            }

            _logger.Info("Fetched {0} EPG programs for {1} channels", allPrograms.Count, channels.Count);
            return allPrograms;
        }

        /// <summary>
        /// Fetches EPG data for a single channel, with per-channel caching.
        /// Tries the bulk XMLTV endpoint first (preserves Live/Repeat/New/Premiere flags);
        /// falls back to per-channel JSON (get_simple_data_table) when XMLTV is unavailable.
        /// </summary>
        internal async Task<List<EpgProgram>> FetchEpgForChannelCachedAsync(int streamId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var cacheTtl = TimeSpan.FromMinutes(config.EpgCacheMinutes);

            // 1. Check per-channel cache (fastest path)
            lock (_perChannelEpgLock)
            {
                (List<EpgProgram> Programs, DateTime CacheTime) entry;
                if (_perChannelEpgCache.TryGetValue(streamId, out entry)
                    && DateTime.UtcNow - entry.CacheTime < cacheTtl)
                {
                    return entry.Programs;
                }
            }

            // 2. Try XMLTV bulk cache if available and fresh
            var xmltvCacheFresh = _xmltvCache != null && DateTime.UtcNow - _xmltvCacheTime < cacheTtl;
            if (xmltvCacheFresh)
            {
                var programs = PopulateFromXmltvCache(streamId);
                if (Diagnostics.IsEnabled)
                {
                    _logger.Info("[livetv-diag] stream={0} xmltv-cache-hit programs={1}", streamId, programs != null ? programs.Count : 0);
                }
                if (programs != null) return programs;
            }

            // 3. If XMLTV cache is stale and either hasn't failed or the failure is old enough
            //    to warrant a retry (once the TTL has elapsed since the failure), try fetching it.
            //    This ensures the plugin recovers transparently when the upstream EPG source
            //    comes back online, without requiring a manual "Refresh Cache" action.
            var xmltvFailedButRetryDue = _xmltvFailed && DateTime.UtcNow - _xmltvFailedTime >= TimeSpan.FromMinutes(5);
            if (!xmltvCacheFresh && (!_xmltvFailed || xmltvFailedButRetryDue))
            {
                var xmltvOk = await TryFetchXmltvEpgAsync(cancellationToken).ConfigureAwait(false);
                if (Diagnostics.IsEnabled)
                {
                    _logger.Info("[livetv-diag] stream={0} xmltv-refetch attempted ok={1} failedFlag={2}", streamId, xmltvOk, _xmltvFailed);
                }
                if (xmltvOk)
                {
                    var programs = PopulateFromXmltvCache(streamId);
                    if (programs != null) return programs;
                }
            }

            // 4. Fall back to per-channel JSON (get_simple_data_table) - m3u-editor Xtream output only.
            //    Custom URL mode does not fall back: if the user's URL failed, return empty so
            //    GetProgramsInternal shows a dummy placeholder rather than silently using a
            //    different source.
            if (Plugin.Instance.Configuration.EpgSource == EpgSourceMode.CustomUrl)
            {
                _logger.Debug("FetchEpgForChannelCachedAsync: custom URL failed, returning empty for stream {0}", streamId);
                return new List<EpgProgram>();
            }

            _logger.Debug("FetchEpgForChannelCachedAsync: using JSON fallback for stream {0}", streamId);
            var epgListings = await FetchEpgForChannelAsync(streamId, cancellationToken).ConfigureAwait(false);
            var jsonPrograms = epgListings?.Listings ?? new List<EpgProgram>();
            if (Diagnostics.IsEnabled)
            {
                _logger.Info("[livetv-diag] stream={0} json-fallback programs={1}", streamId, jsonPrograms.Count);
            }

            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache[streamId] = (jsonPrograms, DateTime.UtcNow);
            }

            return jsonPrograms;
        }

        /// <summary>
        /// Looks up programs for streamId in the XMLTV cache, populates _perChannelEpgCache, and returns them.
        /// Returns null if the channel is not present in the XMLTV data.
        /// </summary>
        private List<EpgProgram> PopulateFromXmltvCache(int streamId)
        {
            string epgChannelId;
            if (!_epgChannelIdByStreamId.TryGetValue(streamId, out epgChannelId))
            {
                epgChannelId = streamId.ToString(CultureInfo.InvariantCulture);
            }

            List<EpgProgram> xmltvPrograms;
            if (_xmltvCache == null || !_xmltvCache.TryGetValue(epgChannelId, out xmltvPrograms))
            {
                if (IsLiveTvDiagnosticsEnabled())
                {
                    _logger.Info("[livetv-diag] xmltv-cache-miss stream={0} epgId='{1}' xmltvCacheChannels={2}",
                        streamId, epgChannelId, _xmltvCache != null ? _xmltvCache.Count : 0);
                }
                return null;
            }

            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache[streamId] = (xmltvPrograms, DateTime.UtcNow);
            }

            return xmltvPrograms;
        }

        /// <summary>
        /// Attempts to fetch the full XMLTV EPG from /xmltv.php and populate _xmltvCache.
        /// Builds the stream_id ↔ epg_channel_id mapping from the channel list.
        /// Returns true on success, false if the fetch failed (sets _xmltvFailed).
        /// </summary>
        private async Task<bool> TryFetchXmltvEpgAsync(CancellationToken cancellationToken)
        {
            await _xmltvLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var config = Plugin.Instance.Configuration;
                var cacheTtl = TimeSpan.FromMinutes(config.EpgCacheMinutes);

                // Already fresh?
                if (_xmltvCache != null && DateTime.UtcNow - _xmltvCacheTime < cacheTtl)
                    return true;

                string url;
                if (config.EpgSource == EpgSourceMode.CustomUrl && !string.IsNullOrWhiteSpace(config.CustomEpgUrl))
                {
                    url = config.CustomEpgUrl;
                    _logger.Info("Fetching bulk XMLTV EPG from custom URL");
                }
                else
                {
                    url = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}/xmltv.php?username={1}&password={2}",
                        config.BaseUrl, config.Username, config.Password);
                    _logger.Info("Fetching bulk XMLTV EPG from {0}/xmltv.php", config.BaseUrl);
                }

                try
                {
                    // Build stream_id → epg_channel_id mapping from the channel list
                    var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                    var mapping = new Dictionary<int, string>(channels.Count);
                    foreach (var ch in channels)
                    {
                        var epgId = !string.IsNullOrEmpty(ch.EpgChannelId)
                            ? ch.EpgChannelId
                            : ch.StreamId.ToString(CultureInfo.InvariantCulture);
                        mapping[ch.StreamId] = epgId;
                    }
                    if (IsLiveTvDiagnosticsEnabled())
                    {
                        _logger.Info("[livetv-diag] xmltv-fetch channelMapping entries={0}", mapping.Count);
                    }

                    var now = DateTimeOffset.UtcNow;
                    var filterEndUnix = now.AddDays(config.EpgDaysToFetch).ToUnixTimeSeconds();

                    Dictionary<string, List<EpgProgram>> xmltvData;
                    using (var httpClient = _httpClientFactory(180))
                    {
                        using (var stream = await httpClient.GetStreamAsync(url).ConfigureAwait(false))
                        {
                            xmltvData = XmltvParser.Parse(stream, now.ToUnixTimeSeconds(), filterEndUnix);
                        }
                    }

                    _xmltvCache = xmltvData;
                    _epgChannelIdByStreamId = mapping;
                    _xmltvCacheTime = DateTime.UtcNow;
                    _xmltvFailed = false;
                    _xmltvFailedTime = DateTime.MinValue;

                    _logger.Info("XMLTV EPG fetched: {0} channels with program data", _xmltvCache.Count);
                    return true;
                }
                catch (Exception ex)
                {
                    _xmltvFailed = true;
                    _xmltvFailedTime = DateTime.UtcNow;
                    var isCustom = config.EpgSource == EpgSourceMode.CustomUrl;
                    _logger.Warn(isCustom
                        ? "Custom EPG URL fetch failed - no fallback: {0}"
                        : "XMLTV EPG fetch failed, will fall back to per-channel JSON: {0}", ex.Message);
                    return false;
                }
            }
            finally
            {
                _xmltvLock.Release();
            }
        }

        private async Task<EpgListings> FetchEpgForChannelAsync(int streamId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_simple_data_table&stream_id={3}",
                config.BaseUrl, config.Username, config.Password, streamId);

            using (var httpClient = _httpClientFactory(10))
            {
                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<EpgListings>(json, JsonOptions);
            }
        }

        private static bool IsLiveTvDiagnosticsEnabled()
        {
            return Diagnostics.IsEnabled;
        }

        private void LogLiveTvChannelDiagnostics(
            List<LiveStreamInfo> channels,
            PluginConfiguration config,
            string phase)
        {
            var total = channels?.Count ?? 0;
            var withEpgId = channels?.Count(c => !string.IsNullOrEmpty(c.EpgChannelId)) ?? 0;
            var withIcon = channels?.Count(c => !string.IsNullOrEmpty(c.StreamIcon)) ?? 0;
            var withValidIcon = channels?.Count(c => Util.UrlValidator.SanitizeHttpUrl(c.StreamIcon) != null) ?? 0;
            var withCategory = channels?.Count(c => c.CategoryId.HasValue) ?? 0;
            var withStats = channels?.Count(c => c.StreamStats != null) ?? 0;
            var adult = channels?.Count(c => c.IsAdultChannel) ?? 0;

            _logger.Info("[livetv-diag] channel-summary phase={0} total={1} epgIds={2} icons={3} validIcons={4} categories={5} stats={6} adult={7}",
                phase,
                total,
                withEpgId,
                withIcon,
                withValidIcon,
                withCategory,
                withStats,
                adult);

            if (channels == null || channels.Count == 0)
            {
                return;
            }

            foreach (var channel in channels.OrderBy(c => c.ChannelNumberSortKey).ThenBy(c => c.StreamId).Take(20))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);
                var sanitizedIcon = Util.UrlValidator.SanitizeHttpUrl(channel.StreamIcon);

                _logger.Info("[livetv-diag] channel-sample phase={0} stream={1} num={2} name='{3}' cleanName='{4}' epgId='{5}' categoryId={6} icon={7} validIcon={8} stats={9}",
                    phase,
                    channel.StreamId,
                    channel.DisplayChannelNumber,
                    channel.Name ?? string.Empty,
                    cleanName ?? string.Empty,
                    channel.EpgChannelId ?? string.Empty,
                    channel.CategoryId.HasValue ? channel.CategoryId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    !string.IsNullOrEmpty(channel.StreamIcon),
                    sanitizedIcon != null,
                    channel.StreamStats != null);
            }
        }

        private static string FormatXmltvTime(long unixTimestamp)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
            return dt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        internal static string DecodeBase64(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return value;
            }
        }

        private static string EscapeAttribute(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\"", "&quot;")
                .Replace("&", "&amp;");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _m3uLock.Dispose();
                    _epgLock.Dispose();
                    _xmltvLock.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
        }
    }
}
