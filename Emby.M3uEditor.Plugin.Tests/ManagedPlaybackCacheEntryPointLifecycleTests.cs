using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    [Collection(PluginSingletonCollection.Name)]
    public class ManagedPlaybackCacheEntryPointLifecycleTests : SyncTestBase
    {
        [Fact]
        public void LifecycleFixture_EntryPointExposesInternalRuntimeSeam()
        {
            var constructor = typeof(ManagedPlaybackCacheEntryPoint).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ISessionManager), typeof(IUserDataManager), typeof(ILibraryManager), typeof(Func<PluginConfiguration>), typeof(Func<HttpClient>), typeof(Func<DateTime>) }, null);
            Assert.NotNull(constructor);
        }

        [Fact]
        public void LifecycleFixture_EntryPointCanCreateTheRealClientWithControlledDns()
        {
            var constructor = typeof(ManagedPlaybackCacheEntryPoint).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ISessionManager), typeof(IUserDataManager), typeof(ILibraryManager), typeof(Func<PluginConfiguration>), typeof(Func<HttpClient>), typeof(Func<DateTime>), typeof(Func<HttpClient, M3uEditorClient>) }, null);
            Assert.NotNull(constructor);
        }

        [Fact]
        public void Run_RegistersRealLifecycleCallbacksAndUsesInjectedClientFactory()
        {
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            var sessionProxy = (LifecycleProxy)(object)sessions;
            var userProxy = (LifecycleProxy)(object)users;
            var clientCreated = false;
            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () => null, () => new HttpClient(new HttpClientHandler(), true), () => DateTime.UtcNow, client => { clientCreated = true; return new M3uEditorClient(client, (host, cancellationToken) => Task.FromResult(new IPAddress[0])); }))
            {
                entryPoint.Run();
                Assert.True(clientCreated);
                Assert.Equal(3, sessionProxy.AddedEvents);
                Assert.Equal(1, userProxy.AddedEvents);
            }
            Assert.Equal(3, sessionProxy.RemovedEvents);
            Assert.Equal(1, userProxy.RemovedEvents);
        }

        [Fact]
        public void ThreeEpisodes_Start_UsesActiveManifestAndPostsUnwatchedPrefetch()
        {
            AssertStartPrefetchTarget(watchSecondEpisode: true, expectedCanonicalId: "episode-3");
        }

        [Fact]
        public void ThreeEpisodes_Start_PrefetchesUnwatchedNextEpisode()
        {
            AssertStartPrefetchTarget(watchSecondEpisode: false, expectedCanonicalId: "episode-2");
        }

        [Theory]
        [InlineData("null")]
        [InlineData("throw")]
        public void WatchedCohort_PreviouslyCompleteThenProfileUnavailable_RecoversAndUndoCancelsRelease(string unavailableMode)
        {
            AssertStartPrefetchTarget(
                watchSecondEpisode: false,
                expectedCanonicalId: "episode-2",
                selectedOtherUserDataMode: unavailableMode,
                startWithCompleteCohort: true);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("throw")]
        public void WatchedCohort_SelectedOtherProfileUnavailable_ProtectsReleaseUntilRecoveryAndUndo(string unavailableMode)
        {
            AssertStartPrefetchTarget(
                watchSecondEpisode: false,
                expectedCanonicalId: "episode-2",
                selectedOtherUserDataMode: unavailableMode);
        }

        [Theory]
        [InlineData("next_n", 3)]
        [InlineData("remaining_season", 1)]
        [InlineData("whole_season", 1)]
        public void PrefetchModes_Start_PostTargetsInNumericPolicyOrderWhenManifestOrderIsLexicographic(string mode, int nextCount)
        {
            var root = Path.Combine(TempDir.Path, "approved", "library", "mapping");
            var mappingUuid = "123e4567-e89b-12d3-a456-426614174004";
            var revision = new string('a', 64);
            var paths = WriteManifest(root, mappingUuid, revision, 9, 10, 100, 11);
            var episodes = paths.Select((path, index) => new Episode { Id = Guid.NewGuid(), Path = path, Name = "Episode " + index }).ToArray();
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php", CapabilityJson);
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_playback");
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_prefetch");
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            ((LifecycleProxy)(object)library).Items = episodes.ToDictionary(item => item.Path, item => (BaseItem)item, StringComparer.Ordinal);
            var config = DefaultConfig();
            config.ManagedPublishingEnabled = true;
            config.ManagedPlaybackCacheEnabled = true;
            config.ManagedPlaybackCacheMode = mode;
            config.ManagedPlaybackCacheNextCount = nextCount;
            config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revision, Success = true } });
            using (var client = new HttpClient(handler))
            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () => config, () => client, () => DateTime.UtcNow, controlledClient => new M3uEditorClient(controlledClient, (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Loopback }))))
            {
                entryPoint.Run();
                ((LifecycleProxy)(object)sessions).Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "numeric-order", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Any(body => body.Contains("action=m3u_editor_cache_prefetch", StringComparison.Ordinal)), TimeSpan.FromSeconds(2)));
                handler.AssertNoUnhandledRequests();
            }

            var prefetch = Assert.Single(handler.ReceivedBodySnapshot().Where(body => body.Contains("action=m3u_editor_cache_prefetch", StringComparison.Ordinal))).ToString();
            var form = ParseForm(prefetch);
            Assert.Equal("episode-10", form["targets[0][canonical_id]"]);
            Assert.Equal("episode-11", form["targets[1][canonical_id]"]);
            Assert.Equal("episode-100", form["targets[2][canonical_id]"]);
        }

        [Fact]
        public void ReconcileDuringIdentityLookup_RejectsOldStartAndAdmitsFreshGeneration()
        {
            var root = Path.Combine(TempDir.Path, "approved", "library", "mapping");
            var mappingUuid = "123e4567-e89b-12d3-a456-426614174001";
            var revisionA = new string('a', 64);
            var revisionB = new string('b', 64);
            var paths = WriteManifest(root, mappingUuid, revisionA);
            var episodes = paths.Select((path, index) => new Episode { Id = Guid.NewGuid(), Path = path, Name = "Episode " + (index + 1) }).ToArray();
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php", CapabilityJson);
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_playback");
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_prefetch");
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_release");
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            var libraryProxy = (LifecycleProxy)(object)library;
            libraryProxy.Items = episodes.ToDictionary(item => item.Path, item => (BaseItem)item, StringComparer.Ordinal);
            libraryProxy.BlockFindByPath = true;
            var config = DefaultConfig();
            config.ManagedPublishingEnabled = true;
            config.ManagedPlaybackCacheEnabled = true;
            config.ManagedPlaybackCacheMode = "next";
            config.ManagedPlaybackCacheUserIds = new[] { "user-a" };
            config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revisionA, Success = true } });
            using (var client = new HttpClient(handler))
            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () => config, () => client, () => DateTime.UtcNow, controlledClient => new M3uEditorClient(controlledClient, (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Loopback }))))
            {
                entryPoint.Run();
                ((LifecycleProxy)(object)sessions).Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "old-session", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Assert.True(libraryProxy.FindByPathEntered.Wait(TimeSpan.FromSeconds(2)));
                WriteManifest(root, mappingUuid, revisionB);
                config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revisionB, Success = true } });
                ManagedPlaybackCacheEntryPoint.NotifyConfigurationChanged(true);
                libraryProxy.ContinueFindByPath.Set();
                Thread.Sleep(150);
                Assert.Empty(handler.ReceivedBodySnapshot());
                ((LifecycleProxy)(object)sessions).Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "fresh-session", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == 3, TimeSpan.FromSeconds(2)));
                config.ManagedPlaybackCacheEnabled = false;
                ManagedPlaybackCacheEntryPoint.NotifyConfigurationChanged(false);
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == 4, TimeSpan.FromSeconds(2)));
                ((LifecycleProxy)(object)sessions).Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "late-session", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                ((LifecycleProxy)(object)sessions).Raise("PlaybackProgress", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "fresh-session", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Thread.Sleep(100);
                Assert.Equal(4, handler.ReceivedBodySnapshot().Count);
                handler.AssertNoUnhandledRequests();
            }
        }

        [Fact]
        public void ConfigurationChangeDuringBlockedProvider_DropsStaleEnabledSnapshot()
        {
            var root = Path.Combine(TempDir.Path, "approved", "library", "mapping");
            var mappingUuid = "123e4567-e89b-12d3-a456-426614174002";
            var revision = new string('a', 64);
            var paths = WriteManifest(root, mappingUuid, revision);
            var episode = new Episode { Id = Guid.NewGuid(), Path = paths[0], Name = "Episode 1" };
            var enabled = DefaultConfig();
            enabled.ManagedPublishingEnabled = true;
            enabled.ManagedPlaybackCacheEnabled = true;
            enabled.ManagedPlaybackCacheMode = "next";
            enabled.ManagedPlaybackCacheUserIds = new[] { "user-a" };
            enabled.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revision, Success = true } });
            var disabled = DefaultConfig();
            disabled.ManagedPublishingEnabled = true;
            disabled.ManagedPlaybackCacheEnabled = false;
            using (var providerEntered = new ManualResetEventSlim(false))
            using (var releaseProvider = new ManualResetEventSlim(false))
            {
                var handler = new FakeHttpHandler();
                handler.RespondWith("player_api.php", CapabilityJson);
                handler.RespondWithRequestIdEcho("action=m3u_editor_cache_playback");
                handler.RespondWithRequestIdEcho("action=m3u_editor_cache_prefetch");
                handler.RespondWithRequestIdEcho("action=m3u_editor_cache_release");
                var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
                var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
                var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
                ((LifecycleProxy)(object)library).Items = new Dictionary<string, BaseItem>(StringComparer.Ordinal) { [paths[0]] = episode };
                var firstRead = true;
                using (var client = new HttpClient(handler))
                using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () =>
                {
                    if (firstRead)
                    {
                        firstRead = false;
                        providerEntered.Set();
                        releaseProvider.Wait(TimeSpan.FromSeconds(2));
                        return enabled;
                    }

                    return disabled;
                }, () => client, () => DateTime.UtcNow, controlledClient => new M3uEditorClient(controlledClient, (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Loopback }))))
                {
                    entryPoint.Run();
                    ((LifecycleProxy)(object)sessions).Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episode, PlaySessionId = "stale-session", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                    Assert.True(providerEntered.Wait(TimeSpan.FromSeconds(2)));
                    ManagedPlaybackCacheEntryPoint.NotifyConfigurationChanged(false);
                    releaseProvider.Set();
                    Thread.Sleep(150);
                    Assert.Empty(handler.ReceivedBodySnapshot());
                    handler.AssertNoUnhandledRequests();
                }
            }
        }

        [Fact]
        public void ReconcileAfterQueuedStart_DropsOldInputAndAdmitsFreshInput()
        {
            var root = Path.Combine(TempDir.Path, "approved", "library", "mapping");
            var mappingUuid = "123e4567-e89b-12d3-a456-426614174003";
            var revisionA = new string('a', 64);
            var revisionB = new string('b', 64);
            var paths = WriteManifest(root, mappingUuid, revisionA);
            var episodes = paths.Select((path, index) => new Episode { Id = Guid.NewGuid(), Path = path, Name = "Episode " + (index + 1) }).ToArray();
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php", CapabilityJson);
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_playback");
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_prefetch");
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_release");
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            var libraryProxy = (LifecycleProxy)(object)library;
            libraryProxy.Items = episodes.ToDictionary(item => item.Path, item => (BaseItem)item, StringComparer.Ordinal);
            libraryProxy.BlockFindByPath = true;
            var config = DefaultConfig();
            config.ManagedPublishingEnabled = true;
            config.ManagedPlaybackCacheEnabled = true;
            config.ManagedPlaybackCacheMode = "next";
            config.ManagedPlaybackCacheUserIds = new[] { "user-a" };
            config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revisionA, Success = true } });
            using (var client = new HttpClient(handler))
            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () => config, () => client, () => DateTime.UtcNow, controlledClient => new M3uEditorClient(controlledClient, (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Loopback }))))
            {
                entryPoint.Run();
                var sessionProxy = (LifecycleProxy)(object)sessions;
                sessionProxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "alpha", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Assert.True(libraryProxy.FindByPathEntered.Wait(TimeSpan.FromSeconds(2)));
                sessionProxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[1], PlaySessionId = "beta", MediaSource = new RuntimeMediaSource { Path = paths[1] } });
                WriteManifest(root, mappingUuid, revisionB);
                config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revisionB, Success = true } });
                ManagedPlaybackCacheEntryPoint.NotifyConfigurationChanged(true);
                libraryProxy.ContinueFindByPath.Set();
                Thread.Sleep(150);
                Assert.Empty(handler.ReceivedBodySnapshot());
                sessionProxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "gamma", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == 3, TimeSpan.FromSeconds(2)));
                Assert.All(handler.ReceivedBodySnapshot().Where(body => !string.IsNullOrEmpty(body)), body => Assert.Contains("revision=" + revisionB, body));
                handler.AssertNoUnhandledRequests();
            }
        }

        private void AssertStartPrefetchTarget(
            bool watchSecondEpisode,
            string expectedCanonicalId,
            string selectedOtherUserDataMode = null,
            bool startWithCompleteCohort = false)
        {
            var root = Path.Combine(TempDir.Path, "approved", "library", "mapping");
            var mappingUuid = "123e4567-e89b-12d3-a456-426614174000";
            var revision = new string('a', 64);
            var paths = WriteManifest(root, mappingUuid, revision);
            var episodes = paths.Select((path, index) => new Episode { Id = Guid.NewGuid(), Path = path, Name = "Episode " + (index + 1) }).ToArray();
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php", CapabilityJson);
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_playback");
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_prefetch");
            handler.RespondWithRequestIdEcho("action=m3u_editor_cache_release");
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            var sessionProxy = (LifecycleProxy)(object)sessions;
            var userProxy = (LifecycleProxy)(object)users;
            var libraryProxy = (LifecycleProxy)(object)library;
            libraryProxy.Items = episodes.ToDictionary(item => item.Path, item => (BaseItem)item, StringComparer.Ordinal);
            if (watchSecondEpisode)
            {
                userProxy.WatchedPaths.Add(paths[1]);
            }
            var config = DefaultConfig();
            config.ManagedPublishingEnabled = true;
            config.ManagedPlaybackCacheEnabled = true;
            config.ManagedPlaybackCacheMode = "next_n";
            config.ManagedPlaybackCacheNextCount = 1;
            var firstUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var secondUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            config.ManagedPlaybackCacheUserIds = new[] { firstUserId.ToString("D"), secondUserId.ToString("D") };
            if (startWithCompleteCohort)
            {
                userProxy.WatchedUserPaths.Add(firstUserId.ToString("D") + "\n" + episodes[0].Path);
                userProxy.WatchedUserPaths.Add(secondUserId.ToString("D") + "\n" + episodes[0].Path);
            }
            config.ManagedPlaybackCacheDeleteAfterWatched = true;
            config.ManagedPlaybackCacheDeleteGraceHours = 37;
            config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revision, Success = true } });
            string[] requests;
            var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            using (var client = new HttpClient(handler))
            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () => config, () => client, () => now, controlledClient => new M3uEditorClient(controlledClient, (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Loopback }))))
            {
                entryPoint.Run();
                sessionProxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "emby-session-1", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == 4, TimeSpan.FromSeconds(2)));
                now = now.AddSeconds(30);
                sessionProxy.Raise("PlaybackProgress", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "emby-session-1", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                sessionProxy.Raise("PlaybackStopped", new PlaybackStopEventArgs { PlaySessionId = "emby-session-1" });
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == 6, TimeSpan.FromSeconds(2)));
                var releaseCount = 6;
                userProxy.FailedUserId = secondUserId.ToString("D");
                userProxy.UserDataFailureMode = selectedOtherUserDataMode;
                userProxy.Raise("UserDataSaved", new UserDataSaveEventArgs { User = new User { Id = firstUserId }, Item = episodes[0], UserData = new UserItemData { Played = true } });
                releaseCount++;
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == releaseCount, TimeSpan.FromSeconds(2)));
                userProxy.WatchedUserPaths.Add(firstUserId.ToString("D") + "\n" + episodes[0].Path);
                userProxy.UserDataFailureMode = null;
                userProxy.Raise("UserDataSaved", new UserDataSaveEventArgs { User = new User { Id = secondUserId }, Item = episodes[0], UserData = new UserItemData { Played = true } });
                releaseCount++;
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == releaseCount, TimeSpan.FromSeconds(2)));
                userProxy.Raise("UserDataSaved", new UserDataSaveEventArgs { User = new User { Id = secondUserId }, Item = episodes[0], UserData = new UserItemData { Played = false } });
                releaseCount++;
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodySnapshot().Count == releaseCount, TimeSpan.FromSeconds(2)));
                sessionProxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "emby-session-1", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                sessionProxy.Raise("PlaybackProgress", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "emby-session-1", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Thread.Sleep(100);
                requests = handler.ReceivedBodySnapshot().ToArray();
                handler.AssertNoUnhandledRequests();
            }
            sessionProxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "disposed-session", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
            userProxy.Raise("UserDataSaved", new UserDataSaveEventArgs { User = new User { Id = firstUserId }, Item = episodes[0], UserData = new UserItemData { Played = true } });
            Thread.Sleep(100);
            var expectedRequestCount = 9;
            Assert.Equal(expectedRequestCount, handler.ReceivedBodySnapshot().Count);
            var playback = requests.Where(body => body.Contains("action=m3u_editor_cache_playback", StringComparison.Ordinal)).ToArray();
            var prefetch = Assert.Single(requests.Where(body => body.Contains("action=m3u_editor_cache_prefetch", StringComparison.Ordinal)));
            Assert.Contains(playback, body => body.Contains("mapping_uuid=" + mappingUuid, StringComparison.Ordinal));
            Assert.Contains(playback, body => body.Contains("revision=" + revision, StringComparison.Ordinal));
            Assert.Contains(playback, body => body.Contains("targets%5B0%5D%5Bcanonical_id%5D=episode-1", StringComparison.Ordinal));
            Assert.Contains("targets%5B0%5D%5Bcanonical_id%5D=" + expectedCanonicalId, prefetch);
            Assert.DoesNotContain("targets%5B0%5D%5Bcanonical_id%5D=" + (watchSecondEpisode ? "episode-2" : "episode-3"), prefetch);
            var playbackForms = playback.Select(ParseForm).ToArray();
            Assert.Equal(new[] { "start", "heartbeat", "stop" }, playbackForms.Select(form => form["state"]).ToArray());
            var protocolSession = playbackForms[0]["session_id"];
            Assert.False(string.IsNullOrWhiteSpace(protocolSession));
            Assert.All(playbackForms, form => Assert.Equal(protocolSession, form["session_id"]));
            Assert.Equal(new[] { "1", "2", "3" }, playbackForms.Select(form => form["sequence"]).ToArray());
            Assert.Equal(expectedRequestCount, requests.Length);
            var releases = requests.Where(body => body.Contains("action=m3u_editor_cache_release", StringComparison.Ordinal)).Select(ParseForm).ToArray();
            var expectedReleaseState = startWithCompleteCohort
                ? new[] { "1", "0", "1", "0" }
                : new[] { "0", "0", "1", "0" };
            Assert.Equal(expectedReleaseState, releases.Select(form => form["watched"]).ToArray());
            Assert.Equal(expectedReleaseState, releases.Select(form => form["cohort_complete"]).ToArray());
            var expectedConsumerId = Hash("7\n" + firstUserId.ToString("D") + "\n" + secondUserId.ToString("D"));
            Assert.All(releases, form =>
            {
                Assert.Equal("7", form["integration_id"]);
                Assert.Equal(mappingUuid, form["mapping_uuid"]);
                Assert.Equal(revision, form["revision"]);
                Assert.Equal(expectedConsumerId, form["consumer_id"]);
                Assert.Equal("episode-1", form["targets[0][canonical_id]"]);
                Assert.Equal("variant-A", form["targets[0][variant_key]"]);
                Assert.Equal("37", form["delete_after_hours"]);
                Assert.True(Guid.TryParse(form["request_id"], out _));
            });
            Assert.Equal(6, libraryProxy.FindByPathCalls);
            Assert.Equal(17, userProxy.UserDataReads);
        }

        private static IDictionary<string, string> ParseForm(string body)
        {
            return body.Split('&')
                .Select(part => part.Split(new[] { '=' }, 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(
                    pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                    pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                    StringComparer.Ordinal);
        }

        private static string[] WriteManifest(string root, string mappingUuid, string revision, params int[] episodeNumbers)
        {
            Directory.CreateDirectory(Path.Combine(root, ".m3u-editor-for-emby"));
            var files = (episodeNumbers == null || episodeNumbers.Length == 0 ? Enumerable.Range(1, 3) : episodeNumbers).Select(number =>
            {
                var relative = "Season 1/Episode " + number.ToString("D2") + ".strm";
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var text = "synthetic fixture " + number + "\n";
                File.WriteAllText(path, text);
                return new { RelativePath = relative, Sha256 = Hash(text), Length = (long)Encoding.UTF8.GetByteCount(text), CanonicalId = "episode-" + number, VariantKey = "variant-A", SourceId = number, PlaylistId = 1, SeriesCanonicalId = "series-1", SeasonNumber = 1, EpisodeNumber = number };
            }).ToArray();
            File.WriteAllText(Path.Combine(root, ".m3u-editor-for-emby", "active.json"), JsonSerializer.Serialize(new { FormatVersion = 1, MappingUuid = mappingUuid, Revision = revision, Files = files }));
            return files.Select(file => Path.Combine(root, file.RelativePath)).ToArray();
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(valueByte => valueByte.ToString("x2")));
        }

        private const string CapabilityJson = "{\"m3u_editor\":{\"library_publishing\":{\"cache_control\":{\"api_version\":1,\"actions\":{\"prefetch\":\"m3u_editor_cache_prefetch\",\"playback\":\"m3u_editor_cache_playback\",\"release\":\"m3u_editor_cache_release\"},\"max_targets\":100,\"heartbeat_seconds\":30,\"lease_seconds\":120}}}}";
        private const string ResponseJson = "{\"api_version\":1,\"data\":{\"request_id\":\"00000000-0000-0000-0000-000000000000\",\"duplicate\":false,\"results\":[]}}";

        private sealed class RuntimePlaybackProgressEventArgs : PlaybackProgressEventArgs { public RuntimeMediaSource MediaSource { get; set; } }
        private sealed class RuntimeMediaSource { public string Path { get; set; } }

        private class LifecycleProxy : DispatchProxy
        {
            private readonly Dictionary<string, Delegate> _handlers = new Dictionary<string, Delegate>(StringComparer.Ordinal);
            public int AddedEvents { get; private set; }
            public int RemovedEvents { get; private set; }
            public int FindByPathCalls { get; private set; }
            public int UserDataReads { get; private set; }
            public string FailedUserId { get; set; }
            public string UserDataFailureMode { get; set; }
            public bool BlockFindByPath { get; set; }
            public ManualResetEventSlim FindByPathEntered { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim ContinueFindByPath { get; } = new ManualResetEventSlim(false);
            public IDictionary<string, BaseItem> Items { get; set; } = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            public ISet<string> WatchedPaths { get; } = new HashSet<string>(StringComparer.Ordinal);
            public ISet<string> WatchedUserPaths { get; } = new HashSet<string>(StringComparer.Ordinal);
            public void Raise(string eventName, object args)
            {
                if (_handlers.TryGetValue(eventName, out var handler) && handler != null)
                {
                    handler.DynamicInvoke(this, args);
                }
            }
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal)) { AddedEvents++; var name = targetMethod.Name.Substring(4); _handlers[name] = Delegate.Combine(_handlers.TryGetValue(name, out var existing) ? existing : null, (Delegate)args[0]); return null; }
                if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal)) { RemovedEvents++; var name = targetMethod.Name.Substring(7); if (_handlers.TryGetValue(name, out var existing)) _handlers[name] = Delegate.Remove(existing, (Delegate)args[0]); return null; }
                if (targetMethod.Name == "FindByPath")
                {
                    FindByPathCalls++;
                    if (BlockFindByPath)
                    {
                        FindByPathEntered.Set();
                        ContinueFindByPath.Wait(TimeSpan.FromSeconds(2));
                    }
                    return args != null && args.Length > 0 && args[0] is string path && Items.TryGetValue(path, out var item) ? item : null;
                }
                if (targetMethod.Name == "GetUserData")
                {
                    UserDataReads++;
                    var userId = args != null && args.Length > 0 ? args[0] as string : null;
                    var item = args != null && args.Length > 1 ? args[1] as BaseItem : null;
                    if (string.Equals(userId, FailedUserId, StringComparison.Ordinal))
                    {
                        if (string.Equals(UserDataFailureMode, "null", StringComparison.Ordinal)) return null;
                        if (string.Equals(UserDataFailureMode, "throw", StringComparison.Ordinal)) throw new InvalidOperationException("synthetic user data failure");
                    }
                    return new UserItemData { Played = item != null && (WatchedPaths.Contains(item.Path) || WatchedUserPaths.Contains((userId ?? string.Empty) + "\n" + item.Path)) };
                }
                return targetMethod.ReturnType == typeof(void) ? null : targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }
    }
}
