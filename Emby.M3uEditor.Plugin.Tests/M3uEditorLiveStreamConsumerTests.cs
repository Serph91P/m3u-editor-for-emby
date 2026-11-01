using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    [Collection(PluginSingletonCollection.Name)]
    public class M3uEditorLiveStreamConsumerTests
    {
        [Fact]
        public void ConsumerCallbacks_ArePublicVoidMethodsWithSingleStringParameter()
        {
            AssertConsumerMethod("AddConsumer");
            AssertConsumerMethod("RemoveConsumer");
        }

        [Fact]
        public void ConsumerCallbacks_ReleaseOnlyMatchingRegistrations()
        {
            using (var stream = CreateLiveStream(new TrackingHttpHandler()))
            {
                var addConsumer = GetConsumerMethod("AddConsumer");
                var removeConsumer = GetConsumerMethod("RemoveConsumer");

                Assert.Equal(0, stream.ConsumerCount);
                addConsumer.Invoke(stream, new object[] { "same-consumer" });
                addConsumer.Invoke(stream, new object[] { "same-consumer" });
                addConsumer.Invoke(stream, new object[] { "another-consumer" });
                Assert.Equal(3, stream.ConsumerCount);

                removeConsumer.Invoke(stream, new object[] { "same-consumer" });
                removeConsumer.Invoke(stream, new object[] { "missing-consumer" });
                Assert.Equal(2, stream.ConsumerCount);
                removeConsumer.Invoke(stream, new object[] { "another-consumer" });
                removeConsumer.Invoke(stream, new object[] { "another-consumer" });
                Assert.Equal(1, stream.ConsumerCount);
                removeConsumer.Invoke(stream, new object[] { "same-consumer" });
                removeConsumer.Invoke(stream, new object[] { "same-consumer" });
                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("probe")]
        public void ConsumerCallbacks_ProbeStopCannotReleasePlayback(string probeId)
        {
            using (var stream = CreateLiveStream(new TrackingHttpHandler()))
            {
                stream.AddConsumer(probeId);
                stream.AddConsumer("playback");
                stream.RemoveConsumer(probeId);
                stream.RemoveConsumer(probeId);
                stream.RemoveConsumer("foreign");
                Assert.Equal(1, stream.ConsumerCount);
                stream.RemoveConsumer("playback");
                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Fact]
        public void ConsumerCallbacks_ConcurrentStopsPreserveRemainingMemberships()
        {
            using (var stream = CreateLiveStream(new TrackingHttpHandler()))
            {
                Parallel.For(0, 1000, i => stream.AddConsumer("viewer-" + i));
                Assert.Equal(1000, stream.ConsumerCount);
                Parallel.For(0, 500, i =>
                {
                    stream.RemoveConsumer("viewer-" + i);
                    stream.RemoveConsumer("viewer-" + i);
                    stream.RemoveConsumer("foreign");
                });
                Assert.Equal(500, stream.ConsumerCount);
                Parallel.For(500, 1000, i => stream.RemoveConsumer("viewer-" + i));
                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Fact]
        public void ConsumerCallbacks_LegacySetterRemainsWritableWithoutGivingForeignStopsOwnership()
        {
            using (var stream = CreateLiveStream(new TrackingHttpHandler()))
            {
                stream.ConsumerCount = 2;
                stream.RemoveConsumer("foreign");
                Assert.Equal(2, stream.ConsumerCount);
                stream.AddConsumer("registered");
                stream.ConsumerCount = 5;
                stream.RemoveConsumer("registered");
                stream.RemoveConsumer("registered");
                Assert.Equal(4, stream.ConsumerCount);
                stream.ConsumerCount--;
                Assert.Equal(3, stream.ConsumerCount);
                stream.ConsumerCount = 0;
                stream.RemoveConsumer(null);
                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Fact]
        public void ConsumerCallbacks_PreserveLegacyCounterAndDoNotOwnTransport()
        {
            var handler = new TrackingHttpHandler();
            using (var stream = CreateLiveStream(handler))
            {
                stream.ConsumerCount = 7;
                Assert.False(stream.EnableStreamSharing);

                GetConsumerMethod("AddConsumer").Invoke(stream, new object[] { "consumer" });
                GetConsumerMethod("RemoveConsumer").Invoke(stream, new object[] { "consumer" });

                Assert.Equal(7, stream.ConsumerCount);
                Assert.Equal(0, handler.RequestCount);
                Assert.Equal(0, handler.DisposeCount);
            }
        }

        [Fact]
        public async Task Open_DefersConnection_AndCloseOwnsDisposal()
        {
            GetConsumerMethod("AddConsumer");
            var handler = new TrackingHttpHandler();
            var previousPlugin = SetTestPluginInstance();
            try
            {
                using (var stream = CreateLiveStream(handler))
                using (var output = new MemoryStream())
                {
                    await stream.Open(CancellationToken.None);
                    Assert.Equal(0, handler.RequestCount);

                    await stream.CopyToAsync(output, null, null, CancellationToken.None);
                    Assert.Equal(1, handler.RequestCount);
                    Assert.NotNull(handler.Response);
                    Assert.Equal(0, handler.Response.DisposeCount);
                    Assert.Equal(0, handler.ResponseStream.DisposeCount);

                    await stream.Close();
                    Assert.Equal(1, handler.Response.DisposeCount);
                    Assert.Equal(1, handler.ResponseStream.DisposeCount);
                    Assert.Equal(1, handler.DisposeCount);
                }
            }
            finally
            {
                SetPluginInstance(previousPlugin);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Issue133_CanceledCopyReconnectsBothOverloads(bool pipe)
        {
            var previousPlugin = SetTestPluginInstance();
            var handler = new ReconnectHandler();
            try
            {
                using (var stream = CreateLiveStream(handler))
                using (var cancellation = new CancellationTokenSource())
                {
                    await stream.Open(CancellationToken.None);
                    Assert.Equal(0, handler.Requests);
                    Task Copy(CancellationToken token, MemoryStream output) => pipe
                        ? stream.CopyToAsync(System.IO.Pipelines.PipeWriter.Create(output, new System.IO.Pipelines.StreamPipeWriterOptions(leaveOpen: true)), token)
                        : stream.CopyToAsync(output, null, null, token);
                    using (var first = new MemoryStream())
                    {
                        var copy = Copy(cancellation.Token, first);
                        await handler.Aborted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        cancellation.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(TimeSpan.FromSeconds(3)));
                    }
                    using (var second = new MemoryStream())
                    {
                        await Copy(CancellationToken.None, second);
                        Assert.Equal(new byte[] { 1, 2, 3 }, second.ToArray());
                    }
                    Assert.Equal(2, handler.Requests);
                    Assert.True(handler.Aborted.Disposed);
                    Assert.Equal(1, handler.Responses[0].DisposeCount);
                    await stream.Close();
                    Assert.True(handler.Disposed);
                    Assert.True(handler.LastStreamDisposed);
                    Assert.Equal(1, handler.Responses[1].DisposeCount);
                }
            }
            finally { SetPluginInstance(previousPlugin); }
        }

        private sealed class AbortingStream : Stream
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Disposed { get; private set; }
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
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long l) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }

        private sealed class ReconnectHandler : HttpMessageHandler
        {
            public System.Collections.Generic.List<TrackingHttpResponse> Responses { get; } = new System.Collections.Generic.List<TrackingHttpResponse>();
            public AbortingStream Aborted { get; } = new AbortingStream();
            public int Requests { get; private set; }
            public bool Disposed { get; private set; }
            private TrackingStream _last;
            public bool LastStreamDisposed => _last?.DisposeCount == 1;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++;
                Stream body = Requests == 1 ? Aborted : (_last = new TrackingStream());
                var response = new TrackingHttpResponse(HttpStatusCode.OK) { Content = new StreamContent(body) };
                Responses.Add(response);
                return Task.FromResult<HttpResponseMessage>(response);
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private static M3uEditorLiveStream CreateLiveStream(HttpMessageHandler handler)
        {
            return new M3uEditorLiveStream(
                new MediaSourceInfo { Id = "stream", Path = "https://example.test/live" },
                "tuner",
                new HttpClient(handler));
        }

        private static void AssertConsumerMethod(string name)
        {
            var method = GetConsumerMethod(name);
            Assert.True(method.IsPublic);
            Assert.Equal(typeof(void), method.ReturnType);
            var parameters = method.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(string), parameters[0].ParameterType);
            Assert.Equal("consumerId", parameters[0].Name);
        }

        private static MethodInfo GetConsumerMethod(string name)
        {
            var method = typeof(M3uEditorLiveStream).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(string) },
                null);
            Assert.NotNull(method);
            return method;
        }

        private static Plugin SetTestPluginInstance()
        {
            var previous = Plugin.InstanceOrNull;
            var instance = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            for (var type = typeof(Plugin); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(object) && field.GetValue(instance) == null)
                        field.SetValue(instance, new object());
                    if (field.FieldType == typeof(PluginConfiguration) && field.GetValue(instance) == null)
                        field.SetValue(instance, new PluginConfiguration());
                }
            }

            SetPluginInstance(instance);
            return previous;
        }

        private static void SetPluginInstance(Plugin instance)
        {
            typeof(Plugin).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, instance);
        }

        private sealed class TrackingHttpHandler : HttpMessageHandler
        {
            private TrackingHttpResponse _response;

            public int RequestCount { get; private set; }
            public int DisposeCount { get; private set; }
            public TrackingHttpResponse Response => _response;
            public TrackingStream ResponseStream { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                ResponseStream = new TrackingStream();
                _response = new TrackingHttpResponse(HttpStatusCode.OK)
                {
                    Content = new StreamContent(ResponseStream),
                };
                return Task.FromResult<HttpResponseMessage>(_response);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    DisposeCount++;
                    _response?.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingHttpResponse : HttpResponseMessage
        {
            private bool _disposed;

            public TrackingHttpResponse(HttpStatusCode statusCode) : base(statusCode) { }

            public int DisposeCount { get; private set; }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_disposed)
                {
                    DisposeCount++;
                    _disposed = true;
                }
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingStream : MemoryStream
        {
            private bool _disposed;

            public TrackingStream() : base(new byte[] { 1, 2, 3 }) { }

            public int DisposeCount { get; private set; }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_disposed)
                {
                    DisposeCount++;
                    _disposed = true;
                }
                base.Dispose(disposing);
            }
        }
    }
}
