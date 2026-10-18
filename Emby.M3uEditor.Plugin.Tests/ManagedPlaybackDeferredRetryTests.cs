using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public partial class ManagedPlaybackCacheEntryPointLifecycleTests
    {
        [Theory]
        [InlineData("deferred", 2, false)]
        [InlineData("queued", 1, false)]
        [InlineData("already_queued", 1, false)]
        [InlineData("already_cached", 1, false)]
        [InlineData("Deferred", 1, false)]
        [InlineData("unknown", 1, false)]
        [InlineData("queued", 1, true)]
        public void DeferredPrefetch_ActiveProgress_RetriesOnlyDeferredTargetsOnceWithNewRequestId(string status, int expectedAttempts, bool injectUnsentDeferred)
        {
            var root = Path.Combine(TempDir.Path, "approved", "library", "mapping");
            const string mappingUuid = "123e4567-e89b-12d3-a456-426614174000";
            var revision = new string('a', 64);
            var paths = WriteManifest(root, mappingUuid, revision);
            var item = new Episode { Id = Guid.NewGuid(), Path = paths[0] };
            var config = DefaultConfig();
            config.ManagedPublishingEnabled = true;
            config.ManagedPublishingIntegrationId = 7;
            config.ManagedApprovedOutputRoots = Path.Combine(TempDir.Path, "approved");
            config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revision, Success = true } });
            config.ManagedPlaybackCacheEnabled = true;
            config.ManagedPlaybackCacheMode = "next_n";
            config.ManagedPlaybackCacheNextCount = 2;
            config.ManagedPlaybackCacheDeleteAfterWatched = true;
            config.ManagedPlaybackCacheUserIds = new[] { "11111111111111111111111111111111" };
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            var proxy = (LifecycleProxy)(object)sessions;
            var now = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);
            using (var handler = new DeferredPrefetchHandler { EpisodeTwoStatus = status, InjectUnsentDeferred = injectUnsentDeferred })
            using (var client = new HttpClient(handler))
            using (var entryPoint = new Service.ManagedPlaybackCacheEntryPoint(sessions, users, library, () => config, () => client, () => now, controlled => new M3uEditorClient(controlled, (host, token) => Task.FromResult(new[] { IPAddress.Loopback }))))
            {
                entryPoint.Run();
                proxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = item, PlaySessionId = "deferred-session", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                // The serial consumer sees this release only after consuming the
                // first real, identity-echoed deferred prefetch response.
                Assert.True(SpinWait.SpinUntil(() => handler.Forms.Any(form => form["action"] == "m3u_editor_cache_release"), TimeSpan.FromSeconds(2)));
                Assert.Single(handler.Prefetches);
                now = now.AddSeconds(90);
                proxy.Raise("PlaybackProgress", new RuntimePlaybackProgressEventArgs { Item = item, PlaySessionId = "deferred-session" });
                Assert.True(SpinWait.SpinUntil(() => handler.HeartbeatCount == 1, TimeSpan.FromSeconds(2)));
                Assert.Single(handler.Prefetches);
                now = now.AddSeconds(30);
                proxy.Raise("PlaybackProgress", new RuntimePlaybackProgressEventArgs { Item = item, PlaySessionId = "deferred-session" });
                Assert.True(SpinWait.SpinUntil(() => handler.HeartbeatCount == 2, TimeSpan.FromSeconds(2)));
                Assert.True(SpinWait.SpinUntil(() => handler.Prefetches.Length == expectedAttempts, TimeSpan.FromSeconds(2)));
                now = now.AddSeconds(120);
                proxy.Raise("PlaybackProgress", new RuntimePlaybackProgressEventArgs { Item = item, PlaySessionId = "deferred-session" });
                Assert.True(SpinWait.SpinUntil(() => handler.HeartbeatCount == 3, TimeSpan.FromSeconds(2)));
                var attempts = handler.Prefetches;
                Assert.Equal(expectedAttempts, attempts.Length);
                if (expectedAttempts == 1) return;
                Assert.NotEqual(attempts[0]["request_id"], attempts[1]["request_id"]);
                Assert.Equal("episode-2", attempts[1]["targets[0][canonical_id]"]);
                Assert.DoesNotContain("targets[1][canonical_id]", attempts[1].Keys);
                Assert.Equal(attempts[0]["consumer_id"], attempts[1]["consumer_id"]);
                Assert.Equal(attempts[0]["revision"], attempts[1]["revision"]);
            }
        }

        private sealed class DeferredPrefetchHandler : HttpMessageHandler
        {
            public string EpisodeTwoStatus { get; set; } = "deferred";
            public bool InjectUnsentDeferred { get; set; }
            public ConcurrentQueue<IDictionary<string, string>> Forms { get; } = new ConcurrentQueue<IDictionary<string, string>>();
            public IDictionary<string, string>[] Prefetches => Forms.Where(form => form["action"] == "m3u_editor_cache_prefetch").ToArray();
            public int HeartbeatCount => Forms.Count(form => form.TryGetValue("state", out var state) && state == "heartbeat");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
                var form = ParseForm(body);
                string response;
                if (!form.ContainsKey("request_id")) response = InjectUnsentDeferred ? CapabilityJson.Replace("\"max_targets\":100", "\"max_targets\":1") : CapabilityJson;
                else
                {
                    Forms.Enqueue(form);
                    var results = new List<object>();
                    if (form["action"] == "m3u_editor_cache_prefetch")
                    {
                        for (var index = 0; form.TryGetValue("targets[" + index + "][canonical_id]", out var id); index++)
                            results.Add(new { canonical_id = id, variant_key = form["targets[" + index + "][variant_key]"], status = id == "episode-2" ? EpisodeTwoStatus : "queued" });
                        if (InjectUnsentDeferred)
                            results.Add(new { canonical_id = "episode-3", variant_key = "variant-A", status = "deferred" });
                    }
                    response = JsonSerializer.Serialize(new { api_version = 1, data = new { request_id = form["request_id"], duplicate = false, results } });
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            }
        }
    }
}
