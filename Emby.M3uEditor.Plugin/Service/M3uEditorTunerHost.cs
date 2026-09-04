using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client.Models;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using STJ = System.Text.Json;
using Emby.M3uEditor.Plugin.Util;

#pragma warning disable CS0612 // SupportsProbing and AnalyzeDurationMs are obsolete but still functional
namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class ChannelArtworkCleanupResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int TotalLibraryChannels { get; set; }
        public int MatchedChannels { get; set; }
        public int ClearedChannels { get; set; }
        public int AlreadyCleanChannels { get; set; }
    }

    public class M3uEditorTunerHost : BaseTunerHost
    {
        internal const string TunerType = "m3u-editor";
        internal const string StableTunerId = "b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private static volatile M3uEditorTunerHost _instance;

        private readonly IServerApplicationHost _applicationHost;

        private volatile Dictionary<int, StreamStatsInfo> _streamStats = new Dictionary<int, StreamStatsInfo>();
        private List<ChannelInfo> _cachedChannels;
        private DateTime _cacheTime = DateTime.MinValue;

        public int CachedChannelCount => _cachedChannels?.Count ?? 0;

        /// <summary>
        /// Snapshot of the current probe-data cache, exposed for diagnostics
        /// (e.g. the "Check Probe Data Coverage" button in config). Returns
        /// the count of channels that currently carry stream stats.
        /// </summary>
        public int CachedStreamStatsCount => _streamStats?.Count ?? 0;

        /// <summary>Channels whose stream_stats came from the m3u-editor backend payload.</summary>
        public int BackendStreamStatsCount { get; private set; }

        public M3uEditorTunerHost(IServerApplicationHost applicationHost)
            : base(applicationHost)
        {
            _instance = this;
            _applicationHost = applicationHost;
        }

        public static M3uEditorTunerHost Instance => _instance;

        public IServerApplicationHost ApplicationHost => _applicationHost;

        public override string Name => "m3u-editor for Emby";
        public override string Type => TunerType;
        public override bool IsSupported => true;
        public override string SetupUrl => null;
        protected override bool UseTunerHostIdAsPrefix => false;

        public override TunerHostInfo GetDefaultConfiguration()
        {
            return new TunerHostInfo
            {
                Id = StableTunerId,
                Type = Type,
                TunerCount = 1
            };
        }

        internal static bool ReconcileTunerHosts(LiveTvOptions options, bool enabled)
        {
            if (options == null)
            {
                return false;
            }

            var original = options.TunerHosts ?? new TunerHostInfo[0];
            var pluginTuners = original.Where(tuner => tuner != null &&
                (string.Equals(tuner.Type, TunerType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tuner.Id, StableTunerId, StringComparison.OrdinalIgnoreCase))).ToList();
            var selected = pluginTuners.FirstOrDefault(tuner => !string.IsNullOrWhiteSpace(tuner.Id))
                ?? pluginTuners.FirstOrDefault();
            var reconciled = new List<TunerHostInfo>();
            var selectedAdded = false;
            var changed = options.TunerHosts == null;

            foreach (var tuner in original)
            {
                if (tuner == null)
                {
                    changed = true;
                    continue;
                }

                var isPluginTuner = string.Equals(tuner.Type, TunerType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tuner.Id, StableTunerId, StringComparison.OrdinalIgnoreCase);
                if (!isPluginTuner)
                {
                    reconciled.Add(tuner);
                    continue;
                }

                if (!enabled || !ReferenceEquals(tuner, selected) || selectedAdded)
                {
                    changed = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tuner.Id))
                {
                    tuner.Id = StableTunerId;
                    changed = true;
                }
                if (!string.Equals(tuner.Type, TunerType, StringComparison.OrdinalIgnoreCase))
                {
                    tuner.Type = TunerType;
                    changed = true;
                }

                if (tuner.TunerCount < 1)
                {
                    tuner.TunerCount = 1;
                    changed = true;
                }

                reconciled.Add(tuner);
                selectedAdded = true;
            }

            if (enabled && !selectedAdded)
            {
                reconciled.Add(new TunerHostInfo
                {
                    Id = StableTunerId,
                    Type = TunerType,
                    TunerCount = 1
                });
                changed = true;
            }

            if (changed || reconciled.Count != original.Length)
            {
                options.TunerHosts = reconciled.ToArray();
                return true;
            }

            return false;
        }

        internal static void ReconcileConfiguredTunerHost(
            IApplicationHost applicationHost,
            bool enabled,
            ILogger logger)
        {
            try
            {
                var configManager = applicationHost.Resolve<IConfigurationManager>();
                var liveTvOptions = configManager.GetConfiguration("livetv") as LiveTvOptions;
                if (liveTvOptions != null && ReconcileTunerHosts(liveTvOptions, enabled))
                {
                    configManager.SaveConfiguration("livetv", liveTvOptions);
                    logger?.Info("Reconciled the stable m3u-editor tuner configuration.");
                }
            }
            catch (InvalidOperationException ex)
            {
                logger?.Warn("m3u-editor tuner reconciliation was unavailable: {0}", ex.Message);
            }
            catch (ArgumentException ex)
            {
                logger?.Warn("m3u-editor tuner reconciliation was unavailable: {0}", ex.Message);
            }
        }

        public override bool SupportsGuideData(TunerHostInfo tuner)
        {
            return Plugin.Instance.Configuration.EpgSource != EpgSourceMode.Disabled;
        }

        protected override async Task<List<ProgramInfo>> GetProgramsInternal(
            TunerHostInfo tuner, string tunerChannelId,
            DateTimeOffset startDateUtc, DateTimeOffset endDateUtc,
            CancellationToken cancellationToken)
        {
            if (!int.TryParse(tunerChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out var streamId))
            {
                Logger.Warn("GetProgramsInternal: cannot parse tunerChannelId '{0}'", tunerChannelId);
                if (IsLiveTvDiagnosticsEnabled())
                {
                    Logger.Info("[livetv-diag] GetProgramsInternal rejected channelId='{0}' because it is not mapped and not numeric", tunerChannelId);
                }
                return new List<ProgramInfo>();
            }

            var liveTvService = Plugin.Instance.LiveTvService;
            List<Client.Models.EpgProgram> programs;
            try
            {
                programs = await liveTvService.FetchEpgForChannelCachedAsync(streamId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn("GetProgramsInternal: failed to fetch EPG for stream {0}: {1}", streamId, ex.Message);
                programs = new List<EpgProgram>();
            }

            var startUnix = startDateUtc.ToUnixTimeSeconds();
            var endUnix = endDateUtc.ToUnixTimeSeconds();

            const long MinTimestamp = 946684800L;   // 2000-01-01
            const long MaxTimestamp = 4102444800L;  // 2100-01-01

            var result = new List<ProgramInfo>();
            foreach (var p in programs)
            {
                if (p.StopTimestamp <= startUnix || p.StartTimestamp >= endUnix)
                {
                    continue;
                }

                if (p.StartTimestamp < MinTimestamp || p.StartTimestamp > MaxTimestamp
                    || p.StopTimestamp < MinTimestamp || p.StopTimestamp > MaxTimestamp)
                {
                    Logger.Debug("GetProgramsInternal: skipping program with out-of-range timestamps " +
                        "(start={0}, stop={1}) on channel {2}", p.StartTimestamp, p.StopTimestamp, streamId);
                    continue;
                }

                // Skip zero-duration or reversed programs - Emby's GetProgram throws when
                // EndDate <= StartDate, which causes the entire channel to be rejected.
                if (p.StopTimestamp <= p.StartTimestamp)
                {
                    Logger.Warn("GetProgramsInternal: skipping zero-duration or reversed program " +
                        "(start={0}, stop={1}, title='{2}') on channel {3}",
                        p.StartTimestamp, p.StopTimestamp,
                        p.IsPlainText ? (p.Title ?? string.Empty) : "(base64)", streamId);
                    continue;
                }

                var title = p.IsPlainText ? p.Title : LiveTvService.DecodeBase64(p.Title);
                var description = p.IsPlainText ? p.Description : LiveTvService.DecodeBase64(p.Description);
                try
                {
                    result.Add(BuildProgramInfo(p, streamId, tunerChannelId, title, description));
                }
                catch (Exception ex)
                {
                    Logger.Warn("GetProgramsInternal: skipping program on channel {0} " +
                        "(start={1}, stop={2}, title='{3}'): {4}",
                        streamId, p.StartTimestamp, p.StopTimestamp,
                        p.IsPlainText ? p.Title : "(base64)", ex.Message);
                }
            }

            // No EPG data - return a dummy entry spanning the requested window so the channel
            // row stays visible and clickable in the guide (matches M3U tuner behaviour).
            if (result.Count == 0)
            {
                if (IsLiveTvDiagnosticsEnabled())
                {
                    Logger.Info("[livetv-diag] stream={0} returned 0 programs in window {1:u} .. {2:u}", streamId, startDateUtc.UtcDateTime, endDateUtc.UtcDateTime);
                }
                var channelName = _cachedChannels?.Find(c => c.TunerChannelId == tunerChannelId)?.Name;
                if (!string.IsNullOrEmpty(channelName))
                {
                    result.Add(new ProgramInfo
                    {
                        Id = string.Format(CultureInfo.InvariantCulture, "xtream_dummy_{0}_{1}", streamId, startDateUtc.ToUnixTimeSeconds()),
                        ChannelId = tunerChannelId,
                        StartDate = startDateUtc.UtcDateTime,
                        EndDate = endDateUtc.UtcDateTime,
                        Name = channelName,
                        Genres = new List<string>(),
                    });
                    Logger.Debug("GetProgramsInternal: no EPG for channel {0}, returning dummy entry", streamId);
                }
            }

            if (result.Count > 0 && result.Count <= 15)
            {
                // Low program count - log first entry to help diagnose EPG quality issues.
                var first = result[0];
                Logger.Debug("GetProgramsInternal: channel {0} first program: start={1:u}, end={2:u}, name='{3}'",
                    streamId, first.StartDate, first.EndDate, first.Name);
            }

            Logger.Debug("GetProgramsInternal: returning {0} programs for channel {1}", result.Count, streamId);
            if (IsLiveTvDiagnosticsEnabled())
            {
                Logger.Info("[livetv-diag] stream={0} returning {1} programs for requested window", streamId, result.Count);
            }
            return result;
        }

        /// <summary>
        /// Converts a single <see cref="EpgProgram"/> into a <see cref="ProgramInfo"/> ready for
        /// Emby. Extracted as an internal static so it can be unit-tested without Emby DI.
        /// </summary>
        internal static ProgramInfo BuildProgramInfo(
            EpgProgram p, int streamId, string tunerChannelId,
            string title, string description)
        {
            var cats = p.Categories;
            var isMovie = cats != null && cats.Exists(c =>
                c.IndexOf("movie", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("film", System.StringComparison.OrdinalIgnoreCase) >= 0);
            var isSports = cats != null && cats.Exists(c =>
                c.IndexOf("sport", System.StringComparison.OrdinalIgnoreCase) >= 0);
            var isSeries = !isMovie && !isSports;

            return new ProgramInfo
            {
                Id = string.Format(CultureInfo.InvariantCulture, "xtream_epg_{0}_{1}", streamId, p.StartTimestamp),
                ChannelId = tunerChannelId,
                StartDate = DateTimeOffset.FromUnixTimeSeconds(p.StartTimestamp).UtcDateTime,
                EndDate = DateTimeOffset.FromUnixTimeSeconds(p.StopTimestamp).UtcDateTime,
                Name = string.IsNullOrEmpty(title) ? "Unknown" : title,
                Overview = string.IsNullOrEmpty(description) ? null : description,
                EpisodeTitle = string.IsNullOrEmpty(p.SubTitle) ? null : p.SubTitle,
                IsLive = p.IsLive,
                IsRepeat = p.IsPreviouslyShown,
                IsPremiere = p.IsNew || p.IsPremiere,
                ImageUrl = Util.UrlValidator.SanitizeHttpUrl(p.ImageUrl),
                ImageWidth = p.ImageWidth > 0 ? p.ImageWidth : 0,
                ImageHeight = p.ImageHeight > 0 ? p.ImageHeight : 0,
                BackdropImageUrl = Util.UrlValidator.SanitizeHttpUrl(p.BackdropImageUrl),
                ThumbImageUrl = Util.UrlValidator.SanitizeHttpUrl(p.ThumbImageUrl),
                LogoImageUrl = Util.UrlValidator.SanitizeHttpUrl(p.LogoImageUrl),
                Genres = cats ?? new List<string>(),
                IsSports = isSports,
                IsNews = cats != null && cats.Exists(c =>
                    c.IndexOf("news", System.StringComparison.OrdinalIgnoreCase) >= 0),
                IsMovie = isMovie,
                IsKids = cats != null && cats.Exists(c =>
                    c.IndexOf("children", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.IndexOf("kids", System.StringComparison.OrdinalIgnoreCase) >= 0),
                IsSeries = isSeries,
                SeriesId = isSeries && !string.IsNullOrEmpty(title) ? title.ToLowerInvariant() : null,
            };
        }

        internal static void ApplyChannelLogoVariants(ChannelInfo channelInfo, string imageUrl, bool useM3uLogoForAllChannelImages)
        {
            if (channelInfo == null)
            {
                return;
            }

            channelInfo.ImageUrl = imageUrl;

            if (useM3uLogoForAllChannelImages && !string.IsNullOrEmpty(imageUrl))
            {
                channelInfo.LightLogoImageUrl = imageUrl;
                channelInfo.LightColorLogoImageUrl = imageUrl;
            }
            else
            {
                channelInfo.LightLogoImageUrl = null;
                channelInfo.LightColorLogoImageUrl = null;
            }
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(
            TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            if (!config.EnableLiveTv)
            {
                return new List<ChannelInfo>();
            }

            // Return cached channels if available and not expired
            if (_cachedChannels != null && DateTime.UtcNow - _cacheTime < CacheDuration)
            {
                Logger.Debug("Returning cached channel list ({0} channels)", _cachedChannels.Count);
                return _cachedChannels;
            }

            Logger.Info("Fetching channels from Xtream API");

            var liveTvService = Plugin.Instance.LiveTvService;
            // Both fetches run concurrently; each handles its own errors internally.
            async Task<List<Client.Models.LiveStreamInfo>> channelsFetch()
            {
                try
                {
                    return await liveTvService.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is TaskCanceledException) && !(ex is OperationCanceledException))
                {
                    Logger.Warn("LiveTvService channel fetch failed, falling back to direct API: {0}", ex.Message);
                    return await FetchAllChannelsDirectAsync(config).ConfigureAwait(false);
                }
            }

            async Task<Dictionary<int, string>> categoriesFetch()
            {
                try
                {
                    var cats = await liveTvService.GetLiveCategoriesAsync(cancellationToken).ConfigureAwait(false);
                    Logger.Debug("Fetched {0} live categories for guide chips", cats.Count);
                    return cats.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Failed to fetch live categories for guide chips: {0}", ex.Message);
                    return new Dictionary<int, string>();
                }
            }

            var channelsTask = channelsFetch();
            var categoriesTask = categoriesFetch();

            await Task.WhenAll(channelsTask, categoriesTask).ConfigureAwait(false);

            var channels = channelsTask.Result;
            var categoryMap = categoriesTask.Result;

            var newStats = CollectBackendStreamStats(channels);

            var result = channels.Select(channel =>
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var streamIdStr = channel.StreamId.ToString(CultureInfo.InvariantCulture);

                string[] tags = null;
                if (channel.CategoryId.HasValue
                    && categoryMap.TryGetValue(channel.CategoryId.Value, out var groupTitle)
                    && !string.IsNullOrEmpty(groupTitle))
                {
                    tags = new[] { groupTitle };
                }

                var channelInfo = new ChannelInfo
                {
                    Id = CreateEmbyChannelId(tuner, streamIdStr),
                    TunerChannelId = streamIdStr,
                    Name = cleanName,
                    Number = channel.DisplayChannelNumber,
                    ChannelType = ChannelType.TV,
                    TunerHostId = tuner.Id,
                    Tags = tags,
                };

                ApplyChannelLogoVariants(
                    channelInfo,
                    Util.UrlValidator.SanitizeHttpUrl(channel.StreamIcon),
                    config.UseM3uLogoForAllChannelImages);

                return channelInfo;
            }).ToList();

            _streamStats = newStats;
            BackendStreamStatsCount = newStats.Count;
            _cachedChannels = result;
            _cacheTime = DateTime.UtcNow;
            Logger.Info("Channel list cached with {0} channels ({1} with backend stream stats)",
                result.Count, newStats.Count);

            return result;
        }

        internal static Dictionary<int, StreamStatsInfo> CollectBackendStreamStats(
            IEnumerable<Client.Models.LiveStreamInfo> channels)
        {
            var stats = new Dictionary<int, StreamStatsInfo>();
            if (channels == null)
            {
                return stats;
            }

            foreach (var channel in channels.Where(channel => channel.StreamStats != null))
            {
                stats[channel.StreamId] = channel.StreamStats;
            }

            return stats;
        }

        private static async Task<List<Client.Models.LiveStreamInfo>> FetchAllChannelsDirectAsync(PluginConfiguration config)
        {
            using (var httpClient = Plugin.CreateHttpClient(30))
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_live_streams",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<List<Client.Models.LiveStreamInfo>>(json, JsonOptions)
                    ?? new List<Client.Models.LiveStreamInfo>();
            }
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
            TunerHostInfo tuner, MediaBrowser.Controller.Entities.BaseItem dbChannel,
            ChannelInfo tunerChannel, CancellationToken cancellationToken)
        {
            if (!TryResolveStreamId(tunerChannel, out int streamId))
            {
                return Task.FromResult(new List<MediaSourceInfo>());
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var config = Plugin.Instance.Configuration;
            var streamUrl = BuildStreamUrl(config, streamId);
            if (IsLiveTvDiagnosticsEnabled())
            {
                Logger.Info("[stream-timing] ch={0} BuildUrl={1}ms", tunerChannel.Name, sw.ElapsedMilliseconds);
            }
            sw.Restart();

            _streamStats.TryGetValue(streamId, out var stats);

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, config.HttpUserAgent);
            if (IsLiveTvDiagnosticsEnabled())
            {
                Logger.Info("[stream-timing] ch={0} CreateMediaSource={1}ms hasStats={2}", tunerChannel.Name, sw.ElapsedMilliseconds, stats != null);
            }

            return Task.FromResult(new List<MediaSourceInfo> { mediaSource });
        }

        protected override Task<ILiveStream> GetChannelStream(
            TunerHostInfo tuner, MediaBrowser.Controller.Entities.BaseItem dbChannel,
            ChannelInfo tunerChannel, string mediaSourceId,
            CancellationToken cancellationToken)
        {
            if (!TryResolveStreamId(tunerChannel, out int streamId))
            {
                throw new System.IO.FileNotFoundException(
                    string.Format("Channel {0} not found in m3u-editor tuner", tunerChannel?.Id));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var config = Plugin.Instance.Configuration;
            var streamUrl = BuildStreamUrl(config, streamId);
            _streamStats.TryGetValue(streamId, out var stats);
            if (IsLiveTvDiagnosticsEnabled())
            {
                Logger.Info("[stream-timing] ch={0} BuildUrl={1}ms", tunerChannel.Name, sw.ElapsedMilliseconds);
            }
            sw.Restart();

            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, config.HttpUserAgent);
            if (IsLiveTvDiagnosticsEnabled())
            {
                Logger.Info("[stream-timing] ch={0} CreateMediaSource={1}ms hasStats={2}", tunerChannel.Name, sw.ElapsedMilliseconds, stats != null);
            }

            var httpClient = Plugin.CreateHttpClient();
            ILiveStream liveStream = new M3uEditorLiveStream(mediaSource, tuner.Id, httpClient, Logger);

            Logger.Info("Opening live stream for channel {0} (stream {1})",
                tunerChannel.Name ?? tunerChannel.Id, streamId);

            return Task.FromResult(liveStream);
        }

        /// <summary>
        /// Lightweight cache invalidation invoked by <see cref="LiveTvService"/> when
        /// the upstream Xtream channel list has changed (rename / add / delete).
        /// Drops the tuner channel cache while preserving stream stats for in-flight playback.
        /// Safe to call concurrently - assignment of these fields is atomic and
        /// they are only read by GetChannelsInternal under its own cache check.
        /// </summary>
        public void OnChannelListChanged()
        {
            _cachedChannels = null;
            _cacheTime = DateTime.MinValue;
            // Logger?. handles the early-init / unit-test case (Logger may be null)
            // and ILogger.Info itself does not throw, so no try/catch is needed.
            Logger?.Info("Tuner channel cache invalidated due to upstream channel-list change");
        }

        /// <summary>
        /// User-triggered cache invalidation. The next channel scan repopulates
        /// both channels and stream stats from m3u-editor.
        /// </summary>
        public new void ClearCaches()
        {
            _cachedChannels = null;
            _cacheTime = DateTime.MinValue;
            _streamStats = new Dictionary<int, StreamStatsInfo>();
            BackendStreamStatsCount = 0;
            // Logger?. covers the case where Logger has not been wired up yet
            // (early init / unit tests). ILogger.Info itself does not throw.
            Logger?.Info("m3u-editor tuner caches cleared");
        }

        internal ChannelArtworkCleanupResult ClearCachedChannelArtwork()
        {
            var result = new ChannelArtworkCleanupResult();
            var cachedChannels = _cachedChannels;
            if (_applicationHost == null || cachedChannels == null || cachedChannels.Count == 0)
            {
                result.Message = "No cached m3u-editor channels are available.";
                return result;
            }

            try
            {
                var libraryManager = _applicationHost.Resolve<ILibraryManager>();
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var channelType = assemblies
                    .Select(assembly => assembly.GetType("MediaBrowser.Controller.LiveTv.LiveTvChannel"))
                    .FirstOrDefault(type => type != null);
                var queryType = assemblies
                    .Select(assembly => assembly.GetType("MediaBrowser.Controller.Entities.InternalItemsQuery"))
                    .FirstOrDefault(type => type != null);
                if (channelType == null || queryType == null)
                {
                    result.Message = "Emby Live TV channel metadata is unavailable.";
                    return result;
                }

                var query = Activator.CreateInstance(queryType);
                queryType.GetProperty("IncludeItemTypes")?.SetValue(query, new[] { "LiveTvChannel" });
                var getItems = typeof(ILibraryManager).GetMethods()
                    .FirstOrDefault(method => method.Name == "GetItemList" &&
                        method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType == queryType);
                var updateItem = typeof(ILibraryManager).GetMethods()
                    .FirstOrDefault(method => method.Name == "UpdateItem" &&
                        method.GetParameters().Length == 3);
                var items = getItems?.Invoke(libraryManager, new[] { query }) as System.Collections.IEnumerable;
                if (items == null || updateItem == null)
                {
                    result.Message = "Emby Live TV channel updates are unavailable.";
                    return result;
                }

                foreach (var item in items)
                {
                    try
                    {
                        result.TotalLibraryChannels++;
                        var itemType = item.GetType();
                        var name = itemType.GetProperty("Name")?.GetValue(item) as string;
                        var number = itemType.GetProperty("Number")?.GetValue(item) as string;
                        var tunerChannelId = itemType.GetProperty("TunerChannelId")?.GetValue(item) as string;
                        if (!IsOwnedChannel(cachedChannels, tunerChannelId, number, name))
                        {
                            continue;
                        }

                        result.MatchedChannels++;
                        var imagesProperty = itemType.GetProperty("ImageInfos");
                        var images = imagesProperty?.GetValue(item) as Array;
                        if (images == null || images.Length == 0)
                        {
                            result.AlreadyCleanChannels++;
                            continue;
                        }

                        imagesProperty.SetValue(item, Array.CreateInstance(images.GetType().GetElementType(), 0));
                        updateItem.Invoke(libraryManager, new object[] { item, null, 4 });
                        result.ClearedChannels++;
                    }
                    catch (Exception ex)
                    {
                        Logger?.Warn("Channel artwork cleanup skipped one item: {0}", ex.Message);
                    }
                }

                result.Success = true;
                result.Message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Cleared cached artwork for {0} of {1} matched m3u-editor channels.",
                    result.ClearedChannels,
                    result.MatchedChannels);
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                Logger?.Warn("Channel artwork cleanup failed: {0}", ex.Message);
            }

            return result;
        }

        internal static bool IsOwnedChannel(
            IEnumerable<ChannelInfo> cachedChannels,
            string tunerChannelId,
            string number,
            string name)
        {
            return cachedChannels != null && cachedChannels.Any(channel =>
                (!string.IsNullOrEmpty(tunerChannelId) &&
                 string.Equals(channel.TunerChannelId, tunerChannelId, StringComparison.Ordinal)) ||
                (!string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(name) &&
                 string.Equals(channel.Number, number, StringComparison.Ordinal) &&
                 string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Locates the configured m3u-editor tuner host inside Emby's LiveTV
        /// configuration. Returns <c>null</c> when no tuner is registered
        /// (e.g. fresh install or the user removed it). Centralised so callers
        /// that need to "trigger a channel load if nothing is cached" do not
        /// each duplicate the LiveTvOptions lookup.
        /// </summary>
        private TunerHostInfo FindRegisteredTunerHost(string callerLogContext)
        {
            try
            {
                var configManager = _applicationHost.Resolve<IConfigurationManager>();
                var liveTvOptions = configManager.GetConfiguration("livetv") as LiveTvOptions;
                if (liveTvOptions?.TunerHosts == null)
                {
                    return null;
                }

                var m3uEditor = liveTvOptions.TunerHosts.FirstOrDefault(t =>
                    string.Equals(t.Type, TunerType, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(t.Id));

                if (m3uEditor == null && !string.IsNullOrEmpty(callerLogContext))
                {
                    Logger.Info("{0}: no m3u-editor tuner host found in LiveTvOptions", callerLogContext);
                }

                return m3uEditor;
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warn("FindRegisteredTunerHost ({0}) failed: {1}", callerLogContext, ex.Message);
                return null;
            }
            catch (ArgumentException ex)
            {
                Logger.Warn("FindRegisteredTunerHost ({0}) failed: {1}", callerLogContext, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Best-effort: makes sure <see cref="_cachedChannels"/> is populated.
        /// Used by diagnostics endpoints that would otherwise have to tell the
        /// user "go to Live TV and refresh first" when the cache simply has not
        /// been warmed yet (e.g. immediately after restart). Returns <c>true</c>
        /// when channels are available afterwards.
        /// </summary>
        public async Task<bool> EnsureChannelsLoadedAsync(CancellationToken cancellationToken)
        {
            var existing = _cachedChannels;
            if (existing != null && existing.Count > 0)
            {
                return true;
            }

            var tuner = FindRegisteredTunerHost("EnsureChannelsLoadedAsync");
            if (tuner == null)
            {
                return false;
            }

            try
            {
                var loaded = await GetChannelsInternal(tuner, cancellationToken).ConfigureAwait(false);
                return loaded != null && loaded.Count > 0;
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warn("EnsureChannelsLoadedAsync: load failed: {0}", ex.Message);
            }
            catch (ArgumentException ex)
            {
                Logger.Warn("EnsureChannelsLoadedAsync: load failed: {0}", ex.Message);
            }
            catch (HttpRequestException ex)
            {
                Logger.Warn("EnsureChannelsLoadedAsync: load failed: {0}", ex.Message);
            }
            catch (TaskCanceledException ex)
            {
                Logger.Warn("EnsureChannelsLoadedAsync: load timed out: {0}", ex.Message);
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warn("EnsureChannelsLoadedAsync: load canceled: {0}", ex.Message);
            }

            return false;
        }

        private bool TryResolveStreamId(ChannelInfo tunerChannel, out int streamId)
        {
            streamId = 0;
            if (tunerChannel == null) return false;

            var id = tunerChannel.TunerChannelId ?? tunerChannel.Id;

            return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
        }

        internal static string BuildStreamUrl(PluginConfiguration config, int streamId)
        {
            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase)
                ? "ts" : "m3u8";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}/live/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, streamId, extension);
        }

        internal MediaSourceInfo CreateMediaSourceInfo(
            int streamId, string streamUrl, StreamStatsInfo stats,
            string userAgent = null)
        {
            var sourceId = "xtream_live_" + streamId.ToString(CultureInfo.InvariantCulture);

            // Audio-only channel: stats are present but no video_codec exists.
            // The normal hasStats gate (VideoCodec != null) would fall through to the dummy
            // H.264 fallback, which causes Emby to expect a video stream that isn't there.
            bool isAudioOnly = stats != null
                && stats.VideoCodec == null
                && !string.IsNullOrEmpty(stats.AudioCodec);

            bool hasStats = stats?.VideoCodec != null || isAudioOnly;

            var audioCodecLower = hasStats && !string.IsNullOrEmpty(stats?.AudioCodec)
                ? stats.AudioCodec.ToLowerInvariant() : null;

            var mediaSource = new MediaSourceInfo
            {
                Id = sourceId,
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                Container = "mpegts",
                SupportsProbing = !hasStats,
                IsRemote = true,
                IsInfiniteStream = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                AnalyzeDurationMs = hasStats ? 0 : 500,
                RequiresOpening = true,
                RequiresClosing = true,
                WallClockStart = DateTime.UtcNow,
            };

            if (!string.IsNullOrEmpty(userAgent))
            {
                mediaSource.RequiredHttpHeaders = new Dictionary<string, string>
                {
                    ["User-Agent"] = userAgent
                };
            }

            if (hasStats)
            {
                var mediaStreams = new List<MediaStream>();

                if (!isAudioOnly)
                {
                    // Parse resolution (e.g. "1920x1080")
                    int width = 0, height = 0;
                    if (!string.IsNullOrEmpty(stats.Resolution))
                    {
                        var parts = stats.Resolution.Split('x');
                        if (parts.Length == 2)
                        {
                            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width);
                            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
                        }
                    }

                    var videoCodec = MapVideoCodec(stats.VideoCodec);

                    var videoStream = new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        Codec = videoCodec,
                        IsInterlaced = false,
                        PixelFormat = "yuv420p",
                    };

                    if (width > 0) videoStream.Width = width;
                    if (height > 0) videoStream.Height = height;
                    videoStream.DisplayTitle = height > 0
                        ? $"{height}p {videoCodec.ToUpperInvariant()}"
                        : videoCodec.ToUpperInvariant();
                    if (stats.SourceFps.HasValue)
                    {
                        videoStream.RealFrameRate = (float)stats.SourceFps.Value;
                        videoStream.AverageFrameRate = (float)stats.SourceFps.Value;
                    }
                    if (stats.Bitrate.HasValue) videoStream.BitRate = (int)(stats.Bitrate.Value * 1000);
                    if (!string.IsNullOrEmpty(stats.VideoProfile))
                        videoStream.Profile = stats.VideoProfile;
                    if (stats.VideoLevel.HasValue)
                        videoStream.Level = (double)stats.VideoLevel.Value;
                    if (stats.VideoBitDepth.HasValue)
                        videoStream.BitDepth = stats.VideoBitDepth.Value;
                    if (stats.VideoRefFrames.HasValue)
                        videoStream.RefFrames = stats.VideoRefFrames.Value;

                    mediaStreams.Add(videoStream);
                }

                // Prefer audio_channels from stream_stats when present. Fall back to
                // codec-based broadcast defaults when the field is absent.
                int? audioChannels = null;
                string channelLayout = null;
                if (!string.IsNullOrEmpty(stats.AudioChannels))
                {
                    audioChannels = ParseAudioChannelCount(stats.AudioChannels);
                    channelLayout = stats.AudioChannels.Contains(".")
                        ? stats.AudioChannels  // e.g. "5.1", "7.1"
                        : stats.AudioChannels; // e.g. "stereo", "mono"
                }
                else if (audioCodecLower == "ac3" || audioCodecLower == "eac3")
                {
                    audioChannels = 6;
                    channelLayout = "5.1(side)";
                }
                else if (audioCodecLower == "mp2" || audioCodecLower == "mp1")
                {
                    audioChannels = 2;
                    channelLayout = "stereo";
                }

                var audioStream = new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = isAudioOnly ? 0 : 1,
                    Codec = audioCodecLower ?? "aac",
                    Channels = audioChannels,
                    ChannelLayout = channelLayout,
                    SampleRate = stats.SampleRate,
                };
                // For audio-only channels set the bitrate so Emby doesn't assume its
                // 40 Mbps live-TV default and force a transcode at low quality settings.
                if (isAudioOnly)
                {
                    if (stats.AudioBitrate.HasValue)
                        audioStream.BitRate = (int)(stats.AudioBitrate.Value * 1000);
                    else if (stats.Bitrate.HasValue)
                        audioStream.BitRate = (int)(stats.Bitrate.Value * 1000);
                }
                else
                {
                    if (stats.AudioBitrate.HasValue)
                        audioStream.BitRate = (int)(stats.AudioBitrate.Value * 1000);
                    else if (audioCodecLower == "ac3") audioStream.BitRate = 384000;
                    else if (audioCodecLower == "eac3") audioStream.BitRate = 640000;
                    else if (audioCodecLower == "aac") audioStream.BitRate = 128000;
                    else if (audioCodecLower == "mp2" || audioCodecLower == "mp1") audioStream.BitRate = 256000;
                }

                if (!string.IsNullOrEmpty(stats.AudioLanguage))
                    audioStream.Language = stats.AudioLanguage;

                audioStream.DisplayTitle = channelLayout != null
                    ? $"{(audioCodecLower ?? "aac").ToUpperInvariant()} {channelLayout}"
                    : (audioCodecLower ?? "aac").ToUpperInvariant();

                mediaStreams.Add(audioStream);

                mediaSource.MediaStreams = mediaStreams;
                mediaSource.DefaultAudioStreamIndex = isAudioOnly ? 0 : 1;

                if (isAudioOnly)
                {
                    Logger?.Debug(
                        "Stream {0}: audio-only - {1} {2}ch",
                        streamId, audioCodecLower ?? "unknown",
                        audioChannels.HasValue ? audioChannels.Value.ToString(CultureInfo.InvariantCulture) : "?");
                }
                else
                {
                    int width = 0, height = 0;
                    if (!string.IsNullOrEmpty(stats.Resolution))
                    {
                        var parts = stats.Resolution.Split('x');
                        if (parts.Length == 2)
                        {
                            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width);
                            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
                        }
                    }
                    Logger?.Debug(
                        "Stream {0}: using stats - {1} {2}x{3} @{4}fps, audio {5} {6}ch",
                        streamId, stats.VideoCodec, width, height,
                        stats.SourceFps, audioCodecLower ?? "unknown",
                        audioChannels.HasValue ? audioChannels.Value.ToString(CultureInfo.InvariantCulture) : "?");
                }
            }
            else
            {
                // No stats - provide defaults so hardware decoding can still be attempted.
                // Codec must be non-null: Emby's RecordingRequiresEncoding accesses it
                // directly and throws NullReferenceException when it is null.  H.264/AAC
                // are the most common IPTV codecs and serve as safe fallbacks.
                mediaSource.MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        Codec = "h264",
                        IsInterlaced = false,
                        PixelFormat = "yuv420p",
                        DisplayTitle = "H264",
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = 1,
                        Codec = "aac",
                        DisplayTitle = "AAC",
                    },
                };
                mediaSource.DefaultAudioStreamIndex = 1;
                Logger?.Debug("Stream {0}: no stats available, using fallback metadata", streamId);
            }

            return mediaSource;
        }

        /// <summary>
        /// Parses an ffmpeg-style audio channel layout string ("5.1", "7.1", "stereo",
        /// "mono", "2.0") into a channel count.  Returns null for unrecognised values.
        /// </summary>
        internal static int? ParseAudioChannelCount(string layout)
        {
            if (string.IsNullOrEmpty(layout)) return null;
            var lower = layout.ToLowerInvariant().Trim();
            if (lower == "mono") return 1;
            if (lower == "stereo") return 2;
            // "X.Y" format: total = X + Y  (e.g. "5.1" → 6, "7.1" → 8, "2.0" → 2)
            var dot = lower.IndexOf('.');
            if (dot > 0 &&
                int.TryParse(lower.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out int main) &&
                int.TryParse(lower.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int lfe))
            {
                return main + lfe;
            }
            if (int.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out int plain))
                return plain;
            return null;
        }

        private bool IsLiveTvDiagnosticsEnabled()
        {
            return Diagnostics.IsEnabled;
        }

        private static string MapVideoCodec(string codec)
        {
            var upper = codec.ToUpperInvariant();
            if (upper == "H264" || upper == "AVC") return "h264";
            if (upper == "HEVC" || upper == "H265") return "hevc";
            if (upper == "MPEG2VIDEO") return "mpeg2video";
            return codec.ToLowerInvariant();
        }
    }
}
