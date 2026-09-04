using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Util;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.M3uEditor.Plugin.Api
{
    [Route("/M3uEditor/Epg", "GET", Summary = "Gets XMLTV EPG data for Live TV channels")]
    public class GetEpgXml : IReturnVoid { }

    [Route("/M3uEditor/LiveTv", "GET", Summary = "Gets M3U playlist for Live TV channels")]
    public class GetM3UPlaylist : IReturnVoid { }

    [Route("/M3uEditor/Categories/Live", "GET", Summary = "Gets Live TV categories from m3u-editor")]
    [Authenticated(Roles = "Admin")]
    public class GetLiveCategories : IReturn<List<Category>> { }

    [Route("/M3uEditor/RefreshCache", "POST", Summary = "Invalidates M3U and EPG caches")]
    [Authenticated(Roles = "Admin")]
    public class RefreshCache : IReturnVoid { }

    [Route("/M3uEditor/RefreshChannelIcons", "POST", Summary = "Reloads m3u-editor channel icon metadata")]
    [Authenticated(Roles = "Admin")]
    public class RefreshChannelIcons : IReturn<RefreshChannelIconsResult> { }

    [Route("/M3uEditor/Managed/Reconcile", "POST", Summary = "Reconciles the m3u-editor managed publishing catalog")]
    [Authenticated(Roles = "Admin")]
    public class ReconcileManagedCatalog : IReturn<ManagedActionResult> { }

    [Route("/M3uEditor/Managed/Rollback", "POST", Summary = "Rolls a managed mapping back to its previous valid generation")]
    [Authenticated(Roles = "Admin")]
    public class RollbackManagedCatalog : IReturn<ManagedActionResult>
    {
        public string MappingUuid { get; set; }
    }

    [Route("/M3uEditor/Managed/Setup/V1", "GET,PUT", Summary = "Gets or establishes managed publishing readiness")]
    [Authenticated(Roles = "Admin")]
    public class ManagedSetupRequest : IReturn<ManagedSetupResult>
    {
        public int IntegrationId { get; set; }
    }

    [Route("/M3uEditor/Dashboard", "GET", Summary = "Gets managed publishing and Live TV status")]
    [Authenticated(Roles = "Admin")]
    public class GetDashboard : IReturn<DashboardResult>
    {
        public int? ManagedPage { get; set; }
        public int? ManagedPageSize { get; set; }
    }

    [Route("/M3uEditor/TestConnection", "POST", Summary = "Tests the m3u-editor Xtream-compatible connection")]
    [Authenticated(Roles = "Admin")]
    public class TestXtreamConnection : IReturn<TestConnectionResult>
    {
        public string Url { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string UserAgent { get; set; }
    }

    [Route("/M3uEditor/ProbeDataCoverage", "GET", Summary = "Reports m3u-editor stream_stats coverage")]
    [Authenticated(Roles = "Admin")]
    public class CheckProbeDataCoverage : IReturn<ProbeDataCoverageResult> { }

    [Route("/M3uEditor/CheckUpdate", "GET", Summary = "Checks GitHub for a newer plugin release")]
    [Authenticated(Roles = "Admin")]
    public class CheckForUpdate : IReturn<UpdateCheckResult>
    {
        public bool? Beta { get; set; }
    }

    [Route("/M3uEditor/Logs", "GET", Summary = "Downloads sanitized plugin logs")]
    [Authenticated(Roles = "Admin")]
    public class GetSanitizedLogs : IReturnVoid { }

    [Route("/M3uEditor/InstallUpdate", "POST", Summary = "Downloads and installs the latest plugin update")]
    [Authenticated(Roles = "Admin")]
    public class InstallUpdate : IReturn<InstallUpdateResult> { }

    [Route("/M3uEditor/RestartEmby", "POST", Summary = "Restarts the Emby server")]
    [Authenticated(Roles = "Admin")]
    public class RestartEmby : IReturnVoid { }

    public class TestConnectionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class ProbeDataCoverageResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int TotalChannels { get; set; }
        public int ChannelsWithProbeData { get; set; }
        public string Source { get; set; }
    }

    public class RefreshChannelIconsResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int MatchedChannels { get; set; }
        public int ClearedChannels { get; set; }
        public int RebuiltChannels { get; set; }
        public int M3UBytes { get; set; }
        public int EpgBytes { get; set; }
    }

    public class InstallUpdateResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class ManagedActionResult
    {
        public bool Success { get; set; }
        public bool Accepted { get; set; }
        public bool Duplicate { get; set; }
        public string JobId { get; set; }
        public string State { get; set; }
        public bool Compatible { get; set; }
        public string Message { get; set; }
        public int TotalMappings { get; set; }
        public int AppliedMappings { get; set; }
        public int FailedMappings { get; set; }
        public int OmittedVersions { get; set; }
        public string Revision { get; set; }
        public string PreviousRevision { get; set; }
    }

    public class ManagedSetupResult
    {
        public int CapabilityVersion { get; set; }
        public int IntegrationId { get; set; }
        public string ConfirmedRoot { get; set; }
        public bool Ready { get; set; }
        public string Result { get; set; }
    }

    public class DashboardResult
    {
        public string PluginVersion { get; set; }
        public LibraryStats LibraryStats { get; set; }
        public ManagedDashboardStatus ManagedPublishing { get; set; }
    }

    public class ManagedDashboardStatus
    {
        public bool Enabled { get; set; }
        public bool SetupReady { get; set; }
        public string SetupResult { get; set; }
        public int ApiVersion { get; set; }
        public int IntegrationId { get; set; }
        public bool ConfigurationValid { get; set; }
        public string CatalogRevision { get; set; }
        public string ActiveGeneration { get; set; }
        public string PreviousGeneration { get; set; }
        public List<ManagedDashboardMapping> Mappings { get; set; }
        public int TotalMappings { get; set; }
        public int TotalFiles { get; set; }
        public int TotalStrmFiles { get; set; }
        public int MovieFolders { get; set; }
        public int MovieCount { get; set; }
        public int SeriesCount { get; set; }
        public int SeasonCount { get; set; }
        public int EpisodeCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool HasMore { get; set; }
        public string DryRunSummary { get; set; }
        public int OmittedVersions { get; set; }
        public string LastError { get; set; }
        public DateTime? LastSuccess { get; set; }
        public ManagedJobStatus Job { get; set; }
    }

    public class ManagedDashboardMapping
    {
        public string MappingUuid { get; set; }
        public string LibraryName { get; set; }
        public bool LibraryNameTruncated { get; set; }
        public string CollectionType { get; set; }
        public string ActiveRevision { get; set; }
        public string PreviousRevision { get; set; }
        public bool Success { get; set; }
        public bool Duplicate { get; set; }
        public int FileCount { get; set; }
        public int StrmFileCount { get; set; }
        public int SeriesCount { get; set; }
        public int SeasonCount { get; set; }
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Removed { get; set; }
        public int OmittedVersions { get; set; }
        public List<string> SourceGroups { get; set; }
        public bool SourceGroupsTruncated { get; set; }
        public string Error { get; set; }
    }

    public class LibraryStats
    {
        public int MovieFolders { get; set; }
        public int MovieCount { get; set; }
        public int SeriesFolders { get; set; }
        public int SeriesCount { get; set; }
        public int SeasonCount { get; set; }
        public int EpisodeCount { get; set; }
        public int LiveTvChannels { get; set; }
    }

    public class M3uEditorApi : BaseApiService
    {
        internal static ManagedDashboardStatus BuildManagedDashboardStatus(
            PluginConfiguration config,
            ManagedJobStatus job,
            int requestedPage,
            int requestedPageSize)
        {
            var page = Math.Max(1, requestedPage);
            var pageSize = Math.Max(1, Math.Min(100, requestedPageSize));
            List<ManagedMappingState> mappings;
            try
            {
                mappings = string.IsNullOrWhiteSpace(config.ManagedMappingsJson)
                    ? new List<ManagedMappingState>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<ManagedMappingState>>(
                        config.ManagedMappingsJson) ?? new List<ManagedMappingState>();
            }
            catch (System.Text.Json.JsonException)
            {
                mappings = new List<ManagedMappingState>();
            }

            mappings = mappings.OrderBy(mapping => mapping.MappingUuid, StringComparer.Ordinal).ToList();
            var maximumPage = mappings.Count == 0 ? 1 : ((mappings.Count - 1) / pageSize) + 1;
            page = Math.Min(page, maximumPage);
            var pageMappings = mappings
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(mapping =>
                {
                    bool libraryNameTruncated;
                    var libraryName = StrmSyncService.NormalizeManagedDashboardLabel(
                        mapping.LibraryName,
                        out libraryNameTruncated);
                    return new ManagedDashboardMapping
                    {
                        MappingUuid = mapping.MappingUuid,
                        LibraryName = libraryName,
                        LibraryNameTruncated = libraryNameTruncated,
                        CollectionType = mapping.CollectionType,
                        ActiveRevision = mapping.ActiveRevision,
                        PreviousRevision = mapping.PreviousRevision,
                        Success = mapping.Success,
                        Duplicate = mapping.Duplicate,
                        FileCount = mapping.FileCount,
                        StrmFileCount = mapping.StrmFileCount,
                        SeriesCount = mapping.SeriesCount,
                        SeasonCount = mapping.SeasonCount,
                        Added = mapping.Added,
                        Changed = mapping.Changed,
                        Removed = mapping.Removed,
                        OmittedVersions = mapping.OmittedVersions,
                        SourceGroups = StrmSyncService.NormalizeManagedSourceGroups(
                        mapping.SourceGroups,
                        out var sourceGroupsTruncated),
                        SourceGroupsTruncated = mapping.SourceGroupsTruncated || sourceGroupsTruncated,
                        Error = mapping.Error
                    };
                })
                .ToList();
            var libraryStats = BuildManagedLibraryStats(mappings);

            return new ManagedDashboardStatus
            {
                Enabled = config.ManagedPublishingEnabled,
                SetupReady = config.ManagedSetupReady,
                SetupResult = config.ManagedSetupLastResult,
                ApiVersion = config.ManagedPublishingApiVersion,
                IntegrationId = config.ManagedPublishingIntegrationId,
                ConfigurationValid = config.ManagedSetupReady &&
                    config.ManagedPublishingIntegrationId > 0 &&
                    ManagedOutputPolicy.GetCanonicalRoots(config.ManagedApprovedOutputRoots).Count > 0,
                CatalogRevision = config.ManagedCatalogRevision,
                ActiveGeneration = config.ManagedActiveGeneration,
                PreviousGeneration = config.ManagedPreviousGeneration,
                Mappings = pageMappings,
                TotalMappings = mappings.Count,
                TotalFiles = mappings.Sum(mapping => mapping.FileCount),
                TotalStrmFiles = mappings.Sum(mapping => mapping.StrmFileCount),
                MovieFolders = libraryStats.MovieFolders,
                MovieCount = libraryStats.MovieCount,
                SeriesCount = libraryStats.SeriesCount,
                SeasonCount = libraryStats.SeasonCount,
                EpisodeCount = libraryStats.EpisodeCount,
                Page = page,
                PageSize = pageSize,
                HasMore = page * pageSize < mappings.Count,
                DryRunSummary = config.ManagedDryRunSummary,
                OmittedVersions = config.ManagedOmittedVersions,
                LastError = config.ManagedLastError,
                LastSuccess = config.ManagedLastSuccessTicks > 0
                    ? new DateTime(config.ManagedLastSuccessTicks, DateTimeKind.Utc)
                    : (DateTime?)null,
                Job = job
            };
        }

        internal static LibraryStats BuildManagedLibraryStats(IEnumerable<ManagedMappingState> mappings)
        {
            var states = (mappings ?? Enumerable.Empty<ManagedMappingState>()).ToList();
            return new LibraryStats
            {
                MovieFolders = states.Count(mapping =>
                    string.Equals(mapping.CollectionType, "movies", StringComparison.Ordinal)),
                MovieCount = states
                    .Where(mapping => string.Equals(mapping.CollectionType, "movies", StringComparison.Ordinal))
                    .Sum(mapping => mapping.StrmFileCount),
                SeriesFolders = states
                    .Where(mapping => string.Equals(mapping.CollectionType, "tvshows", StringComparison.Ordinal))
                    .Sum(mapping => mapping.SeriesCount),
                SeriesCount = states
                    .Where(mapping => string.Equals(mapping.CollectionType, "tvshows", StringComparison.Ordinal))
                    .Sum(mapping => mapping.SeriesCount),
                SeasonCount = states
                    .Where(mapping => string.Equals(mapping.CollectionType, "tvshows", StringComparison.Ordinal))
                    .Sum(mapping => mapping.SeasonCount),
                EpisodeCount = states
                    .Where(mapping => string.Equals(mapping.CollectionType, "tvshows", StringComparison.Ordinal))
                    .Sum(mapping => mapping.StrmFileCount)
            };
        }

        internal static ProbeDataCoverageResult BuildProbeDataCoverageResult(
            int totalChannels,
            int channelsWithProbeData,
            bool autoLoaded)
        {
            var prefix = autoLoaded ? "(auto-loaded " + totalChannels + " channels) " : string.Empty;
            if (totalChannels == 0)
            {
                return new ProbeDataCoverageResult
                {
                    Success = false,
                    Source = "none",
                    Message = "Channel cache is empty and an on-demand load returned no channels.",
                };
            }

            if (channelsWithProbeData == 0)
            {
                return new ProbeDataCoverageResult
                {
                    Success = true,
                    TotalChannels = totalChannels,
                    Source = "none",
                    Message = prefix + "No m3u-editor stream_stats probe data is available. Emby will probe streams on playback.",
                };
            }

            return new ProbeDataCoverageResult
            {
                Success = true,
                TotalChannels = totalChannels,
                ChannelsWithProbeData = channelsWithProbeData,
                Source = "m3u-editor",
                Message = prefix + string.Format(
                    "{0} of {1} channels have m3u-editor stream_stats probe data. FFprobe is bypassed for these channels.",
                    channelsWithProbeData,
                    totalChannels),
            };
        }

        public async Task<object> Get(GetEpgXml request)
        {
            var xml = await Plugin.Instance.LiveTvService.GetXmltvEpgAsync(CancellationToken.None).ConfigureAwait(false);
            return ResultFactory.GetResult(
                Request,
                new MemoryStream(Encoding.UTF8.GetBytes(xml)),
                "application/xml",
                new Dictionary<string, string>());
        }

        public async Task<object> Get(GetM3UPlaylist request)
        {
            var m3u = await Plugin.Instance.LiveTvService.GetM3UPlaylistAsync(CancellationToken.None).ConfigureAwait(false);
            return ResultFactory.GetResult(
                Request,
                new MemoryStream(Encoding.UTF8.GetBytes(m3u)),
                "audio/x-mpegurl",
                new Dictionary<string, string>());
        }

        public async Task<object> Get(GetLiveCategories request)
        {
            var config = Plugin.Instance.Configuration;
            if (string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) ||
                string.IsNullOrEmpty(config.Password))
            {
                return new List<Category>();
            }

            var categories = await Plugin.Instance.LiveTvService
                .GetLiveCategoriesAsync(CancellationToken.None)
                .ConfigureAwait(false);
            config.CachedLiveCategories = System.Text.Json.JsonSerializer.Serialize(
                categories.Select(category => new { category.CategoryId, category.CategoryName }).ToList());
            Plugin.Instance.SaveConfiguration();
            return categories;
        }

        public object Post(ReconcileManagedCatalog request)
        {
            var jobs = Plugin.Instance.StrmSyncService.ManagedActionJobs;
            return ToManagedActionAdmission(jobs.TryStart("reconcile", null, ReconcileManagedCatalogAsync));
        }

        private static async Task<ManagedActionResult> ReconcileManagedCatalogAsync(CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            var reconciled = await plugin.StrmSyncService.ReconcileManagedAsync(
                plugin.Configuration,
                () => plugin.SaveConfiguration(),
                null,
                cancellationToken,
                () => plugin.ApplicationHost.Resolve<ILibraryManager>().QueueLibraryScan())
                .ConfigureAwait(false);
            return new ManagedActionResult
            {
                Success = reconciled.Success,
                Compatible = reconciled.Compatible,
                Message = reconciled.Compatible
                    ? (reconciled.Success ? "Managed catalog reconcile completed." : reconciled.Error)
                    : "Compatible m3u-editor publishing capability version 1 was not advertised.",
                TotalMappings = reconciled.TotalMappings,
                AppliedMappings = reconciled.AppliedMappings,
                FailedMappings = reconciled.FailedMappings,
                OmittedVersions = reconciled.OmittedVersions,
                Revision = reconciled.CatalogRevision
            };
        }

        public object Post(RollbackManagedCatalog request)
        {
            var mappingUuid = request == null ? null : request.MappingUuid;
            var jobs = Plugin.Instance.StrmSyncService.ManagedActionJobs;
            return ToManagedActionAdmission(jobs.TryStart(
                "rollback",
                mappingUuid,
                cancellationToken => RollbackManagedCatalogAsync(mappingUuid, cancellationToken)));
        }

        public object Get(ManagedSetupRequest request)
        {
            var plugin = Plugin.Instance;
            return new ManagedSetupService(plugin.DataFolderPath).Get(plugin.Configuration);
        }

        public object Put(ManagedSetupRequest request)
        {
            return Plugin.Instance.UpdateManagedSetup(request == null ? 0 : request.IntegrationId);
        }

        private static async Task<ManagedActionResult> RollbackManagedCatalogAsync(
            string mappingUuid,
            CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            var config = plugin.Configuration;
            List<ManagedMappingState> mappings;
            try
            {
                mappings = string.IsNullOrWhiteSpace(config.ManagedMappingsJson)
                    ? new List<ManagedMappingState>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<ManagedMappingState>>(config.ManagedMappingsJson);
            }
            catch (System.Text.Json.JsonException)
            {
                mappings = new List<ManagedMappingState>();
            }

            var mapping = mappings?.FirstOrDefault(value =>
                string.Equals(value.MappingUuid, mappingUuid, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
            {
                return new ManagedActionResult { Message = "The managed mapping was not found in plugin-owned state." };
            }

            string approvalError;
            if (!ManagedOutputPolicy.IsApproved(mapping.OutputPath, config.ManagedApprovedOutputRoots, out approvalError))
            {
                return new ManagedActionResult { Message = approvalError };
            }

            var rollback = await plugin.StrmSyncService.RollbackManagedMappingAsync(
                mapping.OutputPath,
                mapping.MappingUuid,
                cancellationToken,
                () => plugin.ApplicationHost.Resolve<ILibraryManager>().QueueLibraryScan(),
                config.ManagedApprovedOutputRoots).ConfigureAwait(false);
            if (rollback.Success)
            {
                mapping.ActiveRevision = rollback.Revision;
                mapping.PreviousRevision = rollback.PreviousRevision;
                mapping.FileCount = rollback.FileCount;
                mapping.StrmFileCount = rollback.StrmFileCount;
                mapping.Success = true;
                mapping.Error = string.Empty;
                config.ManagedMappingsJson = System.Text.Json.JsonSerializer.Serialize(mappings);
                config.ManagedActiveGeneration = rollback.Revision ?? string.Empty;
                config.ManagedPreviousGeneration = rollback.PreviousRevision ?? string.Empty;
                config.ManagedLastError = string.Empty;
            }
            else
            {
                config.ManagedLastError = rollback.Error ?? "Managed rollback failed.";
            }
            plugin.SaveConfiguration();

            return new ManagedActionResult
            {
                Success = rollback.Success,
                Compatible = config.ManagedPublishingEnabled,
                Message = rollback.Success ? "Previous managed generation restored." : rollback.Error,
                Revision = rollback.Revision,
                PreviousRevision = rollback.PreviousRevision
            };
        }

        internal static ManagedActionResult ToManagedActionAdmission(ManagedJobStatus status)
        {
            return new ManagedActionResult
            {
                Success = status.Accepted || status.Duplicate,
                Accepted = status.Accepted,
                Duplicate = status.Duplicate,
                JobId = status.JobId,
                State = status.State,
                Message = status.Accepted ? "Managed action accepted." : "A managed action is already running."
            };
        }

        public object Get(GetDashboard request)
        {
            var plugin = Plugin.Instance;
            var managed = BuildManagedDashboardStatus(
                plugin.Configuration,
                plugin.StrmSyncService.ManagedActionJobs.GetStatus(),
                request?.ManagedPage ?? 1,
                request?.ManagedPageSize ?? 10);
            return new DashboardResult
            {
                PluginVersion = PluginVersionHelper.CurrentVersion,
                ManagedPublishing = managed,
                LibraryStats = new LibraryStats
                {
                    MovieFolders = managed.MovieFolders,
                    MovieCount = managed.MovieCount,
                    SeriesFolders = managed.SeriesCount,
                    SeriesCount = managed.SeriesCount,
                    SeasonCount = managed.SeasonCount,
                    EpisodeCount = managed.EpisodeCount,
                    LiveTvChannels = M3uEditorTunerHost.Instance?.CachedChannelCount ?? 0,
                }
            };
        }

        public void Post(RefreshCache request)
        {
            Plugin.Instance.LiveTvService.InvalidateCache();
            M3uEditorTunerHost.Instance?.ClearCaches();
        }

        public async Task<object> Post(RefreshChannelIcons request)
        {
            var host = M3uEditorTunerHost.Instance;
            if (host == null)
            {
                return new RefreshChannelIconsResult
                {
                    Message = "m3u-editor tuner host is not initialized."
                };
            }

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    if (host.CachedChannelCount == 0)
                    {
                        await host.EnsureChannelsLoadedAsync(cts.Token).ConfigureAwait(false);
                    }
                    var cleanup = host.ClearCachedChannelArtwork();
                    Plugin.Instance.LiveTvService.InvalidateCache();
                    host.ClearCaches();
                    var loaded = await host.EnsureChannelsLoadedAsync(cts.Token).ConfigureAwait(false);
                    var m3u = await Plugin.Instance.LiveTvService.GetM3UPlaylistAsync(cts.Token).ConfigureAwait(false);
                    var epg = await Plugin.Instance.LiveTvService.GetXmltvEpgAsync(cts.Token).ConfigureAwait(false);
                    return new RefreshChannelIconsResult
                    {
                        Success = loaded,
                        MatchedChannels = cleanup.MatchedChannels,
                        ClearedChannels = cleanup.ClearedChannels,
                        RebuiltChannels = host.CachedChannelCount,
                        M3UBytes = m3u == null ? 0 : Encoding.UTF8.GetByteCount(m3u),
                        EpgBytes = epg == null ? 0 : Encoding.UTF8.GetByteCount(epg),
                        Message = loaded
                            ? string.Format(
                                "Cleared cached artwork for {0} m3u-editor channel(s) and reloaded {1} channel(s). Refresh the Emby guide to apply the icons.",
                                cleanup.ClearedChannels,
                                host.CachedChannelCount)
                            : "m3u-editor returned no channels. Verify the configured connection."
                    };
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException || ex is ArgumentException ||
                ex is HttpRequestException || ex is TaskCanceledException)
            {
                return new RefreshChannelIconsResult { Message = "Channel icon refresh failed: " + ex.Message };
            }
        }

        public async Task<object> Post(TestXtreamConnection request)
        {
            var config = Plugin.Instance.Configuration;
            var userAgent = !string.IsNullOrWhiteSpace(request.UserAgent)
                ? request.UserAgent
                : config.HttpUserAgent;
            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            })
            using (var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            })
            {
                if (!string.IsNullOrWhiteSpace(userAgent))
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

                return await TestConnectionAsync(
                    request,
                    config,
                    httpClient,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        internal static async Task<TestConnectionResult> TestConnectionAsync(
            TestXtreamConnection request,
            PluginConfiguration config,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            var baseUrl = !string.IsNullOrWhiteSpace(request.Url)
                ? request.Url.TrimEnd('/')
                : (config.BaseUrl ?? string.Empty).TrimEnd('/');
            var username = !string.IsNullOrWhiteSpace(request.Username) ? request.Username : config.Username;
            var password = !string.IsNullOrWhiteSpace(request.Password) ? request.Password : config.Password;
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                return new TestConnectionResult
                {
                    Message = "Please configure the m3u-editor URL, username, and password first."
                };
            }

            try
            {
                var authenticated = await new M3uEditorClient(httpClient).TestConnectionAsync(
                    baseUrl,
                    username,
                    password,
                    cancellationToken).ConfigureAwait(false);
                if (!authenticated)
                {
                    return new TestConnectionResult
                    {
                        Message = "m3u-editor authentication failed."
                    };
                }

                return new TestConnectionResult
                {
                    Success = true,
                    Message = "Connection to the m3u-editor Xtream-compatible interface succeeded."
                };
            }
            catch (Exception ex) when (
                ex is HttpRequestException || ex is TaskCanceledException ||
                ex is System.Text.Json.JsonException || ex is InvalidOperationException ||
                ex is ArgumentException)
            {
                return new TestConnectionResult
                {
                    Message = "Connection failed: " + LogSanitizer.SanitizeLine(ex.Message, username, password)
                };
            }
        }

        public async Task<object> Get(CheckProbeDataCoverage request)
        {
            var host = M3uEditorTunerHost.Instance;
            if (host == null)
            {
                return new ProbeDataCoverageResult
                {
                    Message = "Tuner host not initialized.",
                    Source = "none"
                };
            }

            var autoLoaded = false;
            if (host.CachedChannelCount == 0)
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                {
                    autoLoaded = await host.EnsureChannelsLoadedAsync(cts.Token).ConfigureAwait(false);
                }
            }

            return BuildProbeDataCoverageResult(
                host.CachedChannelCount,
                host.BackendStreamStatsCount,
                autoLoaded);
        }

        public async Task<object> Get(CheckForUpdate request)
        {
            UpdateChecker.InvalidateCache();
            return await UpdateChecker.CheckForUpdateAsync(request.Beta).ConfigureAwait(false);
        }

        public async Task<object> Post(InstallUpdate request)
        {
            var result = new InstallUpdateResult();
            try
            {
                var checkResult = await UpdateChecker.CheckForUpdateAsync().ConfigureAwait(false);
                if (!checkResult.UpdateAvailable || string.IsNullOrEmpty(checkResult.DownloadUrl))
                {
                    result.Message = "No update available.";
                    return result;
                }

                var currentDll = typeof(Plugin).Assembly.Location;
                if (string.IsNullOrEmpty(currentDll) || !File.Exists(currentDll))
                {
                    var pluginsDir = Plugin.Instance.ApplicationPaths.PluginsPath;
                    currentDll = Path.Combine(pluginsDir, "Emby.M3uEditor.Plugin.dll");
                }
                if (!File.Exists(currentDll))
                {
                    result.Message = "Could not determine plugin DLL path.";
                    return result;
                }

                byte[] dllBytes;
                using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("M3u-Editor-for-Emby/1.0");
                    dllBytes = await httpClient.GetByteArrayAsync(checkResult.DownloadUrl).ConfigureAwait(false);
                }
                if (dllBytes.Length < 1024)
                {
                    result.Message = "Downloaded file is too small. Aborting.";
                    return result;
                }

                var tempPath = currentDll + ".temp";
                var backupPath = currentDll + ".bak";
                File.WriteAllBytes(tempPath, dllBytes);
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(currentDll, backupPath);
                try
                {
                    File.Move(tempPath, currentDll);
                    File.Delete(backupPath);
                }
                catch
                {
                    if (File.Exists(backupPath) && !File.Exists(currentDll))
                        File.Move(backupPath, currentDll);
                    throw;
                }

                UpdateChecker.UpdateInstalled = true;
                UpdateChecker.InvalidateCache();
                Plugin.Instance.Configuration.LastInstalledVersion = checkResult.LatestVersion;
                Plugin.Instance.SaveConfiguration();
                result.Success = true;
                result.Message = "Update installed successfully. Restart Emby to apply.";
            }
            catch (Exception ex) when (
                ex is HttpRequestException || ex is TaskCanceledException ||
                ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                result.Message = "Install failed: " + ex.Message;
            }
            return result;
        }

        public void Post(RestartEmby request)
        {
            try
            {
                var appHost = Plugin.Instance.ApplicationHost;
                var restartMethod = appHost.GetType().GetMethod(
                    "Restart",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                restartMethod?.Invoke(appHost, null);
            }
            catch (Exception ex) when (
                ex is System.Reflection.TargetInvocationException ||
                ex is MethodAccessException || ex is InvalidOperationException)
            {
                Logger.Warn("RestartEmby failed: {0}", ex.Message);
            }
        }

        public object Get(GetSanitizedLogs request)
        {
            var config = Plugin.Instance.Configuration;
            var lines = new List<string>();
            try
            {
                foreach (var logFile in Directory.GetFiles(Plugin.Instance.ApplicationPaths.LogDirectoryPath, "*.*")
                    .Where(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(5))
                {
                    try
                    {
                        lines.AddRange(File.ReadLines(logFile).Where(line =>
                            line.IndexOf("M3uEditor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("LiveTv", StringComparison.OrdinalIgnoreCase) >= 0));
                    }
                    catch (IOException ex)
                    {
                        Logger.Debug("Log read failed for '{0}': {1}", logFile, ex.Message);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Logger.Debug("Log read failed for '{0}': {1}", logFile, ex.Message);
                    }
                }
            }
            catch (IOException ex)
            {
                Logger.Debug("Log discovery failed: {0}", ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Debug("Log discovery failed: {0}", ex.Message);
            }

            var sanitized = new StringBuilder();
            foreach (var line in lines)
                sanitized.AppendLine(LogSanitizer.SanitizeLine(line, config.Username, config.Password));
            if (sanitized.Length == 0)
                sanitized.AppendLine("No plugin-related log entries found.");

            var headers = new Dictionary<string, string>
            {
                { "Content-Disposition", "attachment; filename=\"m3u-editor-log.txt\"" },
            };
            return ResultFactory.GetResult(
                Request,
                new MemoryStream(Encoding.UTF8.GetBytes(sanitized.ToString())),
                "text/plain",
                headers);
        }
    }
}
