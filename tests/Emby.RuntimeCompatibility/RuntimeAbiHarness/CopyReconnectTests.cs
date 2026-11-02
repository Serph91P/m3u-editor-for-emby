using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.RuntimeCompatibility
{
    internal static class CopyReconnectTests
    {
        internal static void Run(Assembly plugin, Assembly controller, string runtimeName)
        {
            var pluginType = plugin.GetType("Emby.M3uEditor.Plugin.Plugin", true);
            var configurationType = plugin.GetType("Emby.M3uEditor.Plugin.PluginConfiguration", true);
            var pluginInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(pluginType);
            for (var type = pluginType; type != null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(object) && field.GetValue(pluginInstance) == null) field.SetValue(pluginInstance, new object());
                    if (field.FieldType == configurationType && field.GetValue(pluginInstance) == null) field.SetValue(pluginInstance, Activator.CreateInstance(configurationType));
                }
            pluginType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, pluginInstance);
            var streamType = plugin.GetType("Emby.M3uEditor.Plugin.Service.M3uEditorLiveStream", true);
            var contract = controller.GetType("MediaBrowser.Controller.Library.ILiveStream", true);
            var methods = contract.GetMethods().Where(method => method.Name == "CopyToAsync").ToArray();
            if (methods.Length == 0) throw new InvalidOperationException("Native copy contract missing.");
            foreach (var method in methods) RunCopy(streamType, method, runtimeName).GetAwaiter().GetResult();
        }

        private static async Task RunCopy(Type streamType, MethodInfo nativeCopy, string runtimeName)
        {
            var constructor = streamType.GetConstructors().Single();
            var sourceType = constructor.GetParameters()[0].ParameterType;
            var source = Activator.CreateInstance(sourceType);
            sourceType.GetProperty("Id").SetValue(source, "synthetic-copy");
            sourceType.GetProperty("Path").SetValue(source, "https://upstream.example.test/stream");
            var handler = new ReconnectHandler();
            var instance = constructor.Invoke(new object[] { source, "synthetic-tuner", new HttpClient(handler), null });
            try
            {
                await (Task)streamType.GetMethod("Open").Invoke(instance, new object[] { CancellationToken.None });
                if (handler.Requests != 0) throw new InvalidOperationException("Open connected eagerly.");
                using (var cancellation = new CancellationTokenSource())
                using (var first = new MemoryStream())
                {
                    var copying = InvokeCopy(nativeCopy, instance, first, cancellation.Token);
                    await handler.First.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    cancellation.Cancel();
                    try { await copying.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("Copy ignored cancellation."); }
                    catch (OperationCanceledException) { }
                }
                using (var second = new MemoryStream())
                {
                    await InvokeCopy(nativeCopy, instance, second, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
                    if (!second.ToArray().SequenceEqual(new byte[] { 1, 2, 3 }) || handler.Requests != 2 || !handler.First.Disposed)
                        throw new InvalidOperationException("Native second copy did not reconnect and dispose the aborted response.");
                }
                await (Task)streamType.GetMethod("Close").Invoke(instance, null);
                if (!handler.Disposed || handler.Second.CanRead) throw new InvalidOperationException("Close did not dispose client/response.");
                Console.WriteLine(runtimeName + ": native " + nativeCopy + " cancellation/reconnect/deferred Open/Close passed.");
            }
            finally { ((IDisposable)instance).Dispose(); }
        }

        private static Task InvokeCopy(MethodInfo method, object instance, Stream output, CancellationToken token)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];
            if (parameters[0].ParameterType == typeof(Stream)) args[0] = output;
            else
            {
                var writerType = parameters[0].ParameterType;
                var create = writerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(candidate => candidate.Name == "Create" && candidate.GetParameters().Length == 2 && candidate.GetParameters()[0].ParameterType == typeof(Stream));
                var optionsType = create.GetParameters()[1].ParameterType;
                var ctor = optionsType.GetConstructors().Single();
                var optionsArgs = ctor.GetParameters().Select(parameter => parameter.Name == "leaveOpen" ? (object)true : parameter.DefaultValue).ToArray();
                args[0] = create.Invoke(null, new[] { (object)output, ctor.Invoke(optionsArgs) });
            }
            args[args.Length - 1] = token;
            return (Task)method.Invoke(instance, args);
        }

        private sealed class ReconnectHandler : HttpMessageHandler
        {
            internal readonly AbortingStream First = new AbortingStream();
            internal readonly MemoryStream Second = new MemoryStream(new byte[] { 1, 2, 3 });
            internal int Requests;
            internal bool Disposed;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Requests == 1 ? First : Second) });
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private sealed class AbortingStream : Stream
        {
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed;
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Disposed) throw new ObjectDisposedException(nameof(AbortingStream));
                Entered.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, token); return 0; }
                catch (OperationCanceledException) { Disposed = true; throw; }
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long length) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }
}
