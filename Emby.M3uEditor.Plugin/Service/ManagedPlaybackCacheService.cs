using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.M3uEditor.Plugin.Service
{
    public sealed class ManagedPlaybackDispatchContext
    {
        public string BaseUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int IntegrationId { get; set; }
        public string ConsumerId { get; set; }
    }

    public sealed class ManagedPlaybackCacheCommand
    {
        public string Action { get; set; }
        public string RequestId { get; set; }
        public ManagedPlaybackDispatchContext Context { get; set; }
        public ManagedPlaybackIdentity Identity { get; set; }
        public IReadOnlyList<ManagedPlaybackIdentity> Targets { get; set; }
        public string SessionId { get; set; }
        public long? Sequence { get; set; }
        public string State { get; set; }
        public bool? Watched { get; set; }
        public int? DeleteAfterHours { get; set; }
        public bool? CohortComplete { get; set; }
        internal long Generation { get; set; }
    }

    /// <summary>
    /// Owns the bounded asynchronous cache-control stream. Callers only enqueue
    /// immutable commands; the single consumer isolates all network failures.
    /// </summary>
    public sealed class ManagedPlaybackCacheService : IDisposable
    {
        private readonly Func<ManagedPlaybackCacheCommand, CancellationToken, Task> _send;
        private readonly TimeSpan _heartbeatInterval;
        private readonly Func<DateTime> _utcNow;
        private readonly BlockingCollection<ManagedPlaybackCacheCommand> _commands =
            new BlockingCollection<ManagedPlaybackCacheCommand>(100);
        private readonly Dictionary<string, SessionState> _activeSessions =
            new Dictionary<string, SessionState>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _terminalSessions =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly Task _consumer;
        private bool _enabled = true;
        private long _generation;
        private bool _disposed;

        public ManagedPlaybackCacheService(
            Func<ManagedPlaybackCacheCommand, CancellationToken, Task> send,
            TimeSpan heartbeatInterval,
            Func<DateTime> utcNow = null)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            if (heartbeatInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
            }

            _heartbeatInterval = heartbeatInterval;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _consumer = Task.Factory.StartNew(
                Consume,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void SetEnabled(bool enabled)
        {
            lock (_gate)
            {
                if (_disposed || _enabled == enabled)
                {
                    return;
                }

                if (!enabled)
                {
                    _generation++;
                    StopAllSessions(_utcNow());
                }

                _enabled = enabled;
            }
        }

        /// <summary>
        /// Invalidates queued work bound to the old configuration or manifest,
        /// while preserving best-effort stops for already granted leases.
        /// </summary>
        public void Reconcile(bool enabled)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _generation++;
                StopAllSessions(_utcNow());
                _enabled = enabled;
            }
        }

        public bool PlaybackStarted(
            string embySessionId,
            ManagedPlaybackIdentity current,
            IEnumerable<ManagedPlaybackIdentity> prefetchTargets,
            ManagedPlaybackDispatchContext context)
        {
            if (!IsValidSessionInput(embySessionId, current, context))
            {
                return false;
            }

            lock (_gate)
            {
                var now = _utcNow();
                PruneTerminalSessions(now);
                if (_disposed || !_enabled || _activeSessions.ContainsKey(embySessionId) ||
                    _terminalSessions.ContainsKey(embySessionId))
                {
                    return false;
                }

                var state = new SessionState
                {
                    ProtocolSessionId = Guid.NewGuid().ToString("D"),
                    Identity = current,
                    Context = context,
                    Sequence = 1,
                    LastHeartbeatUtc = now
                };
                var start = CreatePlaybackCommand(state, "start", _generation);
                if (!TryEnqueue(start))
                {
                    return false;
                }

                _activeSessions.Add(embySessionId, state);
                var targets = NormalizePrefetchTargets(current, prefetchTargets);
                if (targets.Count > 0)
                {
                    TryEnqueue(new ManagedPlaybackCacheCommand
                    {
                        Action = "prefetch",
                        RequestId = Guid.NewGuid().ToString("D"),
                        Context = context,
                        Identity = current,
                        Targets = targets,
                        Generation = _generation
                    });
                }

                return true;
            }
        }

        public void PlaybackProgress(string embySessionId)
        {
            if (string.IsNullOrWhiteSpace(embySessionId))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || !_enabled || !_activeSessions.TryGetValue(embySessionId, out var session))
                {
                    return;
                }

                var now = _utcNow();
                if (now - session.LastHeartbeatUtc < _heartbeatInterval)
                {
                    return;
                }

                session.Sequence++;
                session.LastHeartbeatUtc = now;
                TryEnqueue(CreatePlaybackCommand(session, "heartbeat", _generation));
            }
        }

        public void PlaybackStopped(string embySessionId)
        {
            if (string.IsNullOrWhiteSpace(embySessionId))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || !_activeSessions.TryGetValue(embySessionId, out var session))
                {
                    return;
                }

                _activeSessions.Remove(embySessionId);
                var now = _utcNow();
                _terminalSessions[embySessionId] = now.AddSeconds(120);
                session.Sequence++;
                TryEnqueue(CreatePlaybackCommand(session, "stop", _generation));
            }
        }

        public void WatchedChanged(
            ManagedPlaybackIdentity identity,
            bool watched,
            int deleteAfterHours,
            bool cohortComplete,
            ManagedPlaybackDispatchContext context)
        {
            if (identity == null || context == null || deleteAfterHours < 0 || deleteAfterHours > 168)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || !_enabled)
                {
                    return;
                }

                TryEnqueue(new ManagedPlaybackCacheCommand
                {
                    Action = "release",
                    RequestId = Guid.NewGuid().ToString("D"),
                    Context = context,
                    Identity = identity,
                    Targets = new[] { identity },
                    Watched = watched,
                    DeleteAfterHours = deleteAfterHours,
                    CohortComplete = cohortComplete,
                    Generation = _generation
                });
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                StopAllSessions(_utcNow());
                _enabled = false;
                _disposed = true;
                _commands.CompleteAdding();
            }

            try
            {
                if (!_consumer.Wait(TimeSpan.FromSeconds(2)))
                {
                    _cancellation.Cancel();
                    _consumer.Wait(TimeSpan.FromSeconds(1));
                }
            }
            catch (AggregateException)
            {
            }
            finally
            {
                _cancellation.Cancel();
                _cancellation.Dispose();
                _commands.Dispose();
            }
        }

        private void StopAllSessions(DateTime now)
        {
            foreach (var pair in _activeSessions.ToList())
            {
                pair.Value.Sequence++;
                TryEnqueue(CreatePlaybackCommand(pair.Value, "stop", _generation));
                _terminalSessions[pair.Key] = now.AddSeconds(120);
            }

            _activeSessions.Clear();
        }

        private static ManagedPlaybackCacheCommand CreatePlaybackCommand(SessionState session, string state, long generation)
        {
            return new ManagedPlaybackCacheCommand
            {
                Action = "playback",
                RequestId = Guid.NewGuid().ToString("D"),
                Context = session.Context,
                Identity = session.Identity,
                Targets = new[] { session.Identity },
                SessionId = session.ProtocolSessionId,
                Sequence = session.Sequence,
                State = state,
                Generation = generation
            };
        }

        private static IReadOnlyList<ManagedPlaybackIdentity> NormalizePrefetchTargets(
            ManagedPlaybackIdentity current,
            IEnumerable<ManagedPlaybackIdentity> targets)
        {
            return (targets ?? Enumerable.Empty<ManagedPlaybackIdentity>())
                .Where(target => target != null &&
                    string.Equals(target.MappingUuid, current.MappingUuid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(target.Revision, current.Revision, StringComparison.Ordinal) &&
                    string.Equals(target.VariantKey, current.VariantKey, StringComparison.Ordinal) &&
                    !string.Equals(target.CanonicalId, current.CanonicalId, StringComparison.Ordinal))
                .GroupBy(target => target.CanonicalId + "\n" + target.VariantKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(100)
                .ToList();
        }

        private static bool IsValidSessionInput(
            string embySessionId,
            ManagedPlaybackIdentity identity,
            ManagedPlaybackDispatchContext context)
        {
            return !string.IsNullOrWhiteSpace(embySessionId) && identity != null && context != null &&
                context.IntegrationId > 0 && !string.IsNullOrWhiteSpace(context.ConsumerId) &&
                !string.IsNullOrWhiteSpace(identity.MappingUuid) && !string.IsNullOrWhiteSpace(identity.Revision) &&
                !string.IsNullOrWhiteSpace(identity.CanonicalId) && !string.IsNullOrWhiteSpace(identity.VariantKey);
        }

        private bool TryEnqueue(ManagedPlaybackCacheCommand command)
        {
            return !_commands.IsAddingCompleted && _commands.TryAdd(command);
        }

        private void PruneTerminalSessions(DateTime now)
        {
            foreach (var key in _terminalSessions.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
            {
                _terminalSessions.Remove(key);
            }
        }

        private void Consume()
        {
            try
            {
                foreach (var command in _commands.GetConsumingEnumerable(_cancellation.Token))
                {
                    if (!CanDispatch(command))
                    {
                        continue;
                    }

                    try
                    {
                        _send(command, _cancellation.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        // Cache-control failures are isolated from playback.
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private bool CanDispatch(ManagedPlaybackCacheCommand command)
        {
            lock (_gate)
            {
                return IsLeaseStop(command) ||
                    (_enabled && command != null && command.Generation == _generation);
            }
        }

        private static bool IsLeaseStop(ManagedPlaybackCacheCommand command)
        {
            return command != null &&
                string.Equals(command.Action, "playback", StringComparison.Ordinal) &&
                string.Equals(command.State, "stop", StringComparison.Ordinal);
        }

        private sealed class SessionState
        {
            public string ProtocolSessionId { get; set; }
            public ManagedPlaybackIdentity Identity { get; set; }
            public ManagedPlaybackDispatchContext Context { get; set; }
            public long Sequence { get; set; }
            public DateTime LastHeartbeatUtc { get; set; }
        }
    }
}
