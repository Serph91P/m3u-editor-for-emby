using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackCacheServiceTests
    {
        [Fact]
        public void ThreeEpisodeLifecycle_StartPrefetchHeartbeatStop_IsOrderedAndSessionSequenced()
        {
            var calls = new ConcurrentQueue<ManagedPlaybackCacheCommand>();
            var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
            using (var service = new ManagedPlaybackCacheService(
                (command, cancellationToken) =>
                {
                    calls.Enqueue(command);
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(30),
                () => now))
            {
                var current = Identity("episode-1", 1);
                service.PlaybackStarted("emby-session", current,
                    new[] { Identity("episode-2", 2) }, Context());
                service.PlaybackStarted("emby-session", current,
                    new[] { Identity("episode-3", 3) }, Context());
                Assert.True(SpinWait.SpinUntil(() => calls.Count >= 2, TimeSpan.FromSeconds(2)));

                service.PlaybackProgress("emby-session");
                Thread.Sleep(30);
                Assert.Equal(2, calls.Count);

                now = now.AddSeconds(30);
                service.PlaybackProgress("emby-session");
                service.PlaybackStopped("emby-session");
                service.PlaybackProgress("emby-session");
                Assert.True(SpinWait.SpinUntil(() => calls.Count >= 4, TimeSpan.FromSeconds(2)));

                var ordered = calls.ToArray();
                Assert.Equal(new[] { "playback", "prefetch", "playback", "playback" },
                    ordered.Select(command => command.Action));
                Assert.Equal(new[] { "start", "heartbeat", "stop" },
                    ordered.Where(command => command.Action == "playback").Select(command => command.State));
                Assert.Equal(new long?[] { 1, 2, 3 },
                    ordered.Where(command => command.Action == "playback").Select(command => command.Sequence));
                Assert.Single(ordered.Select(command => command.SessionId).Where(value => value != null).Distinct());
                Assert.Equal("episode-2", Assert.Single(ordered[1].Targets).CanonicalId);
                Assert.DoesNotContain(ordered, command => command.Action == "release");
            }
        }

        [Fact]
        public void FeatureOff_StopsGrantedLeaseAndRejectsNewWork()
        {
            var calls = new ConcurrentQueue<ManagedPlaybackCacheCommand>();
            using (var service = NewService(calls))
            {
                service.PlaybackStarted("session-1", Identity("episode-1", 1),
                    new ManagedPlaybackIdentity[0], Context());
                Assert.True(SpinWait.SpinUntil(() => calls.Count == 1, TimeSpan.FromSeconds(2)));

                service.SetEnabled(false);
                service.PlaybackStarted("session-2", Identity("episode-2", 2),
                    new ManagedPlaybackIdentity[0], Context());
                Assert.True(SpinWait.SpinUntil(() => calls.Count == 2, TimeSpan.FromSeconds(2)));

                Assert.Equal(new[] { "start", "stop" }, calls.Select(command => command.State));
            }
        }

        [Fact]
        public void Reconcile_StopsOldLeaseAndDropsQueuedPrefetchBeforeReenabling()
        {
            var calls = new ConcurrentQueue<ManagedPlaybackCacheCommand>();
            using var startEntered = new ManualResetEventSlim(false);
            using var releaseStart = new ManualResetEventSlim(false);
            using (var service = new ManagedPlaybackCacheService(
                (command, cancellationToken) =>
                {
                    if (command.Action == "playback" && command.State == "start")
                    {
                        startEntered.Set();
                        releaseStart.Wait(cancellationToken);
                    }

                    calls.Enqueue(command);
                    return Task.CompletedTask;
                }, TimeSpan.FromSeconds(30)))
            {
                service.PlaybackStarted("session-1", Identity("episode-1", 1),
                    new[] { Identity("episode-2", 2) }, Context());
                Assert.True(startEntered.Wait(TimeSpan.FromSeconds(2)));

                service.Reconcile(true);
                releaseStart.Set();
                Assert.True(SpinWait.SpinUntil(() => calls.Count >= 2, TimeSpan.FromSeconds(2)));

                var delivered = calls.ToArray();
                Assert.Equal(new[] { "start", "stop" }, delivered.Select(command => command.State));
                Assert.DoesNotContain(delivered, command => command.Action == "prefetch");
            }
        }

        [Fact]
        public void WatchedRelease_IsSeparateAndUndoRemainsProtective()
        {
            var calls = new ConcurrentQueue<ManagedPlaybackCacheCommand>();
            using (var service = NewService(calls))
            {
                service.WatchedChanged(Identity("episode-1", 1), true, 24, true, Context());
                service.WatchedChanged(Identity("episode-1", 1), false, 24, false, Context());
                Assert.True(SpinWait.SpinUntil(() => calls.Count == 2, TimeSpan.FromSeconds(2)));

                var ordered = calls.ToArray();
                Assert.All(ordered, command => Assert.Equal("release", command.Action));
                Assert.True(ordered[0].Watched);
                Assert.True(ordered[0].CohortComplete);
                Assert.False(ordered[1].Watched);
                Assert.False(ordered[1].CohortComplete);
                Assert.All(ordered, command => Assert.Null(command.State));
            }
        }

        [Fact]
        public void DispatchFailure_DoesNotBreakLaterPlaybackCommands()
        {
            var attempts = 0;
            var delivered = new ConcurrentQueue<string>();
            using (var service = new ManagedPlaybackCacheService(
                (command, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                        throw new InvalidOperationException("synthetic client failure");
                    delivered.Enqueue(command.Action);
                    return Task.CompletedTask;
                }, TimeSpan.FromSeconds(30)))
            {
                service.PlaybackStarted("session-1", Identity("episode-1", 1),
                    new[] { Identity("episode-2", 2) }, Context());
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref attempts) == 2, TimeSpan.FromSeconds(2)));
                Assert.Equal("prefetch", Assert.Single(delivered));
            }
        }

        [Fact]
        public void FeatureOff_DropsQueuedPrefetchAndReleaseButDeliversGrantedLeaseStop()
        {
            var calls = new ConcurrentQueue<ManagedPlaybackCacheCommand>();
            using var startEntered = new ManualResetEventSlim(false);
            using var releaseStart = new ManualResetEventSlim(false);
            using (var service = new ManagedPlaybackCacheService(
                (command, cancellationToken) =>
                {
                    if (command.Action == "playback" && command.State == "start")
                    {
                        startEntered.Set();
                        releaseStart.Wait(cancellationToken);
                    }

                    calls.Enqueue(command);
                    return Task.CompletedTask;
                }, TimeSpan.FromSeconds(30)))
            {
                var current = Identity("episode-1", 1);
                service.PlaybackStarted("session-1", current, new[] { Identity("episode-2", 2) }, Context());
                service.WatchedChanged(current, true, 0, true, Context());
                Assert.True(startEntered.Wait(TimeSpan.FromSeconds(2)));

                service.SetEnabled(false);
                releaseStart.Set();
                Assert.True(SpinWait.SpinUntil(() => calls.Count >= 2, TimeSpan.FromSeconds(2)));

                var delivered = calls.ToArray();
                Assert.Equal(new[] { "playback", "playback" }, delivered.Select(command => command.Action));
                Assert.Equal(new[] { "start", "stop" }, delivered.Select(command => command.State));
                Assert.DoesNotContain(delivered, command => command.Action == "prefetch" || command.Action == "release");
            }
        }

        [Fact]
        public void SelectedMediaSourcePath_RequiresConcreteRuntimeMediaSource()
        {
            Assert.Equal(
                "/managed/selected.strm",
                ManagedPlaybackCacheEntryPoint.GetSelectedMediaSourcePath(
                    new RuntimePlaybackEvent
                    {
                        MediaSource = new RuntimeMediaSource { Path = "/managed/selected.strm" }
                    },
                    "/managed/item.strm"));
            Assert.Null(ManagedPlaybackCacheEntryPoint.GetSelectedMediaSourcePath(new object(), "/managed/item.strm"));
        }

        private static ManagedPlaybackCacheService NewService(
            ConcurrentQueue<ManagedPlaybackCacheCommand> calls)
        {
            return new ManagedPlaybackCacheService(
                (command, cancellationToken) =>
                {
                    calls.Enqueue(command);
                    return Task.CompletedTask;
                }, TimeSpan.FromSeconds(30));
        }

        private static ManagedPlaybackDispatchContext Context()
        {
            return new ManagedPlaybackDispatchContext
            {
                BaseUrl = "https://editor.example",
                Username = "account",
                Password = "credential",
                IntegrationId = 7,
                ConsumerId = new string('b', 64)
            };
        }

        private static ManagedPlaybackIdentity Identity(string canonicalId, int episode)
        {
            return new ManagedPlaybackIdentity
            {
                MappingUuid = "123e4567-e89b-12d3-a456-426614174000",
                Revision = new string('a', 64),
                CanonicalId = canonicalId,
                VariantKey = "1080p",
                SeriesCanonicalId = "series-1",
                SeasonNumber = 1,
                EpisodeNumber = episode,
                ManagedRoot = "/managed"
            };
        }
        private sealed class RuntimePlaybackEvent
        {
            public RuntimeMediaSource MediaSource { get; set; }
        }

        private sealed class RuntimeMediaSource
        {
            public string Path { get; set; }
        }
    }
}
