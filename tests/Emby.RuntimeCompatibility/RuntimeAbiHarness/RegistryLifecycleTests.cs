using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.RuntimeCompatibility
{
    // Offline regression: real plugin factory/open IDs and the server's registry/close methods.
    // No server startup, provider connection, playback or transport emulation.
    internal sealed partial class RegistryLifecycleTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Type _liveType;
        private readonly Type _managerType;
        private readonly object _manager;
        private readonly object _host;
        private readonly MethodInfo _factory;
        private readonly List<OpenedStream> _opened = new List<OpenedStream>();

        private RegistryLifecycleTests(ServerAssemblyLoadContext context, Assembly plugin)
        {
            _liveType = plugin.GetType("Emby.M3uEditor.Plugin.Service.M3uEditorLiveStream", true);
            var server = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "Emby.Server.Implementations.dll"));
            _managerType = server.GetType("Emby.Server.Implementations.Library.MediaSourceManager", true);
            _manager = RuntimeHelpers.GetUninitializedObject(_managerType);
            var map = _managerType.GetField("_openStreams", Instance);
            map.SetValue(_manager, Activator.CreateInstance(map.FieldType));
            _managerType.GetField("_liveStreamSemaphore", Instance).SetValue(_manager, new SemaphoreSlim(1, 1));
            var loggerField = _managerType.GetField("_logger", Instance);
            loggerField.SetValue(_manager, DispatchProxy.Create(loggerField.FieldType, typeof(OfflineLogger)));
            var hostType = plugin.GetType("Emby.M3uEditor.Plugin.Service.M3uEditorTunerHost", true);
            _host = RuntimeHelpers.GetUninitializedObject(hostType);
            _factory = hostType.GetMethod("CreateMediaSourceInfo", Instance);
            var pluginType = plugin.GetType("Emby.M3uEditor.Plugin.Plugin", true);
            var configurationType = plugin.GetType("Emby.M3uEditor.Plugin.PluginConfiguration", true);
            var pluginInstance = RuntimeHelpers.GetUninitializedObject(pluginType);
            for (var type = pluginType; type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(object) && field.GetValue(pluginInstance) == null)
                        field.SetValue(pluginInstance, new object());
                    if (field.FieldType == configurationType && field.GetValue(pluginInstance) == null)
                        field.SetValue(pluginInstance, Activator.CreateInstance(configurationType));
                }
            }
            pluginType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, pluginInstance);
        }

        public static void Run(ServerAssemblyLoadContext context, Assembly plugin)
        {
            var tests = new RegistryLifecycleTests(context, plugin);
            try
            {
                tests.StopOrder(false);
                tests.StopOrder(true);
                tests.RestartAndLateStop();
                tests.ProbeAndPlayback();
                tests.DifferentChannels();
                // Shared server contracts, not app/version-specific workarounds or physical device tests.
                tests.Renegotiation(context, plugin, true, 0);
                tests.Renegotiation(context, plugin, true, 3);
                tests.Renegotiation(context, plugin, true, 1);
                tests.Renegotiation(context, plugin, false, 3);
                tests.Renegotiation(context, plugin, false, 3, false);
                tests.Renegotiation(context, plugin, true, 1, true, false);
                tests.MixedRenegotiation(context, plugin);
                Console.WriteLine("Emby Server 4.10.2.0: real registry lifecycle passed (both stop orders, restart/late stop, probe/playback, cleanup/reopen, different channels; zero HTTP requests).");
            }
            finally
            {
                foreach (var opened in tests._opened)
                    ((IDisposable)opened.Stream).Dispose();
            }
        }

        private OpenedStream Open(int channel, string consumer)
        {
            var source = _factory.Invoke(_host, new object[] { channel, "https://synthetic.example.test/live/" + channel + ".ts", null, "SyntheticPlayer/1.0" });
            var handler = new OfflineHttpHandler();
            var stream = Activator.CreateInstance(_liveType, Instance, null,
                new object[] { source, "synthetic-tuner", new HttpClient(handler), null }, null);
            var opened = new OpenedStream { Stream = stream, Handler = handler };
            _opened.Add(opened);
            ((Task)_liveType.GetMethod("Open").Invoke(stream, new object[] { CancellationToken.None })).GetAwaiter().GetResult();
            var id = (string)source.GetType().GetProperty("Id").GetValue(source);
            Require((string)_liveType.GetProperty("OriginalStreamId").GetValue(stream) == "xtream_live_" + channel, "Original stream identity changed");
            Require(!(bool)_liveType.GetProperty("EnableStreamSharing").GetValue(stream), "Sharing must remain disabled");
            // These are the provider prefixes added by LiveTvManager and MediaSourceManager.
            opened.Key = Prefix("Emby.LiveTV.LiveTvMediaSourceProvider") + Prefix("Emby.LiveTV.EmbyTV") + id;
            source.GetType().GetProperty("LiveStreamId").SetValue(source, opened.Key);
            Add(opened, consumer);
            _managerType.GetMethod("AddOrUpdateStream", Instance).Invoke(_manager, new object[] { opened.Key, stream });
            return opened;
        }

        private static string Prefix(string name)
        {
            return new Guid(MD5.HashData(Encoding.Unicode.GetBytes(name))).ToString("N") + "_";
        }

        private void Add(OpenedStream stream, string consumer)
        {
            _liveType.GetMethod("AddConsumer").Invoke(stream.Stream, new object[] { consumer });
        }

        private void Stop(OpenedStream stream, string consumer)
        {
            ((Task)_managerType.GetMethod("CloseLiveStream", Instance).Invoke(_manager, new object[] { stream.Key, consumer })).GetAwaiter().GetResult();
        }

        private object Lookup(OpenedStream stream)
        {
            return _managerType.GetMethod("GetLiveStreamInfo", Instance).Invoke(_manager, new object[] { stream.Key });
        }

        private void Alive(OpenedStream stream, int count)
        {
            Require(ReferenceEquals(stream.Stream, Lookup(stream)), "A valid opened stream was replaced or removed");
            Require((int)_liveType.GetProperty("ConsumerCount").GetValue(stream.Stream) == count, "Consumer membership changed unexpectedly");
            Require(stream.Handler.DisposeCount == 0 && stream.Handler.RequestCount == 0, "Transport was opened or disposed for a surviving stream");
        }

        private bool WaitForClose(OpenedStream stream)
        {
            // Native close removes the registry entry before transport disposal finishes.
            return SpinWait.SpinUntil(() => Lookup(stream) == null &&
                Volatile.Read(ref stream.Handler.DisposeCount) > 0, TimeSpan.FromSeconds(3));
        }

        private void Closed(OpenedStream stream)
        {
            Require(Lookup(stream) == null, "Final stop did not clear registry");
            Require(stream.Handler.DisposeCount == 1 && stream.Handler.RequestCount == 0, "Final stop did not dispose exactly once without HTTP");
        }

        private void StopOrder(bool reverse)
        {
            var a = Open(42, "viewer-a");
            var b = Open(42, "viewer-b");
            // Assert via the registry, not just different GUIDs on otherwise colliding objects.
            Alive(a, 1);
            Alive(b, 1);
            var first = reverse ? b : a;
            var last = reverse ? a : b;
            Stop(first, reverse ? "viewer-b" : "viewer-a");
            Closed(first);
            Alive(last, 1);
            Stop(first, "late-stop");
            Alive(last, 1);
            Stop(last, reverse ? "viewer-a" : "viewer-b");
            Closed(last);
        }

        private void RestartAndLateStop()
        {
            var old = Open(42, "same-session");
            var next = Open(42, "same-session");
            Stop(old, "same-session");
            Closed(old);
            Alive(next, 1);
            Stop(old, "same-session");
            Alive(next, 1);
            Stop(next, "same-session");
            Closed(next);
            var reopened = Open(42, "same-session");
            Stop(next, "same-session");
            Alive(reopened, 1);
            Stop(reopened, "same-session");
            Closed(reopened);
        }

        private void ProbeAndPlayback()
        {
            foreach (var probeId in new[] { "probe", null, "" })
            {
                var stream = Open(42, probeId);
                Add(stream, "playback");
                Stop(stream, "foreign");
                Alive(stream, 2);
                Stop(stream, probeId);
                Stop(stream, probeId);
                Alive(stream, 1);
                Stop(stream, "playback");
                Closed(stream);
            }
            var repeated = Open(42, "same-session");
            Add(repeated, "same-session");
            Add(repeated, "other-session");
            Stop(repeated, "same-session");
            Alive(repeated, 2);
            Stop(repeated, "same-session");
            Stop(repeated, "same-session");
            Alive(repeated, 1);
            Stop(repeated, "other-session");
            Closed(repeated);
        }

        private void DifferentChannels()
        {
            var a = Open(42, "viewer");
            var b = Open(43, "viewer");
            Stop(a, "viewer");
            Closed(a);
            Alive(b, 1);
            Stop(b, "viewer");
            Closed(b);
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private sealed class OpenedStream
        {
            public object Stream;
            public string Key;
            public OfflineHttpHandler Handler;
        }

        private sealed class OfflineHttpHandler : HttpMessageHandler
        {
            public int RequestCount;
            public int DisposeCount;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                throw new InvalidOperationException("Offline registry tests must not make HTTP requests");
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) DisposeCount++;
                base.Dispose(disposing);
            }
        }
    }

    public class OfflineLogger : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            return method.ReturnType == typeof(void) ? null :
                (method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
        }
    }
}
