using System;
using Emby.M3uEditor.Plugin;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;

namespace Emby.M3uEditor.Plugin.Service
{
    /// <summary>
    /// Thin Emby lifecycle adapter. It deliberately subscribes only after Run
    /// and delegates work to the bounded cache service, keeping playback event
    /// threads free of network and configuration I/O.
    /// </summary>
    public sealed class ManagedPlaybackCacheEntryPoint : IServerEntryPoint, IDisposable
    {
        private readonly ISessionManager _sessionManager;
        private readonly ManagedPlaybackCacheService _cache;
        private bool _running;
        private bool _disposed;

        public ManagedPlaybackCacheEntryPoint(ISessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _cache = new ManagedPlaybackCacheService((action, sequence) => { }, TimeSpan.FromSeconds(30));
        }

        public void Run()
        {
            if (_disposed || _running)
            {
                return;
            }

            _running = true;
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_running)
            {
                _sessionManager.PlaybackStart -= OnPlaybackStart;
                _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            }

            _cache.Dispose();
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs args)
        {
            _cache.SetEnabled(Plugin.InstanceOrNull?.Configuration?.ManagedPlaybackCacheEnabled == true);
            if (args?.PlaySessionId != null)
            {
                _cache.PlaybackStarted(args.PlaySessionId);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs args)
        {
            if (args?.PlaySessionId != null)
            {
                _cache.PlaybackStopped(args.PlaySessionId);
            }
        }
    }
}
