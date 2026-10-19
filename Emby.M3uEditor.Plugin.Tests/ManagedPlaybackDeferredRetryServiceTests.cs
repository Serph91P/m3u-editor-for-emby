using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackDeferredRetryServiceTests
    {
        [Theory]
        [InlineData("stop")]
        [InlineData("reconcile")]
        [InlineData("disable")]
        [InlineData("dispose")]
        [InlineData("watched_then_undo")]
        [InlineData("watched_uppercase_then_undo")]
        public void DeferredRetry_RespectsLifecycleAndWatchedFences(string fence)
        {
            var calls = new ConcurrentQueue<ManagedPlaybackCacheCommand>();
            using var acknowledged = new ManualResetEventSlim();
            var now = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);
            var next = Identity("episode-2");
            var later = Identity("episode-3");
            var context = new ManagedPlaybackDispatchContext { BaseUrl = "https://editor.example.test", Username = "synthetic", Password = "synthetic", IntegrationId = 7, ConsumerId = new string('b', 64) };
            ManagedPlaybackCacheService service = null;
            service = new ManagedPlaybackCacheService((command, token) =>
            {
                calls.Enqueue(command);
                if (command.Action == "prefetch" && !command.IsDeferredRetry)
                {
                    service.RecordDeferredPrefetch(command, new[] { next, later });
                    acknowledged.Set();
                }
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(30), () => now);
            using (service)
            {
                service.PlaybackStarted("native-session", Identity("episode-1"), new[] { next, later }, context);
                Assert.True(acknowledged.Wait(TimeSpan.FromSeconds(2)));
                switch (fence)
                {
                    case "stop": service.PlaybackStopped("native-session"); break;
                    case "reconcile": service.Reconcile(true); break;
                    case "disable": service.SetEnabled(false); break;
                    case "dispose": service.Dispose(); break;
                    case "watched_then_undo":
                        service.WatchedChanged(next, true, 0, true, context);
                        service.WatchedChanged(next, false, 0, false, context);
                        break;
                    case "watched_uppercase_then_undo":
                        var watchedIdentity = Identity("episode-2");
                        watchedIdentity.MappingUuid = watchedIdentity.MappingUuid.ToUpperInvariant();
                        service.WatchedChanged(watchedIdentity, true, 0, true, context);
                        service.WatchedChanged(watchedIdentity, false, 0, false, context);
                        break;
                }
                now = now.AddSeconds(120);
                service.PlaybackProgress("native-session");
                if (fence == "watched_then_undo" || fence == "watched_uppercase_then_undo")
                {
                    Assert.True(SpinWait.SpinUntil(() => calls.Count(command => command.Action == "prefetch") == 2, TimeSpan.FromSeconds(2)));
                    var retry = Assert.Single(calls.Where(command => command.IsDeferredRetry));
                    Assert.Equal("episode-3", Assert.Single(retry.Targets).CanonicalId);
                }
                else
                {
                    service.Dispose(); // joins the consumer, including best-effort stops
                    Assert.Single(calls.Where(command => command.Action == "prefetch"));
                    Assert.DoesNotContain(calls, command => command.IsDeferredRetry);
                    Assert.Contains(calls, command => command.State == "stop");
                }
            }
        }

        [Theory]
        [InlineData("stop")]
        [InlineData("reconcile")]
        [InlineData("disable")]
        [InlineData("watched")]
        [InlineData("watched_uppercase")]
        public void QueuedDeferredRetry_RechecksFencesAfterHeartbeatUnblocks(string fence)
        {
            var calls = new ConcurrentQueue<ManagedPlaybackCacheCommand>();
            using var acknowledged = new ManualResetEventSlim();
            using var heartbeatEntered = new ManualResetEventSlim();
            using var unblockHeartbeat = new ManualResetEventSlim();
            using var barrierProcessed = new ManualResetEventSlim();
            var now = DateTime.UtcNow;
            var next = Identity("episode-2");
            var context = new ManagedPlaybackDispatchContext { BaseUrl = "https://editor.example.test", Username = "synthetic", Password = "synthetic", IntegrationId = 7, ConsumerId = new string('b', 64) };
            ManagedPlaybackCacheService service = null;
            service = new ManagedPlaybackCacheService((command, token) =>
            {
                calls.Enqueue(command);
                if (command.Action == "prefetch" && !command.IsDeferredRetry)
                {
                    service.RecordDeferredPrefetch(command, new[] { next });
                    acknowledged.Set();
                }
                if (command.State == "heartbeat")
                {
                    heartbeatEntered.Set();
                    unblockHeartbeat.Wait(token);
                }
                if (command.Action == "release" && command.Identity.CanonicalId == "episode-1")
                    barrierProcessed.Set();
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(30), () => now);
            using (service)
            {
                service.PlaybackStarted("native-session", Identity("episode-1"), new[] { next }, context);
                Assert.True(acknowledged.Wait(TimeSpan.FromSeconds(2)));
                now = now.AddSeconds(120);
                service.PlaybackProgress("native-session");
                Assert.True(heartbeatEntered.Wait(TimeSpan.FromSeconds(2)));
                try
                {
                    if (fence == "stop") service.PlaybackStopped("native-session");
                    if (fence == "reconcile") service.Reconcile(true);
                    if (fence == "disable") service.SetEnabled(false);
                    if (fence == "watched") service.WatchedChanged(next, true, 0, true, context);
                    if (fence == "watched_uppercase")
                    {
                        var watchedIdentity = Identity("episode-2");
                        watchedIdentity.MappingUuid = watchedIdentity.MappingUuid.ToUpperInvariant();
                        service.WatchedChanged(watchedIdentity, true, 0, true, context);
                    }
                    if (fence == "disable") service.SetEnabled(true);
                    service.WatchedChanged(Identity("episode-1"), false, 0, false, context);
                }
                finally { unblockHeartbeat.Set(); }
                Assert.True(barrierProcessed.Wait(TimeSpan.FromSeconds(2)));
                Assert.Single(calls.Where(command => command.Action == "prefetch"));
                Assert.DoesNotContain(calls, command => command.IsDeferredRetry);
            }
        }

        private static ManagedPlaybackIdentity Identity(string id) => new ManagedPlaybackIdentity
        {
            MappingUuid = "123e4567-e89b-12d3-a456-426614174000", Revision = new string('a', 64),
            CanonicalId = id, VariantKey = "variant-A", SeriesCanonicalId = "series-1", ManagedRoot = "/managed"
        };
    }
}
