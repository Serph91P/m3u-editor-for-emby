using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.M3uEditor.Plugin.Service
{
    /// <summary>
    /// Owns the bounded, asynchronous cache-control command stream for active
    /// playback sessions. Playback callbacks only enqueue work; consumers must
    /// never perform network I/O on Emby's playback thread.
    /// </summary>
    public sealed class ManagedPlaybackCacheService : IDisposable
    {
        private readonly Action<string, long> _send;
        private readonly BlockingCollection<Command> _commands = new BlockingCollection<Command>(100);
        private readonly HashSet<string> _activeSessions = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private readonly Task _consumer;
        private bool _enabled = true;
        private bool _disposed;
        private long _sequence;

        public ManagedPlaybackCacheService(Action<string, long> send, TimeSpan heartbeatInterval)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
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
                if (_disposed)
                {
                    return;
                }

                _enabled = enabled;
            }
        }

        public void PlaybackStarted(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || !_enabled || !_activeSessions.Add(sessionId))
                {
                    return;
                }

                Enqueue("playback");
                Enqueue("prefetch");
            }
        }

        public void PlaybackStopped(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || !_activeSessions.Remove(sessionId))
                {
                    return;
                }

                Enqueue("release");
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

                _disposed = true;
                _enabled = false;
                _activeSessions.Clear();
                _commands.CompleteAdding();
            }

            _consumer.GetAwaiter().GetResult();
            _commands.Dispose();
        }

        private void Enqueue(string action)
        {
            if (!_commands.IsAddingCompleted)
            {
                _commands.TryAdd(new Command(action, ++_sequence));
            }
        }

        private void Consume()
        {
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                try
                {
                    _send(command.Action, command.Sequence);
                }
                catch
                {
                    // Cache-control failures are isolated from playback.
                }
            }
        }

        private sealed class Command
        {
            public Command(string action, long sequence)
            {
                Action = action;
                Sequence = sequence;
            }

            public string Action { get; }
            public long Sequence { get; }
        }
    }
}
