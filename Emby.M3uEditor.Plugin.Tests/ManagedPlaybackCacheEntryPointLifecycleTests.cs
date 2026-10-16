using System;
using System.Net.Http;
using System.Reflection;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    /// <summary>
    /// Lifecycle fixtures use the real entry point with only its runtime-owned
    /// configuration, transport and clock dependencies controlled.
    /// </summary>
    public class ManagedPlaybackCacheEntryPointLifecycleTests
    {
        [Fact]
        public void LifecycleFixture_EntryPointExposesInternalRuntimeSeam()
        {
            var constructor = typeof(ManagedPlaybackCacheEntryPoint).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(ISessionManager),
                    typeof(IUserDataManager),
                    typeof(ILibraryManager),
                    typeof(Func<PluginConfiguration>),
                    typeof(Func<HttpClient>),
                    typeof(Func<DateTime>)
                },
                null);

            Assert.NotNull(constructor);
        }
    }
}
