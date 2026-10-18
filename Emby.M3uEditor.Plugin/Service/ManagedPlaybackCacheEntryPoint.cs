using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;

namespace Emby.M3uEditor.Plugin.Service
{
    /// <summary>
    /// Emby lifecycle adapter. SDK callbacks only copy immutable identifiers into
    /// a bounded queue; manifest reads and HTTP run on owned background workers.
    /// </summary>
    public sealed class ManagedPlaybackCacheEntryPoint : IServerEntryPoint, IDisposable
    {
        private static readonly object RuntimeGate = new object();
        private static ManagedPlaybackCacheEntryPoint _current;
        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly Func<PluginConfiguration> _configurationProvider;
        private readonly Func<HttpClient> _httpClientFactory;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<HttpClient, M3uEditorClient> _clientFactory;
        private readonly BlockingCollection<InputEvent> _inputs = new BlockingCollection<InputEvent>(100);
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly ManagedPlaybackIdentityResolver _resolver = new ManagedPlaybackIdentityResolver();
        private readonly ConcurrentDictionary<string, ManagedPlaybackIdentity> _identityByItem =
            new ConcurrentDictionary<string, ManagedPlaybackIdentity>(StringComparer.Ordinal);
        private Task _inputConsumer;
        private ManagedPlaybackCacheService _cache;
        private ManagedPlaybackWatchedCohort _cohort;
        private HttpClient _httpClient;
        private M3uEditorClient _client;
        private string _capabilityKey;
        private string _capabilitySnapshot;
        private string _lifecycleSnapshot;
        private string _capabilityRefreshSnapshot;
        private CapabilityRefreshRequest _capabilityRefreshOwner;
        private M3uEditorCacheControlCapability _cacheCapability;
        private Task _capabilityRefresh;
        private bool _capabilityRefreshQueued;
        private long _capabilityGeneration;
        private long _configurationGeneration;
        private bool _running;
        private bool _disposed;

        public ManagedPlaybackCacheEntryPoint(
            ISessionManager sessionManager,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager)
            : this(sessionManager, userDataManager, libraryManager, null, null, null)
        {
        }

        internal ManagedPlaybackCacheEntryPoint(
            ISessionManager sessionManager,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            Func<PluginConfiguration> configurationProvider,
            Func<HttpClient> httpClientFactory,
            Func<DateTime> utcNow)
            : this(
                sessionManager,
                userDataManager,
                libraryManager,
                configurationProvider,
                httpClientFactory,
                utcNow,
                null)
        {
        }

        internal ManagedPlaybackCacheEntryPoint(
            ISessionManager sessionManager,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            Func<PluginConfiguration> configurationProvider,
            Func<HttpClient> httpClientFactory,
            Func<DateTime> utcNow,
            Func<HttpClient, M3uEditorClient> clientFactory)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _configurationProvider = configurationProvider ?? (() => Plugin.InstanceOrNull?.Configuration);
            _httpClientFactory = httpClientFactory ?? CreateDefaultHttpClient;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _clientFactory = clientFactory ?? (client => new M3uEditorClient(client));
        }

        public void Run()
        {
            if (_disposed || _running)
            {
                return;
            }

            _httpClient = _httpClientFactory();
            _client = _clientFactory(_httpClient);
            _cache = new ManagedPlaybackCacheService(DispatchAsync, TimeSpan.FromSeconds(30), _utcNow);
            _cohort = new ManagedPlaybackWatchedCohort(new string[0]);
            _inputConsumer = Task.Factory.StartNew(
                ConsumeInputs,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _running = true;
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _userDataManager.UserDataSaved += OnUserDataSaved;
            lock (RuntimeGate)
            {
                _current = this;
            }
            RefreshCapabilityIfNeeded();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (RuntimeGate)
            {
                if (ReferenceEquals(_current, this))
                {
                    _current = null;
                }
            }
            if (_running)
            {
                _sessionManager.PlaybackStart -= OnPlaybackStart;
                _sessionManager.PlaybackProgress -= OnPlaybackProgress;
                _sessionManager.PlaybackStopped -= OnPlaybackStopped;
                _userDataManager.UserDataSaved -= OnUserDataSaved;
                _inputs.CompleteAdding();
                try
                {
                    if (_inputConsumer != null && !_inputConsumer.Wait(TimeSpan.FromSeconds(2)))
                    {
                        _cancellation.Cancel();
                        _inputConsumer.Wait(TimeSpan.FromSeconds(1));
                    }
                }
                catch (AggregateException)
                {
                }
            }

            _cancellation.Cancel();
            _cache?.Dispose();
            _httpClient?.Dispose();
            _inputs.Dispose();
            _cancellation.Dispose();
        }

        internal static bool IsCacheCapabilityReady(PluginConfiguration config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.BaseUrl) ||
                string.IsNullOrWhiteSpace(config.Username))
            {
                return false;
            }

            lock (RuntimeGate)
            {
                if (_current == null || _current._disposed || _current._cacheCapability == null)
                {
                    return false;
                }

                var capabilityKey = CreateCapabilityKey(config);
                return string.Equals(_current._capabilityKey, capabilityKey, StringComparison.Ordinal);
            }
        }

        internal static void NotifyConfigurationChanged(bool enabled)
        {
            lock (RuntimeGate)
            {
                _current?.ReconcileConfiguration(enabled);
            }
        }

        private void ReconcileConfiguration(bool enabled)
        {
            var config = _configurationProvider();
            var capabilitySnapshot = CreateCapabilitySnapshot(config);
            var lifecycleSnapshot = CreateLifecycleSnapshot(config);
            if (!string.Equals(_lifecycleSnapshot, lifecycleSnapshot, StringComparison.Ordinal))
            {
                _configurationGeneration++;
                _lifecycleSnapshot = lifecycleSnapshot;
                _identityByItem.Clear();
                if (!string.Equals(_capabilityKey, CreateCapabilityKey(config), StringComparison.Ordinal))
                {
                    _capabilityKey = null;
                    _cacheCapability = null;
                }
            }
            _capabilitySnapshot = capabilitySnapshot;
            _cache?.Reconcile(enabled);
            if (_cacheCapability == null)
            {
                RefreshCapabilityIfNeeded();
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs args)
        {
            var selectedMediaSourcePath = GetSelectedMediaSourcePath(args, args?.Item?.Path);
            TryEnqueue(InputEvent.Start(
                args?.PlaySessionId,
                FormatEntityId(args?.Item?.Id),
                selectedMediaSourcePath,
                !string.IsNullOrWhiteSpace(selectedMediaSourcePath),
                args?.Item,
                CaptureConfigurationGeneration()));
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs args)
        {
            TryEnqueue(InputEvent.Progress(args?.PlaySessionId, CaptureConfigurationGeneration()));
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs args)
        {
            TryEnqueue(InputEvent.Stop(args?.PlaySessionId, CaptureConfigurationGeneration()));
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs args)
        {
            TryEnqueue(InputEvent.Watched(
                FormatEntityId(args?.Item?.Id),
                args?.Item?.Path,
                FormatEntityId(args?.User?.Id),
                args?.UserData?.Played == true,
                args?.Item,
                CaptureConfigurationGeneration()));
        }

        private void TryEnqueue(InputEvent input)
        {
            if (_disposed || input == null)
            {
                return;
            }

            try
            {
                if (!_inputs.IsAddingCompleted)
                {
                    _inputs.TryAdd(input);
                }
            }
            catch (InvalidOperationException)
            {
                // Shutdown completed or disposed the bounded queue between the checks.
            }
        }

        private void ConsumeInputs()
        {
            try
            {
                foreach (var input in _inputs.GetConsumingEnumerable(_cancellation.Token))
                {
                    try
                    {
                        ProcessInput(input);
                    }
                    catch
                    {
                        // Playback and user-data persistence must never depend on cache control.
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void ProcessInput(InputEvent input)
        {
            if (input.Kind == "progress")
            {
                _cache?.PlaybackProgress(input.SessionId);
                return;
            }

            if (input.Kind == "stop")
            {
                // Stops are best-effort lease cleanup even after a reconciliation.
                _cache?.PlaybackStopped(input.SessionId);
                return;
            }

            var config = _configurationProvider();
            if (config == null || !TryApplyConfiguration(input.ConfigurationGeneration, config))
            {
                return;
            }

            if (!config.ManagedPlaybackCacheEnabled || !config.ManagedPublishingEnabled ||
                config.ManagedPublishingIntegrationId < 1)
            {
                return;
            }

            var users = NormalizeUsers(config.ManagedPlaybackCacheUserIds);
            _cohort.UpdateUsers(users);
            var context = CreateContext(config, users);
            if (context == null)
            {
                return;
            }

            if (input.Kind == "start")
            {
                if (!TryResolveConfiguredIdentity(config, input.Path, input.ConcreteMediaSource, out var identity))
                {
                    return;
                }

                var targets = SelectPrefetchTargets(config, identity, users, context.ConsumerId);
                if (!TryAdmitPlaybackStarted(input.ConfigurationGeneration, input.SessionId, identity, targets, context))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(input.ItemId))
                {
                    TryCacheIdentity(input.ConfigurationGeneration, input.ItemId, identity);
                }
                if (config.ManagedPlaybackCacheDeleteAfterWatched && users.Count > 0 && input.Item != null)
                {
                    var startCohortComplete = RecordWatchedSnapshot(
                        input.ItemId, input.Item, users, context.ConsumerId, null, null);
                    TryAdmitWatchedChanged(
                        input.ConfigurationGeneration,
                        identity,
                        startCohortComplete,
                        config.ManagedPlaybackCacheDeleteGraceHours,
                        startCohortComplete,
                        context);
                }
                return;
            }

            if (input.Kind != "watched" || !config.ManagedPlaybackCacheDeleteAfterWatched ||
                users.Count == 0 || !users.Contains(input.UserId))
            {
                return;
            }

            if (!TryGetCachedIdentity(input.ConfigurationGeneration, input.ItemId, out var watchedIdentity) &&
                !TryResolveConfiguredIdentity(config, input.Path, true, out watchedIdentity))
            {
                return;
            }

            var complete = RecordWatchedSnapshot(
                input.ItemId,
                input.Item,
                users,
                context.ConsumerId,
                input.UserId,
                input.Played);
            TryAdmitWatchedChanged(
                input.ConfigurationGeneration,
                watchedIdentity,
                complete,
                config.ManagedPlaybackCacheDeleteGraceHours,
                complete,
                context);
        }

        private long CaptureConfigurationGeneration()
        {
            lock (RuntimeGate)
            {
                return _configurationGeneration;
            }
        }

        private bool TryApplyConfiguration(long generation, PluginConfiguration config)
        {
            lock (RuntimeGate)
            {
                if (_disposed || !ReferenceEquals(_current, this) || generation != _configurationGeneration || _cache == null)
                {
                    return false;
                }

                _cache.SetEnabled(config.ManagedPlaybackCacheEnabled);
                return true;
            }
        }

        private void TryCacheIdentity(long generation, string itemId, ManagedPlaybackIdentity identity)
        {
            lock (RuntimeGate)
            {
                if (!_disposed && ReferenceEquals(_current, this) && generation == _configurationGeneration)
                {
                    _identityByItem[itemId] = identity;
                }
            }
        }

        private bool TryGetCachedIdentity(
            long generation,
            string itemId,
            out ManagedPlaybackIdentity identity)
        {
            lock (RuntimeGate)
            {
                identity = null;
                return !_disposed && ReferenceEquals(_current, this) &&
                    generation == _configurationGeneration &&
                    _identityByItem.TryGetValue(itemId ?? string.Empty, out identity);
            }
        }

        private bool TryAdmitPlaybackStarted(
            long generation,
            string sessionId,
            ManagedPlaybackIdentity identity,
            IReadOnlyList<ManagedPlaybackIdentity> targets,
            ManagedPlaybackDispatchContext context)
        {
            lock (RuntimeGate)
            {
                if (_disposed || !ReferenceEquals(_current, this) || generation != _configurationGeneration)
                {
                    return false;
                }

                return _cache != null && _cache.PlaybackStarted(sessionId, identity, targets, context);
            }
        }

        private bool TryAdmitWatchedChanged(
            long generation,
            ManagedPlaybackIdentity identity,
            bool watched,
            int deleteAfterHours,
            bool cohortComplete,
            ManagedPlaybackDispatchContext context)
        {
            lock (RuntimeGate)
            {
                if (_disposed || !ReferenceEquals(_current, this) || generation != _configurationGeneration)
                {
                    return false;
                }

                _cache?.WatchedChanged(identity, watched, deleteAfterHours, cohortComplete, context);
                return true;
            }
        }

        private bool RecordWatchedSnapshot(
            string itemId,
            BaseItem item,
            IEnumerable<string> users,
            string cohortId,
            string eventUserId,
            bool? eventPlayed)
        {
            if (item == null)
            {
                return false;
            }

            foreach (var userId in users)
            {
                var played = false;
                if (eventPlayed.HasValue && string.Equals(userId, eventUserId, StringComparison.Ordinal))
                {
                    played = eventPlayed.Value;
                }
                else
                {
                    try
                    {
                        played = _userDataManager.GetUserData(userId, item)?.Played == true;
                    }
                    catch
                    {
                        // Missing or unreadable user data is unknown and therefore protective.
                        played = false;
                    }
                }

                _cohort.Record(itemId, userId, played, cohortId);
            }

            return _cohort.IsComplete(itemId, cohortId);
        }

        private bool TryResolveConfiguredIdentity(
            PluginConfiguration config,
            string selectedPath,
            bool concrete,
            out ManagedPlaybackIdentity identity)
        {
            identity = null;
            var mappings = ReadMappings(config);
            var roots = mappings
                .Where(mapping => mapping != null && mapping.Success &&
                    mapping.IntegrationId == config.ManagedPublishingIntegrationId &&
                    !string.IsNullOrWhiteSpace(mapping.OutputPath) &&
                    ManagedOutputPolicy.IsApproved(
                        mapping.OutputPath,
                        config.ManagedApprovedOutputRoots,
                        out _))
                .Select(mapping => mapping.OutputPath)
                .ToList();
            if (!_resolver.TryResolveActive(roots, selectedPath, concrete, out identity))
            {
                return false;
            }

            var resolvedMappingUuid = identity.MappingUuid;
            var resolvedRevision = identity.Revision;
            return mappings.Any(mapping => mapping != null && mapping.Success &&
                mapping.IntegrationId == config.ManagedPublishingIntegrationId &&
                string.Equals(mapping.MappingUuid, resolvedMappingUuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mapping.ActiveRevision, resolvedRevision, StringComparison.Ordinal));
        }

        private IReadOnlyList<ManagedPlaybackIdentity> SelectPrefetchTargets(
            PluginConfiguration config,
            ManagedPlaybackIdentity current,
            IReadOnlyCollection<string> users,
            string cohortId)
        {
            var candidates = _resolver.GetSeriesCandidates(current).ToList();
            var positions = candidates.Select(candidate => new ManagedEpisodePosition
            {
                Id = candidate.CanonicalId,
                Season = candidate.SeasonNumber ?? 0,
                Episode = candidate.EpisodeNumber ?? 0
            });
            var selected = ManagedPlaybackCachePolicy.Select(
                positions,
                current.CanonicalId,
                config.ManagedPlaybackCacheMode,
                config.ManagedPlaybackCacheNextCount,
                FindFullyWatchedCandidateIds(candidates, users, cohortId));
            return selected
                .Select(id => candidates.FirstOrDefault(candidate => string.Equals(candidate.CanonicalId, id, StringComparison.Ordinal)))
                .Where(candidate => candidate != null)
                .ToList();
        }

        private IReadOnlyCollection<string> FindFullyWatchedCandidateIds(
            IEnumerable<ManagedPlaybackIdentity> candidates,
            IReadOnlyCollection<string> users,
            string cohortId)
        {
            if (users == null || users.Count == 0 || string.IsNullOrWhiteSpace(cohortId))
            {
                return new string[0];
            }

            try
            {
                if (_libraryManager == null)
                {
                    return new string[0];
                }

                var watched = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in candidates ?? Enumerable.Empty<ManagedPlaybackIdentity>())
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.ManagedPath))
                    {
                        continue;
                    }

                    var item = _libraryManager.FindByPath(candidate.ManagedPath, false);
                    if (item != null && RecordWatchedSnapshot(
                        FormatEntityId(item.Id), item, users, cohortId, null, null))
                    {
                        watched.Add(candidate.CanonicalId);
                    }
                }

                return watched;
            }
            catch
            {
                // Library lookup/user data failures are unknown, never treated as watched.
                return new string[0];
            }
        }

        private async Task DispatchAsync(
            ManagedPlaybackCacheCommand command,
            CancellationToken cancellationToken)
        {
            if (command?.Context == null || command.Identity == null || _client == null)
            {
                return;
            }

            var capabilityKey = CreateCapabilityKey(
                command.Context.BaseUrl, command.Context.Username, command.Context.Password);
            M3uEditorCacheControlCapability capability;
            lock (RuntimeGate)
            {
                capability = string.Equals(_capabilityKey, capabilityKey, StringComparison.Ordinal)
                    ? _cacheCapability
                    : null;
            }
            if (capability == null)
            {
                capability = await _client.DiscoverCacheControlCapabilityAsync(
                    command.Context.BaseUrl,
                    command.Context.Username,
                    command.Context.Password,
                    cancellationToken).ConfigureAwait(false);
                if (capability != null)
                {
                    lock (RuntimeGate)
                    {
                        if (!_disposed && ReferenceEquals(_current, this))
                        {
                            _capabilityKey = capabilityKey;
                            _cacheCapability = capability;
                        }
                    }
                }
            }
            if (capability == null)
            {
                return;
            }

            var request = new M3uEditorCacheControlRequest
            {
                IntegrationId = command.Context.IntegrationId,
                MappingUuid = command.Identity.MappingUuid,
                Revision = command.Identity.Revision,
                RequestId = command.RequestId,
                ConsumerId = command.Context.ConsumerId,
                Targets = (command.Targets ?? new[] { command.Identity })
                    .Take(capability.MaxTargets)
                    .Select(identity => new M3uEditorCacheControlTarget
                    {
                        CanonicalId = identity.CanonicalId,
                        VariantKey = identity.VariantKey
                    }).ToList(),
                SessionId = command.SessionId,
                Sequence = command.Sequence,
                State = command.State,
                Watched = command.Watched,
                DeleteAfterHours = command.DeleteAfterHours,
                CohortComplete = command.CohortComplete
            };

            if (command.Action == "playback")
            {
                await _client.SendCachePlaybackAsync(
                    command.Context.BaseUrl, command.Context.Username, command.Context.Password,
                    request, cancellationToken).ConfigureAwait(false);
            }
            else if (command.Action == "prefetch")
            {
                await _client.SendCachePrefetchAsync(
                    command.Context.BaseUrl, command.Context.Username, command.Context.Password,
                    request, cancellationToken).ConfigureAwait(false);
            }
            else if (command.Action == "release")
            {
                await _client.SendCacheReleaseAsync(
                    command.Context.BaseUrl, command.Context.Username, command.Context.Password,
                    request, cancellationToken).ConfigureAwait(false);
            }
        }

        private void RefreshCapabilityIfNeeded()
        {
            var config = _configurationProvider();
            if (config == null || config.ManagedPlaybackCacheEnabled || !config.ManagedPublishingEnabled ||
                config.ManagedPublishingIntegrationId < 1 ||
                string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.Username) ||
                _client == null)
            {
                return;
            }

            var generation = _configurationGeneration;
            var capabilityKey = CreateCapabilityKey(config);
            var snapshot = CreateCapabilitySnapshot(config);
            if (_lifecycleSnapshot == null)
            {
                _lifecycleSnapshot = CreateLifecycleSnapshot(config);
                generation = ++_configurationGeneration;
            }
            if (string.IsNullOrEmpty(capabilityKey))
            {
                return;
            }
            if (_capabilitySnapshot == null)
            {
                _capabilitySnapshot = snapshot;
            }
            if (_capabilityRefresh != null && !_capabilityRefresh.IsCompleted)
            {
                if (!string.Equals(_capabilityRefreshSnapshot, snapshot, StringComparison.Ordinal))
                {
                    QueueCapabilityRefresh();
                }
                return;
            }
            StartCapabilityRefresh(new CapabilityRefreshRequest(
                generation, capabilityKey, config.BaseUrl, config.Username, config.Password, snapshot));
        }

        private void QueueCapabilityRefresh()
        {
            if (_capabilityRefreshQueued || _disposed)
            {
                return;
            }

            _capabilityRefreshQueued = true;
            Task.Run(async () =>
            {
                await Task.Delay(25).ConfigureAwait(false);
                lock (RuntimeGate)
                {
                    _capabilityRefreshQueued = false;
                    if (!_disposed && ReferenceEquals(_current, this) && _cacheCapability == null)
                    {
                        StartCapabilityRefreshFromCurrentConfiguration();
                    }
                }
            });
        }

        private void StartCapabilityRefreshFromCurrentConfiguration()
        {
            var config = _configurationProvider();
            if (config == null || config.ManagedPlaybackCacheEnabled || !config.ManagedPublishingEnabled ||
                config.ManagedPublishingIntegrationId < 1 || string.IsNullOrWhiteSpace(config.BaseUrl) ||
                string.IsNullOrWhiteSpace(config.Username) || _client == null)
            {
                return;
            }

            var capabilityKey = CreateCapabilityKey(config);
            if (string.IsNullOrEmpty(capabilityKey))
            {
                return;
            }

            var snapshot = CreateCapabilitySnapshot(config);
            _capabilitySnapshot = snapshot;
            StartCapabilityRefresh(new CapabilityRefreshRequest(
                _configurationGeneration, capabilityKey, config.BaseUrl, config.Username, config.Password, snapshot));
        }

        private void StartCapabilityRefresh(CapabilityRefreshRequest request)
        {
            _capabilityGeneration = request.Generation;
            _capabilityRefreshSnapshot = request.Snapshot;
            _capabilityRefreshOwner = request;
            _capabilityRefresh = RefreshCapabilityAsync(request);
        }

        private async Task RefreshCapabilityAsync(CapabilityRefreshRequest request)
        {
            try
            {
                var capability = await _client.DiscoverCacheControlCapabilityAsync(
                    request.BaseUrl, request.Username, request.Password, _cancellation.Token).ConfigureAwait(false);
                if (capability == null || _cancellation.IsCancellationRequested) return;
                lock (RuntimeGate)
                {
                    if (!_disposed && ReferenceEquals(_current, this) &&
                        request.Generation == _configurationGeneration && request.Generation == _capabilityGeneration)
                    {
                        _capabilityKey = request.CapabilityKey;
                        _cacheCapability = capability;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
            finally
            {
                lock (RuntimeGate)
                {
                    if (ReferenceEquals(_capabilityRefreshOwner, request))
                    {
                        _capabilityRefreshOwner = null;
                        _capabilityRefresh = null;
                        if (!_disposed && ReferenceEquals(_current, this) &&
                            !string.Equals(_capabilitySnapshot, request.Snapshot, StringComparison.Ordinal))
                        {
                            QueueCapabilityRefresh();
                        }
                    }
                }
            }
        }

        private static string CreateCapabilitySnapshot(PluginConfiguration config)
        {
            if (config == null) return null;
            return string.Join("\n", new[]
            {
                CreateCapabilityKey(config),
                config.ManagedPublishingEnabled.ToString(),
                config.ManagedPlaybackCacheEnabled.ToString(),
                config.ManagedPublishingIntegrationId.ToString(CultureInfo.InvariantCulture),
                config.ManagedActiveGeneration ?? string.Empty,
                config.ManagedMappingsJson ?? string.Empty,
                config.ManagedApprovedOutputRoots ?? string.Empty
            });
        }

        private static string CreateLifecycleSnapshot(PluginConfiguration config)
        {
            if (config == null) return null;
            return string.Join("\n", new[]
            {
                CreateCapabilitySnapshot(config),
                config.ManagedPlaybackCacheMode ?? string.Empty,
                config.ManagedPlaybackCacheNextCount.ToString(CultureInfo.InvariantCulture),
                config.ManagedPlaybackCacheDeleteAfterWatched.ToString(),
                config.ManagedPlaybackCacheDeleteGraceHours.ToString(CultureInfo.InvariantCulture),
                string.Join("\n", config.ManagedPlaybackCacheUserIds ?? new string[0])
            });
        }

        private static string CreateCapabilityKey(PluginConfiguration config)
        {
            return config == null ? null : CreateCapabilityKey(config.BaseUrl, config.Username, config.Password);
        }

        private static string CreateCapabilityKey(string baseUrl, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(baseUrl + "\n" + username + "\n" + (password ?? string.Empty));
                return Convert.ToBase64String(sha256.ComputeHash(bytes));
            }
        }

        private static ManagedPlaybackDispatchContext CreateContext(
            PluginConfiguration config,
            IReadOnlyCollection<string> users)
        {
            if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.Username) ||
                string.IsNullOrEmpty(config.Password) || users == null)
            {
                return null;
            }

            return new ManagedPlaybackDispatchContext
            {
                BaseUrl = config.BaseUrl,
                Username = config.Username,
                Password = config.Password,
                IntegrationId = config.ManagedPublishingIntegrationId,
                ConsumerId = ComputeConsumerId(config.ManagedPublishingIntegrationId, users)
            };
        }

        private static string ComputeConsumerId(int integrationId, IEnumerable<string> users)
        {
            var value = integrationId.ToString(CultureInfo.InvariantCulture) + "\n" +
                string.Join("\n", users.OrderBy(user => user, StringComparer.Ordinal));
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value))
                    .Select(valueByte => valueByte.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static HashSet<string> NormalizeUsers(IEnumerable<string> users)
        {
            return new HashSet<string>(
                (users ?? Enumerable.Empty<string>())
                    .Where(user => !string.IsNullOrWhiteSpace(user))
                    .Select(user => user.Trim()),
                StringComparer.Ordinal);
        }

        private static string FormatEntityId(object value)
        {
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal static string GetSelectedMediaSourcePath(object eventArgs, string fallbackItemPath)
        {
            try
            {
                var mediaSource = eventArgs?.GetType().GetProperty("MediaSource")?.GetValue(eventArgs);
                var path = mediaSource?.GetType().GetProperty("Path")?.GetValue(mediaSource) as string;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is InvalidOperationException ||
                exception is System.Reflection.TargetInvocationException)
            {
                return null;
            }
        }

        private static List<ManagedMappingState> ReadMappings(PluginConfiguration config)
        {
            try
            {
                return string.IsNullOrWhiteSpace(config.ManagedMappingsJson)
                    ? new List<ManagedMappingState>()
                    : JsonSerializer.Deserialize<List<ManagedMappingState>>(config.ManagedMappingsJson) ??
                        new List<ManagedMappingState>();
            }
            catch (JsonException)
            {
                return new List<ManagedMappingState>();
            }
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            return new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            })
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
        }

        private static StringComparer PathComparer => System.IO.Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private sealed class CapabilityRefreshRequest
        {
            public CapabilityRefreshRequest(long generation, string capabilityKey, string baseUrl, string username, string password, string snapshot)
            {
                Generation = generation;
                CapabilityKey = capabilityKey;
                BaseUrl = baseUrl;
                Username = username;
                Password = password;
                Snapshot = snapshot;
            }

            public long Generation { get; private set; }
            public string CapabilityKey { get; private set; }
            public string BaseUrl { get; private set; }
            public string Username { get; private set; }
            public string Password { get; private set; }
            public string Snapshot { get; private set; }
        }

        private sealed class InputEvent
        {
            public string Kind { get; private set; }
            public string SessionId { get; private set; }
            public string ItemId { get; private set; }
            public string Path { get; private set; }
            public bool ConcreteMediaSource { get; private set; }
            public string UserId { get; private set; }
            public bool Played { get; private set; }
            public BaseItem Item { get; private set; }
            public long ConfigurationGeneration { get; private set; }

            public static InputEvent Start(
                string sessionId,
                string itemId,
                string path,
                bool concrete,
                BaseItem item,
                long configurationGeneration)
            {
                return string.IsNullOrWhiteSpace(sessionId) ? null : new InputEvent
                {
                    Kind = "start",
                    SessionId = sessionId,
                    ItemId = itemId,
                    Path = path,
                    ConcreteMediaSource = concrete,
                    Item = item,
                    ConfigurationGeneration = configurationGeneration
                };
            }

            public static InputEvent Progress(string sessionId, long configurationGeneration)
            {
                return string.IsNullOrWhiteSpace(sessionId) ? null : new InputEvent
                {
                    Kind = "progress",
                    SessionId = sessionId,
                    ConfigurationGeneration = configurationGeneration
                };
            }

            public static InputEvent Stop(string sessionId, long configurationGeneration)
            {
                return string.IsNullOrWhiteSpace(sessionId) ? null : new InputEvent
                {
                    Kind = "stop",
                    SessionId = sessionId,
                    ConfigurationGeneration = configurationGeneration
                };
            }

            public static InputEvent Watched(
                string itemId,
                string path,
                string userId,
                bool played,
                BaseItem item,
                long configurationGeneration)
            {
                return string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(userId) ? null : new InputEvent
                {
                    Kind = "watched",
                    ItemId = itemId,
                    Path = path,
                    UserId = userId,
                    Played = played,
                    Item = item,
                    ConfigurationGeneration = configurationGeneration
                };
            }
        }
    }
}
