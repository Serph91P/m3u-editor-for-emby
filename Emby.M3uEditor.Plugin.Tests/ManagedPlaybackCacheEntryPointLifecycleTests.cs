using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
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

        [Fact]
        public void LifecycleFixture_EntryPointCanCreateTheRealClientWithControlledDns()
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
                    typeof(Func<DateTime>),
                    typeof(Func<HttpClient, M3uEditorClient>)
                },
                null);

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

            using (var entryPoint = new ManagedPlaybackCacheEntryPoint(
                sessions,
                users,
                library,
                () => null,
                () => new HttpClient(new HttpClientHandler(), true),
                () => DateTime.UtcNow,
                client =>
                {
                    clientCreated = true;
                    return new M3uEditorClient(client, (host, cancellationToken) =>
                        Task.FromResult(new System.Net.IPAddress[0]));
                }))
            {
                entryPoint.Run();

                Assert.True(clientCreated);
                Assert.Equal(3, sessionProxy.AddedEvents);
                Assert.Equal(1, userProxy.AddedEvents);
            }

            Assert.Equal(3, sessionProxy.RemovedEvents);
            Assert.Equal(1, userProxy.RemovedEvents);
        }

        private class LifecycleProxy : DispatchProxy
        {
            public int AddedEvents { get; private set; }
            public int RemovedEvents { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal))
                {
                    AddedEvents++;
                }
                else if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal))
                {
                    RemovedEvents++;
                }

                return targetMethod.ReturnType == typeof(void)
                    ? null
                    : targetMethod.ReturnType.IsValueType
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null;
            }
        }
    }
}
