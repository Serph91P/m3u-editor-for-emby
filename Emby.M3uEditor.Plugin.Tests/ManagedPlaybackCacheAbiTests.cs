using System;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackCacheAbiTests
    {
        [Fact]
        public void PlaybackCacheEntryPoint_ImplementsServerLifecycleWithoutConstructorConfigurationAccess()
        {
            var entryPoint = typeof(Plugin).Assembly.GetType(
                "Emby.M3uEditor.Plugin.Service.ManagedPlaybackCacheEntryPoint");
            Assert.NotNull(entryPoint);
            Assert.Contains(typeof(IServerEntryPoint), entryPoint.GetInterfaces());
            Assert.Contains(typeof(IDisposable), entryPoint.GetInterfaces());
            Assert.NotNull(entryPoint.GetMethod("Run", Type.EmptyTypes));
        }

        [Fact]
        public void SupportedSdk_ExposesPlaybackAndWatchedLifecycleContract()
        {
            AssertEvent(typeof(ISessionManager), "PlaybackStart", "MediaBrowser.Controller.Library.PlaybackProgressEventArgs");
            AssertEvent(typeof(ISessionManager), "PlaybackProgress", "MediaBrowser.Controller.Library.PlaybackProgressEventArgs");
            AssertEvent(typeof(ISessionManager), "PlaybackStopped", "MediaBrowser.Controller.Library.PlaybackStopEventArgs");
            AssertEvent(typeof(IUserDataManager), "UserDataSaved", "MediaBrowser.Controller.Library.UserDataSaveEventArgs");

            var run = typeof(IServerEntryPoint).GetMethod("Run", Type.EmptyTypes);
            Assert.NotNull(run);
            Assert.Equal(typeof(void), run.ReturnType);
        }

        private static void AssertEvent(Type owner, string name, string argumentTypeName)
        {
            var eventInfo = owner.GetEvent(name);
            Assert.NotNull(eventInfo);
            Assert.Equal(typeof(EventHandler<>), eventInfo.EventHandlerType.GetGenericTypeDefinition());
            Assert.Equal(argumentTypeName, eventInfo.EventHandlerType.GetGenericArguments().Single().FullName);
        }
    }
}
