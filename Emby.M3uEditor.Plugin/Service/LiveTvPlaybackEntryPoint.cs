using System;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using Emby.M3uEditor.Plugin.Util;

namespace Emby.M3uEditor.Plugin.Service
{
    // Keep the original open's ownership when native PlaybackInfo replaces only the play session.
    public sealed class LiveTvPlaybackEntryPoint : IServerEntryPoint, IDisposable
    {
        private readonly ISessionManager _sessions;
        private readonly IMediaSourceManager _sources;
        private readonly ILogger _logger;
        private readonly MethodInfo _closeConsumer;
        private bool _running;

        public LiveTvPlaybackEntryPoint(ISessionManager sessions, IMediaSourceManager sources, ILogManager logManager)
        {
            _sessions = sessions;
            _sources = sources;
            _logger = logManager?.GetLogger("M3uEditor.LiveTv");
            // The 4.8 SDK only exposes close-all; never use it for consumer cleanup.
            _closeConsumer = typeof(IMediaSourceManager).GetMethod("CloseLiveStream", new[] { typeof(string), typeof(string) });
        }

        public void Run()
        {
            if (_running || _closeConsumer == null) return;
            _running = true;
            _sessions.PlaybackStart += OnPlaybackStart;
            _sessions.PlaybackStopped += OnPlaybackStopped;
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            _sessions.PlaybackStart -= OnPlaybackStart;
            _sessions.PlaybackStopped -= OnPlaybackStopped;
        }

        private M3uEditorLiveStream FindStream(PlaybackProgressEventArgs args)
        {
            if (string.IsNullOrEmpty(args?.MediaSourceId)) return null;
            return _sources.GetLiveStreamInfoByUniqueId(args.MediaSourceId) as M3uEditorLiveStream;
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs args)
        {
            FindStream(args)?.BindPlaybackSession(args.Session, args.PlaySessionId, _sessions.Sessions);
        }

        private async void OnPlaybackStopped(object sender, PlaybackStopEventArgs args)
        {
            try
            {
                // Unknown/late stops have no native play session and cannot acquire ownership.
                if (args?.PlaySession == null || args.PlaySession.Id != args.PlaySessionId) return;
                var stream = FindStream(args);
                // Native events are queued; a stop may arrive before its start callback.
                stream?.BindPlaybackSession(args.Session, args.PlaySessionId, _sessions.Sessions, args.PlaySession);
                var consumerId = stream?.TakeRenegotiatedConsumer(args.Session, args.PlaySessionId);
                if (consumerId != null)
                    await ((Task)_closeConsumer.Invoke(_sources, new object[] { stream.MediaSource.LiveStreamId, consumerId })).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (Diagnostics.IsEnabled)
                    _logger?.Warn("Live TV renegotiated playback cleanup failed.");
            }
        }
    }
}
