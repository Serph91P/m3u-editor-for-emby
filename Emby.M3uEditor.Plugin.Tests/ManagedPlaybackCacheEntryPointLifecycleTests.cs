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
            var root = Path.Combine(TempDir.Path, "approved", "library", "mapping");
            var mappingUuid = "123e4567-e89b-12d3-a456-426614174000";
            var revision = new string('a', 64);
            var paths = WriteManifest(root, mappingUuid, revision);
            var episodes = paths.Select((path, index) => new Episode { Id = Guid.NewGuid(), Path = path, Name = "Episode " + (index + 1) }).ToArray();
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php", CapabilityJson);
            handler.RespondWith("action=m3u_editor_cache_playback", ResponseJson);
            handler.RespondWith("action=m3u_editor_cache_prefetch", ResponseJson);
            var sessions = DispatchProxy.Create<ISessionManager, LifecycleProxy>();
            var users = DispatchProxy.Create<IUserDataManager, LifecycleProxy>();
            var library = DispatchProxy.Create<ILibraryManager, LifecycleProxy>();
            var sessionProxy = (LifecycleProxy)(object)sessions;
            var userProxy = (LifecycleProxy)(object)users;
            var libraryProxy = (LifecycleProxy)(object)library;
            libraryProxy.Items = episodes.ToDictionary(item => item.Path, item => (BaseItem)item, StringComparer.Ordinal);
            userProxy.WatchedPaths.Add(paths[1]);
            var config = DefaultConfig();
            config.ManagedPublishingEnabled = true;
            config.ManagedPlaybackCacheEnabled = true;
            config.ManagedPlaybackCacheMode = "next_n";
            config.ManagedPlaybackCacheNextCount = 1;
            config.ManagedPlaybackCacheUserIds = new[] { "user-a" };
            config.ManagedMappingsJson = JsonSerializer.Serialize(new[] { new ManagedMappingState { MappingUuid = mappingUuid, IntegrationId = 7, OutputPath = root, ActiveRevision = revision, Success = true } });
            string[] requests;
            using (var client = new HttpClient(handler))
            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(sessions, users, library, () => config, () => client, () => DateTime.UtcNow, controlledClient => new M3uEditorClient(controlledClient, (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Loopback }))))
            {
                entryPoint.Run();
                sessionProxy.Raise("PlaybackStart", new RuntimePlaybackProgressEventArgs { Item = episodes[0], PlaySessionId = "emby-session-1", MediaSource = new RuntimeMediaSource { Path = paths[0] } });
                Assert.True(SpinWait.SpinUntil(() => handler.ReceivedBodies.Count == 3, TimeSpan.FromSeconds(2)));
                requests = handler.ReceivedBodies.ToArray();
            }
            var playback = Assert.Single(requests.Where(body => body.Contains("action=m3u_editor_cache_playback", StringComparison.Ordinal)));
            var prefetch = Assert.Single(requests.Where(body => body.Contains("action=m3u_editor_cache_prefetch", StringComparison.Ordinal)));
            Assert.Contains("mapping_uuid=" + mappingUuid, playback);
            Assert.Contains("revision=" + revision, playback);
            Assert.Contains("targets%5B0%5D%5Bcanonical_id%5D=episode-1", playback);
            Assert.Contains("targets%5B0%5D%5Bcanonical_id%5D=episode-3", prefetch);
            Assert.DoesNotContain("episode-2", prefetch);
            Assert.Equal(3, libraryProxy.FindByPathCalls);
            Assert.Equal(3, userProxy.UserDataReads);
        }

        private static string[] WriteManifest(string root, string mappingUuid, string revision)
        {
            Directory.CreateDirectory(Path.Combine(root, ".m3u-editor-for-emby"));
            var files = Enumerable.Range(1, 3).Select(number =>
            {
                var relative = "Season 1/Episode " + number + ".strm";
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
            public IDictionary<string, BaseItem> Items { get; set; } = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            public ISet<string> WatchedPaths { get; } = new HashSet<string>(StringComparer.Ordinal);
            public void Raise(string eventName, object args) { if (_handlers.TryGetValue(eventName, out var handler)) handler.DynamicInvoke(this, args); }
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal)) { AddedEvents++; var name = targetMethod.Name.Substring(4); _handlers[name] = Delegate.Combine(_handlers.TryGetValue(name, out var existing) ? existing : null, (Delegate)args[0]); return null; }
                if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal)) { RemovedEvents++; var name = targetMethod.Name.Substring(7); if (_handlers.TryGetValue(name, out var existing)) _handlers[name] = Delegate.Remove(existing, (Delegate)args[0]); return null; }
                if (targetMethod.Name == "FindByPath") { FindByPathCalls++; return args != null && args.Length > 0 && args[0] is string path && Items.TryGetValue(path, out var item) ? item : null; }
                if (targetMethod.Name == "GetUserData") { UserDataReads++; var item = args != null && args.Length > 1 ? args[1] as BaseItem : null; return new UserItemData { Played = item != null && WatchedPaths.Contains(item.Path) }; }
                return targetMethod.ReturnType == typeof(void) ? null : targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }
    }
}
