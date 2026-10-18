using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public partial class ManagedPlaybackCacheEntryPointLifecycleTests
    {
        [Theory]
        [InlineData("start", "disable")]
        [InlineData("start", "reconcile")]
        [InlineData("start", "revision")]
        [InlineData("prefetch", "disable")]
        [InlineData("prefetch", "reconcile")]
        [InlineData("prefetch", "revision")]
        [InlineData("watched", "disable")]
        [InlineData("watched", "reconcile")]
        [InlineData("watched", "revision")]
        [InlineData("start", "dispose")]
        [InlineData("prefetch", "dispose")]
        [InlineData("watched", "dispose")]
        [InlineData("stop", "dispose")]
        public async Task Dispatch_DiscoveryCrossesConfigurationBoundary_OnlyLeaseStopSurvives(string input, string change)
        {
            var root = System.IO.Path.Combine(TempDir.Path, "approved", "dispatch-fence");
            var mappingUuid = "123e4567-e89b-12d3-a456-426614174005";
            var revision = new string('a', 64);
            var paths = WriteManifest(root, mappingUuid, revision);
            var episodes = paths.Select(path => new Episode { Id = Guid.NewGuid(), Path = path }).ToArray();
            var config = DefaultConfig();
            config.ManagedPublishingEnabled = true;
            config.ManagedPlaybackCacheEnabled = true;
            config.ManagedPlaybackCacheMode = "next";
            config.ManagedPlaybackCacheDeleteAfterWatched = true;
            var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            config.ManagedPlaybackCacheUserIds = new[] { userId.ToString("D") };
            config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revision, Success = true } });
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            ((LifecycleProxy)(object)library).Items = episodes.ToDictionary(item => item.Path, item => (BaseItem)item, StringComparer.Ordinal);
            using (var handler = new DispatchFenceHandler())
            using (var client = new HttpClient(handler))
            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () => config, () => client, () => DateTime.UtcNow, http => new M3uEditorClient(http, (host, token) => Task.FromResult(new[] { IPAddress.Loopback }))))
            {
                // Watched needs an existing session so reconcile queues a real stop as
                // a FIFO completion barrier, after the potentially stale release.
                handler.BlockDiscovery = input == "start";
                if (input == "prefetch")
                {
                    handler.AfterStart = () =>
                    {
                        typeof(ManagedPlaybackCacheEntryPoint).GetField("_cacheCapability", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entryPoint, null);
                        handler.BlockDiscovery = true;
                    };
                }
                entryPoint.Run();
                ((LifecycleProxy)(object)sessions).Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "dispatch-fence", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                if (input == "watched" || input == "stop")
                {
                    await handler.InitialRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    typeof(ManagedPlaybackCacheEntryPoint).GetField("_cacheCapability", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entryPoint, null);
                    handler.Bodies.Clear();
                    handler.BlockDiscovery = true;
                    if (input == "watched")
                        ((LifecycleProxy)(object)users).Raise("UserDataSaved", new UserDataSaveEventArgs { User = new User { Id = userId }, Item = episodes[0], UserData = new UserItemData { Played = true } });
                    else
                        ((LifecycleProxy)(object)sessions).Raise("PlaybackStopped", new PlaybackStopEventArgs { PlaySessionId = "dispatch-fence" });
                }
                Task disposal = null;
                try
                {
                    await handler.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (input == "prefetch") handler.Bodies.Clear();
                    if (change == "disable") config.ManagedPlaybackCacheEnabled = false;
                    if (change == "revision")
                    {
                        var nextRevision = new string('b', 64);
                        WriteManifest(root, mappingUuid, nextRevision);
                        config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = nextRevision, Success = true } });
                    }
                    if (change == "dispose")
                    {
                        disposal = Task.Run(entryPoint.Dispose);
                        Assert.True(SpinWait.SpinUntil(() =>
                            (bool)typeof(ManagedPlaybackCacheEntryPoint).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(entryPoint), TimeSpan.FromSeconds(5)));
                    }
                    else
                        ManagedPlaybackCacheEntryPoint.NotifyConfigurationChanged(config.ManagedPlaybackCacheEnabled);
                    handler.ContinueDiscovery.TrySetResult(true);
                    await handler.StopSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (disposal != null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
                    var actions = handler.Bodies.Select(ParseForm).Where(form => form.TryGetValue("action", out var action) && action.StartsWith("m3u_editor_cache_", StringComparison.Ordinal)).ToArray();
                    var stop = Assert.Single(actions);
                    Assert.Equal("m3u_editor_cache_playback", stop["action"]);
                    Assert.Equal("stop", stop["state"]);
                    Assert.Equal(revision, stop["revision"]);
                }
                finally
                {
                    handler.ContinueDiscovery.TrySetResult(true);
                    if (disposal != null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }

        private sealed class DispatchFenceHandler : HttpMessageHandler
        {
            public bool BlockDiscovery;
            public Action AfterStart;
            public ConcurrentQueue<string> Bodies { get; } = new ConcurrentQueue<string>();
            public TaskCompletionSource<bool> DiscoveryEntered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> ContinueDiscovery { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> InitialRelease { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> StopSeen { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
                Bodies.Enqueue(body);
                var form = ParseForm(body);
                if (!form.TryGetValue("action", out var action) || !action.StartsWith("m3u_editor_cache_", StringComparison.Ordinal))
                {
                    if (BlockDiscovery)
                    {
                        DiscoveryEntered.TrySetResult(true);
                        await ContinueDiscovery.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CapabilityJson) };
                }
                if (action == "m3u_editor_cache_release") InitialRelease.TrySetResult(true);
                if (form.TryGetValue("state", out var playbackState) && playbackState == "start") AfterStart?.Invoke();
                if (form.TryGetValue("state", out var state) && state == "stop") StopSeen.TrySetResult(true);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"api_version\":1,\"data\":{\"request_id\":\"" + form["request_id"] + "\",\"duplicate\":false,\"results\":[]}}") };
            }
        }
    }
}
