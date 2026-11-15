using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.RuntimeCompatibility
{
    internal sealed partial class RegistryLifecycleTests
    {
        // Execute the server's request interpretation and consumer registration, not a model of them.
        private void DvrLifecycle(ServerAssemblyLoadContext context, Assembly plugin)
        {
            var controller = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Controller.dll"));
            var model = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Model.dll"));
            var liveTv = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "Emby.LiveTV.dll"));
            var embyType = liveTv.GetType("Emby.LiveTV.EmbyTV", true);
            var common = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Common.dll"));
            var server = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "Emby.Server.Implementations.dll"));
            common.GetType("MediaBrowser.Common.Extensions.BaseExtensions", true).GetProperty("CryptographyProvider").SetValue(null,
                Activator.CreateInstance(server.GetType("Emby.Server.Implementations.Cryptography.CryptographyProvider", true)));
            var emby = RuntimeHelpers.GetUninitializedObject(embyType);
            object pending = null;
            object source = null;
            object Proxy(Type type, Func<MethodInfo, object[], object> handler)
            {
                var proxy = DispatchProxy.Create(type, typeof(NativeFixtureProxy));
                ((NativeFixtureProxy)proxy).Handler = handler;
                return proxy;
            }
            void SetField(object target, string name, Func<MethodInfo, object[], object> handler)
            {
                var field = target.GetType().GetField(name, Instance);
                field.SetValue(target, Proxy(field.FieldType, handler));
            }
            var tunerType = controller.GetType("MediaBrowser.Controller.LiveTv.ITunerHost", true);
            var tuner = Proxy(tunerType, (method, args) => method.Name switch
            {
                "get_Type" => "synthetic",
                "GetChannelIdPrefix" => "synthetic-",
                "GetChannelStream" => typeof(Task).GetMethod("FromResult").MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, new[] { pending }),
                _ => throw new InvalidOperationException("Unexpected tuner operation: " + method.Name)
            });
            var tunerInfo = Activator.CreateInstance(model.GetType("MediaBrowser.Model.LiveTv.TunerHostInfo", true));
            NativePlaybackFixture.Set(tunerInfo, "Type", "synthetic");
            var options = Activator.CreateInstance(model.GetType("MediaBrowser.Model.LiveTv.LiveTvOptions", true));
            var tuners = Array.CreateInstance(tunerInfo.GetType(), 1);
            tuners.SetValue(tunerInfo, 0);
            NativePlaybackFixture.Set(options, "TunerHosts", tuners);
            SetField(emby, "_config", (method, args) => method.Name == "GetConfiguration" ? options : throw new InvalidOperationException(method.Name));
            embyType.GetField("_logger", Instance).SetValue(emby, DispatchProxy.Create(model.GetType("MediaBrowser.Model.Logging.ILogger", true), typeof(OfflineLogger)));
            var managerField = embyType.GetField("_liveTvManager", Instance);
            var managerInterface = managerField.FieldType.GetGenericArguments()[0];
            var nativeTuners = Array.CreateInstance(tunerType, 1);
            nativeTuners.SetValue(tuner, 0);
            var tvManager = RuntimeHelpers.GetUninitializedObject(managerInterface);
            managerInterface.GetField("_tunerHosts", Instance).SetValue(tvManager, nativeTuners);
            managerField.SetValue(emby, Expression.Lambda(managerField.FieldType, Expression.Constant(tvManager, managerInterface)).Compile());
            var providerType = controller.GetType("MediaBrowser.Controller.Library.IMediaSourceProvider", true);
            var provider = Proxy(providerType, (method, args) =>
            {
                Require(method.Name == "OpenMediaSource", "Unexpected provider operation");
                return embyType.GetMethod("GetChannelStreamWithDirectStreamProvider").Invoke(emby,
                    new[] { null, "synthetic-channel", "synthetic-source", args[1], args[2], args[3] });
            });
            var providers = Array.CreateInstance(providerType, 1);
            providers.SetValue(provider, 0);
            _managerType.GetField("_providers", Instance).SetValue(_manager, providers);
            SetField(_manager, "_userManager", (method, args) => null);
            _managerType.GetField("_jsonSerializer", Instance).SetValue(_manager,
                Activator.CreateInstance(server.GetType("Emby.Common.Implementations.Serialization.JsonSerializer", true),
                    null, embyType.GetField("_logger", Instance).GetValue(emby)));
            var peer = Open(42, "active-playback");
            var activeRecording = Open(43, "Recording_active");
            try
            {
                for (var cycle = 0; cycle < 12; cycle++)
                {
                    var handler = new DvrHttpHandler(cycle % 3);
                    source = _factory.Invoke(_host, new object[] { 42, "https://synthetic.example.test/record.ts", null, "Synthetic/1.0" });
                    pending = Activator.CreateInstance(_liveType, Instance, null, new object[] { source, "synthetic-tuner", new HttpClient(handler), null }, null);
                    NativePlaybackFixture.Set(source, "LiveStreamId", "synthetic-dvr-" + cycle);
                    NativePlaybackFixture.Set(source, "SupportsDirectStream", false);
                    NativePlaybackFixture.Set(source, "IsRemote", true);
                    var requestType = _managerType.GetMethod("OpenLiveStreamInternal", Instance).GetParameters()[0].ParameterType;
                    var request = Activator.CreateInstance(requestType);
                    var recordingConsumer = "Recording_synthetic-" + cycle;
                    NativePlaybackFixture.Set(request, "ConsumerId", recordingConsumer);
                    NativePlaybackFixture.Set(request, "OpenToken", Prefix(provider.GetType().FullName) + "synthetic");
                    var open = (Task)_managerType.GetMethod("OpenLiveStreamInternal", Instance).Invoke(_manager, new[] { request, (object)CancellationToken.None });
                    open.GetAwaiter().GetResult();
                    var consumers = (IList)_liveType.GetField("_consumers", Instance).GetValue(pending);
                    Require(consumers.Count == 1 && consumers[0] == null, "Native DVR request no longer registers a null PlaySessionId; revisit workaround");
                    Console.WriteLine("Native DVR open: ConsumerId supplied, null PlaySessionId registered.");
                    AssertNativeCapacity(context, plugin, true);
                    var recorderType = liveTv.GetType("Emby.LiveTV.DirectRecorder", true);
                    var fileSystemType = model.GetType("MediaBrowser.Model.IO.IFileSystem", true);
                    var output = new MemoryStream();
                    var fileSystem = Proxy(fileSystemType, (method, args) => method.Name switch
                    {
                        "CreateDirectory" => null,
                        "GetDirectoryName" => "synthetic",
                        "GetFileStream" => output,
                        _ => throw new InvalidOperationException(method.Name)
                    });
                    var recorder = Activator.CreateInstance(recorderType, embyType.GetField("_logger", Instance).GetValue(emby), null, fileSystem, null);
                    using var cancellation = new CancellationTokenSource();
                    var record = (Task)recorderType.GetMethod("Record").Invoke(recorder,
                        new[] { pending, null, source, "synthetic/record.ts", (Action)(() => { }), cancellation.Token });
                    if (cycle % 3 == 1)
                    {
                        Require(handler.Entered.Wait(TimeSpan.FromSeconds(3)), "Recorder did not enter cancellable copy");
                        cancellation.Cancel();
                    }
                    try
                    {
                        record.GetAwaiter().GetResult();
                        Require(cycle % 3 == 0, "Expected cancellation or upstream error");
                        Require(output.ToArray().SequenceEqual(new byte[] { 1, 2, 3 }), "Native recorder lost output bytes");
                    }
                    catch (OperationCanceledException) when (cycle % 3 == 1) { }
                    catch (IOException) when (cycle % 3 == 2) { }
                    var key = (string)NativePlaybackFixture.Get(source, "LiveStreamId");
                    ((Task)_managerType.GetMethod("CloseLiveStream", Instance).Invoke(_manager, new object[] { key, recordingConsumer })).GetAwaiter().GetResult();
                    Require(_managerType.GetMethod("GetLiveStreamInfo", Instance).Invoke(_manager, new object[] { key }) == null,
                        "DVR completion/cancellation/error retained native registry entry and tuner slot");
                    Require(handler.Disposed, "DVR final close did not dispose transport");
                    ((Task)_managerType.GetMethod("CloseLiveStream", Instance).Invoke(_manager, new object[] { key, recordingConsumer })).GetAwaiter().GetResult();
                    Alive(peer, 1);
                    Alive(activeRecording, 1);
                    AssertNativeCapacity(context, plugin, false);
                    Console.WriteLine("Native DVR cycle " + cycle + ": " + new[] { "EOF/completed", "cancelled", "upstream error" }[cycle % 3] + "; registry removed, capacity reusable, active peers retained.");
                }
            }
            finally
            {
                (pending as IDisposable)?.Dispose();
                Stop(peer, "active-playback");
                Stop(activeRecording, "Recording_active");
            }
        }

        private sealed class DvrHttpHandler : HttpMessageHandler
        {
            private readonly int _mode;
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public bool Disposed;
            public DvrHttpHandler(int mode) { _mode = mode; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Entered.Set();
                if (_mode == 1) await Task.Delay(Timeout.Infinite, token);
                if (_mode == 2) throw new IOException("Synthetic upstream failure");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }
    }
}
