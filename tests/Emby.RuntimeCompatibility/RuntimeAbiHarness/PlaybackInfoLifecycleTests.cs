using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.RuntimeCompatibility
{
    internal sealed partial class RegistryLifecycleTests
    {
        private void MixedRenegotiation(ServerAssemblyLoadContext context, Assembly plugin)
        {
            var fixture = new NativePlaybackFixture(context, _manager);
            var stream = Open(42, "synthetic-original");
            var sameChannelPeer = Open(42, "synthetic-peer");
            var otherChannelPeer = Open(43, "synthetic-other");
            var source = _liveType.GetProperty("MediaSource").GetValue(stream.Stream);
            var sourceId = (string)NativePlaybackFixture.Get(source, "Id");
            fixture.Register("synthetic-original", stream.Key, sourceId);
            var adapterType = plugin.GetType("Emby.M3uEditor.Plugin.Service.LiveTvPlaybackEntryPoint", true);
            var adapter = Activator.CreateInstance(adapterType, fixture.SessionManager, _manager, null);
            adapterType.GetMethod("Run").Invoke(adapter, null);
            try
            {
                fixture.RaiseStart(sourceId, "synthetic-original");
                var replacement = (string)NativePlaybackFixture.Get(fixture.PlaybackInfo("synthetic-original", stream.Key, sourceId), "PlaySessionId");
                fixture.StartAndWait(replacement, stream.Key, sourceId);
                var additional = (string)NativePlaybackFixture.Get(fixture.PlaybackInfo(null, stream.Key, sourceId), "PlaySessionId");
                fixture.StopAndWait(additional, stream.Key, sourceId);
                var surviving = fixture.Session.GetType().GetMethod("GetPlaySessionInfo").Invoke(fixture.Session, new object[] { replacement });
                Require(surviving != null && (string)NativePlaybackFixture.Get(NativePlaybackFixture.Get(surviving, "PlayState"), "MediaSourceId") == sourceId,
                    "Native started replacement must survive the additional stop");
                Require(string.IsNullOrEmpty((string)NativePlaybackFixture.Get(NativePlaybackFixture.Get(fixture.Session, "PlayState"), "MediaSourceId")),
                    "Mixed regression must exercise the empty last-session projection");
                Alive(stream, 1);
                Alive(sameChannelPeer, 1);
                Alive(otherChannelPeer, 1);
                AssertNativeCapacity(context, plugin, true);
                fixture.StopAndWait(replacement, stream.Key, sourceId);
                Require(SpinWait.SpinUntil(() => Lookup(stream) == null, TimeSpan.FromSeconds(3)), "Final started replacement stop did not release the original consumer");
                Closed(stream);
                Alive(sameChannelPeer, 1);
                Alive(otherChannelPeer, 1);
                AssertNativeCapacity(context, plugin, false);
                fixture.StopAndWait(replacement, stream.Key, sourceId);
                Closed(stream);
                Console.WriteLine("Native mixed negotiation passed: additional stop preserves started replacement; its final stop releases registry/tuner capacity and retains peers.");
            }
            finally
            {
                (adapter as IDisposable)?.Dispose();
                Stop(sameChannelPeer, "synthetic-peer");
                Stop(otherChannelPeer, "synthetic-other");
            }
        }

        private void Renegotiation(ServerAssemblyLoadContext context, Assembly plugin, bool startBefore, int replacements, bool notifyStart = true, bool replaceCurrent = true)
        {
            var fixture = new NativePlaybackFixture(context, _manager);
            var stream = Open(42, "synthetic-original");
            var sameChannelPeer = Open(42, "synthetic-peer");
            var otherChannelPeer = Open(43, "synthetic-other");
            var source = _liveType.GetProperty("MediaSource").GetValue(stream.Stream);
            var sourceId = (string)NativePlaybackFixture.Get(source, "Id");
            fixture.Register("synthetic-original", stream.Key, sourceId);

            // The baseline has no lifecycle adapter. Run the identical native flow either way.
            var adapterType = plugin.GetType("Emby.M3uEditor.Plugin.Service.LiveTvPlaybackEntryPoint");
            object adapter = null;
            if (adapterType != null)
            {
                adapter = Activator.CreateInstance(adapterType, fixture.SessionManager, _manager, null);
                adapterType.GetMethod("Run").Invoke(adapter, null);
                if (startBefore && notifyStart) fixture.RaiseStart(sourceId, "synthetic-original");
            }
            try
            {
                var current = "synthetic-original";
                for (var i = 0; i < replacements; i++)
                {
                    var response = fixture.PlaybackInfo(replaceCurrent ? current : null, stream.Key, sourceId);
                    var next = (string)NativePlaybackFixture.Get(response, "PlaySessionId");
                    Require(next != current, "PlaybackInfo did not replace PlaySessionId");
                    var sources = (Array)NativePlaybackFixture.Get(response, "MediaSources");
                    Require((string)NativePlaybackFixture.Get(sources.GetValue(0), "LiveStreamId") == stream.Key, "PlaybackInfo changed LiveStreamId");
                    var previous = fixture.Session.GetType().GetMethod("GetPlaySessionInfo", new[] { typeof(string) }).Invoke(fixture.Session, new object[] { current });
                    Require(replaceCurrent ? previous == null : previous != null, "Unexpected native prior-session ownership");
                    Alive(stream, 1); // Native renegotiation performs NO AddConsumer.
                    Console.WriteLine("Native PlaybackInfo replacement " + (i + 1) + ": same registry object, new play session, ConsumerCount=1.");
                    current = next;
                }
                if (!startBefore && notifyStart) fixture.RaiseStart(sourceId, current);
                if (replacements > 0 && replaceCurrent)
                {
                    fixture.Stop("synthetic-original", stream.Key, sourceId);
                    fixture.Stop("synthetic-unknown", stream.Key, sourceId);
                    Alive(stream, 1);
                }
                AssertNativeCapacity(context, plugin, true);
                fixture.Stop(current, stream.Key, sourceId);
                if (!replaceCurrent)
                {
                    Alive(stream, 1);
                    fixture.Stop("synthetic-original", stream.Key, sourceId);
                }
                Require(SpinWait.SpinUntil(() => Lookup(stream) == null, TimeSpan.FromSeconds(3)), "Renegotiated final stop left original consumer registered");
                Closed(stream);
                Alive(sameChannelPeer, 1);
                Alive(otherChannelPeer, 1);
                AssertNativeCapacity(context, plugin, false);
                fixture.Stop(current, stream.Key, sourceId);
                Closed(stream);
                Console.WriteLine("Native PlaybackInfo/final-stop registry cleanup passed; independent same/other-channel peers retained.");
            }
            finally
            {
                (adapter as IDisposable)?.Dispose();
                Stop(sameChannelPeer, "synthetic-peer");
                Stop(otherChannelPeer, "synthetic-other");
            }
        }
    }

    // Only peripheral services are stubs. PlaybackInfo, session replacement, Stop and registry are official server methods.
    internal sealed class NativePlaybackFixture
    {
        private readonly Assembly _controller;
        private readonly Assembly _model;
        private readonly Assembly _encoding;
        private readonly object _service;
        private readonly object _logger;
        public object Session { get; }
        public object SessionManager { get; }

        public NativePlaybackFixture(ServerAssemblyLoadContext context, object mediaSourceManager)
        {
            _controller = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Controller.dll"));
            _model = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Model.dll"));
            _encoding = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "Emby.Server.MediaEncoding.dll"));
            var server = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "Emby.Server.Implementations.dll"));

            _logger = Proxy(_model.GetType("MediaBrowser.Model.Logging.ILogger", true));
            SessionManager = RuntimeHelpers.GetUninitializedObject(server.GetType("Emby.Server.Implementations.Session.SessionManager", true));
            Session = Activator.CreateInstance(_controller.GetType("MediaBrowser.Controller.Session.SessionInfo", true), SessionManager, _logger);
            Set(Session, "Id", "synthetic-device-session");
            var connectionsField = SessionManager.GetType().GetField("_activeConnections", BindingFlags.Instance | BindingFlags.NonPublic);
            var connections = Activator.CreateInstance(connectionsField.FieldType);
            connections.GetType().GetMethod("TryAdd").Invoke(connections, new object[] { "synthetic-device-session", Session });
            connectionsField.SetValue(SessionManager, connections);
            var library = Proxy(_controller.GetType("MediaBrowser.Controller.Library.ILibraryManager", true), (m, a) =>
            {
                if (m.Name == "GetItemById") return Activator.CreateInstance(_controller.GetType("MediaBrowser.Controller.Entities.Video", true));
                return Default(m.ReturnType);
            });
            Set(SessionManager, "_libraryManager", library);
            Set(SessionManager, "_logger", _logger);
            Set(SessionManager, "_mediaSourceManager", mediaSourceManager);
            var users = Proxy(_controller.GetType("MediaBrowser.Controller.Library.IUserManager", true));
            Set(SessionManager, "_userManager", users);
            var configType = _controller.GetType("MediaBrowser.Controller.Configuration.IServerConfigurationManager", true);
            var config = Proxy(configType, (m, a) =>
            {
                if (m.Name == "get_Configuration") return Activator.CreateInstance(_model.GetType("MediaBrowser.Model.Configuration.ServerConfiguration", true));
                if (m.Name == "GetConfiguration" && (string)a[0] == "encoding") return Activator.CreateInstance(_model.GetType("MediaBrowser.Model.Configuration.EncodingOptions", true));
                return Default(m.ReturnType);
            });
            var logManager = Proxy(_model.GetType("MediaBrowser.Model.Logging.ILogManager", true), (m, a) => m.Name == "GetLogger" ? _logger : Default(m.ReturnType));
            var encodingManager = RuntimeHelpers.GetUninitializedObject(_encoding.GetType("Emby.Server.MediaEncoding.Api.EncodingManager", true));
            foreach (var field in encodingManager.GetType().GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (!field.IsStatic && field.FieldType.GetConstructor(Type.EmptyTypes) != null && field.FieldType.Namespace != null && field.FieldType.Namespace.StartsWith("System.Collections.", StringComparison.Ordinal))
                    field.SetValue(encodingManager, Activator.CreateInstance(field.FieldType));
            }
            var diagnosticsType = _controller.GetType("MediaBrowser.Controller.MediaEncoding.IEncodingDiagnosticOptionsManager", true) ?? throw new InvalidOperationException();
            var diagnostics = Proxy(diagnosticsType, (m, a) => m.Name == "GetOptions" ? Activator.CreateInstance(_encoding.GetType("Emby.Server.MediaEncoding.Configuration.EncodingDiagnosticOptions", true)) : Default(m.ReturnType));
            var host = Proxy(_controller.GetType("MediaBrowser.Controller.IServerApplicationHost", true), (m, a) =>
            {
                if (m.Name == "Resolve")
                {
                    var type = m.IsGenericMethod ? m.GetGenericArguments()[0] : (Type)a[0];
                    if (type == configType) return config;
                    if (type == encodingManager.GetType()) return encodingManager;
                    if (type == diagnosticsType) return diagnostics;
                    if (type.Name == "ILogManager") return logManager;
                }
                return Default(m.ReturnType);
            });
            var serializer = Activator.CreateInstance(server.GetType("Emby.Common.Implementations.Serialization.JsonSerializer", true), null, _logger);
            var sessionContextType = _encoding.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService", true).GetConstructors().Single().GetParameters()[5].ParameterType;
            var sessionContext = Proxy(sessionContextType, (m, a) => m.Name == "GetSession" ? Session : Default(m.ReturnType));
            _service = Activator.CreateInstance(_encoding.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService", true), mediaSourceManager, serializer, logManager, host, null, sessionContext, SessionManager, null);
            Set(_service, "Logger", _logger);
            Set(_service, "LibraryManager", library);
            Set(_service, "UserManager", users);
            Set(_service, "ConfigurationManager", config);
            var authType = _controller.GetType("MediaBrowser.Controller.Net.IAuthorizationContext", true);
            Set(_service, "AuthorizationContext", Proxy(authType, (m, a) => m.Name == "GetAuthorizationInfo" ? Activator.CreateInstance(m.ReturnType) : Default(m.ReturnType)));
            var requestType = _service.GetType().GetProperty("Request").PropertyType;
            Set(_service, "Request", Proxy(requestType, (m, a) =>
            {
                if (m.Name == "get_Items") return new Dictionary<string, object> { { "IsInLocalNetwork", true } };
                return Default(m.ReturnType);
            }));
        }

        public void Register(string id, string liveId, string sourceId)
        {
            var info = NewProgress("PlaybackStartInfo", id, liveId, sourceId);
            SessionManager.GetType().GetMethod("RegisterPlaySession").Invoke(SessionManager, new[] { info, Session });
        }

        public object PlaybackInfo(string current, string liveId, string sourceId)
        {
            var request = Activator.CreateInstance(_encoding.GetType("Emby.Server.MediaEncoding.Api.GetPostedPlaybackInfo", true));
            Set(request, "Id", "0");
            Set(request, "IsPlayback", true);
            Set(request, "CurrentPlaySessionId", current);
            Set(request, "LiveStreamId", liveId);
            Set(request, "MediaSourceId", sourceId);
            var task = (Task)_service.GetType().GetMethod("Post", new[] { request.GetType() }).Invoke(_service, new[] { request });
            task.GetAwaiter().GetResult();
            return Get(task, "Result");
        }

        public void RaiseStart(string sourceId, string id)
        {
            var args = Activator.CreateInstance(_controller.GetType("MediaBrowser.Controller.Library.PlaybackProgressEventArgs", true));
            Set(args, "Session", Session);
            Set(args, "MediaSourceId", sourceId);
            Set(args, "PlaySessionId", id);
            var handlers = (Delegate)SessionManager.GetType().GetField("PlaybackStart", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(SessionManager);
            handlers?.DynamicInvoke(SessionManager, args);
        }

        public void StartAndWait(string id, string liveId, string sourceId)
        {
            InvokeAndWait("PlaybackStart", () =>
            {
                var info = NewProgress("PlaybackStartInfo", id, liveId, sourceId);
                var method = SessionManager.GetType().GetMethod("OnPlaybackStart", new[] { info.GetType(), Session.GetType() });
                ((Task)method.Invoke(SessionManager, new[] { info, Session })).GetAwaiter().GetResult();
            });
        }

        public void StopAndWait(string id, string liveId, string sourceId) => InvokeAndWait("PlaybackStopped", () => Stop(id, liveId, sourceId));

        private void InvokeAndWait(string eventName, Action invoke)
        {
            using var delivered = new ManualResetEventSlim();
            var observer = new NativeEventObserver(delivered);
            var eventInfo = SessionManager.GetType().GetEvent(eventName);
            var handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, observer, typeof(NativeEventObserver).GetMethod("OnEvent"));
            eventInfo.AddEventHandler(SessionManager, handler);
            try
            {
                invoke();
                if (!delivered.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Native " + eventName + " callback was not delivered");
            }
            finally { eventInfo.RemoveEventHandler(SessionManager, handler); }
        }

        public void Stop(string id, string liveId, string sourceId)
        {
            var info = NewProgress("PlaybackStopInfo", id, liveId, sourceId);
            var method = SessionManager.GetType().GetMethods().Single(m => m.Name == "OnPlaybackStopped" && m.GetParameters().Length == 2);
            ((Task)method.Invoke(SessionManager, new[] { info, Session })).GetAwaiter().GetResult();
        }

        private object NewProgress(string name, string id, string liveId, string sourceId)
        {
            var info = Activator.CreateInstance(_model.GetType("MediaBrowser.Model.Session." + name, true));
            Set(info, "PlaySessionId", id);
            Set(info, "LiveStreamId", liveId);
            Set(info, "MediaSourceId", sourceId);
            Set(info, "ItemId", "0");
            return info;
        }

        internal static object Get(object owner, string name) => owner.GetType().GetProperty(name).GetValue(owner);
        internal static void Set(object owner, string name, object value)
        {
            for (var type = owner.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property?.SetMethod != null) { property.SetValue(owner, value); return; }
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) { field.SetValue(owner, value); return; }
            }
            throw new InvalidOperationException("Missing fixture member " + owner.GetType().FullName + "." + name);
        }

        private static object Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
        private static object Proxy(Type type, Func<MethodInfo, object[], object> handler = null)
        {
            var proxy = DispatchProxy.Create(type, typeof(NativeFixtureProxy));
            ((NativeFixtureProxy)proxy).Handler = handler ?? ((m, a) => Default(m.ReturnType));
            return proxy;
        }
    }

    internal sealed class NativeEventObserver
    {
        private readonly ManualResetEventSlim _delivered;
        public NativeEventObserver(ManualResetEventSlim delivered) => _delivered = delivered;
        public void OnEvent(object sender, EventArgs args) => _delivered.Set();
    }

    public class NativeFixtureProxy : DispatchProxy
    {
        internal Func<MethodInfo, object[], object> Handler;
        protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
    }
}
