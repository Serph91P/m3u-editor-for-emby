using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackCacheServiceTests
    {
        [Fact]
        public void StartThenDuplicate_StartsPlaybackBeforePrefetchExactlyOnce_AndDisposeStopsOnce()
        {
            var calls = new List<string>();
            var serviceType = typeof(Plugin).Assembly.GetType(
                "Emby.M3uEditor.Plugin.Service.ManagedPlaybackCacheService");
            Assert.NotNull(serviceType);
            var service = Activator.CreateInstance(serviceType, new Action<string, long>((action, sequence) =>
                calls.Add(action + ":" + sequence)), TimeSpan.FromMilliseconds(20));
            var start = serviceType.GetMethod("PlaybackStarted", BindingFlags.Public | BindingFlags.Instance);
            var stop = serviceType.GetMethod("PlaybackStopped", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(start);
            Assert.NotNull(stop);

            start.Invoke(service, new object[] { "session-1" });
            start.Invoke(service, new object[] { "session-1" });
            Assert.True(SpinWait.SpinUntil(() => calls.Count >= 2, TimeSpan.FromSeconds(2)));

            stop.Invoke(service, new object[] { "session-1" });
            ((IDisposable)service).Dispose();
            ((IDisposable)service).Dispose();

            Assert.Equal(new[] { "playback:1", "prefetch:2", "release:3" }, calls);
        }

        [Fact]
        public void DisabledService_DoesNotEnqueueNewPlaybackOrPrefetch()
        {
            var calls = new List<string>();
            var serviceType = typeof(Plugin).Assembly.GetType(
                "Emby.M3uEditor.Plugin.Service.ManagedPlaybackCacheService");
            Assert.NotNull(serviceType);
            var service = Activator.CreateInstance(serviceType, new Action<string, long>((action, sequence) =>
                calls.Add(action + ":" + sequence)), TimeSpan.FromMilliseconds(20));
            var setEnabled = serviceType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Instance);
            var start = serviceType.GetMethod("PlaybackStarted", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(setEnabled);
            Assert.NotNull(start);

            setEnabled.Invoke(service, new object[] { false });
            start.Invoke(service, new object[] { "session-1" });
            Thread.Sleep(80);
            ((IDisposable)service).Dispose();

            Assert.Empty(calls);
        }
    }
}
