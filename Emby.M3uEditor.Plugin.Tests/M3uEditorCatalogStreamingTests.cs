using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class M3uEditorCatalogStreamingTests
    {
        private const long CatalogBudget = 256L * 1024 * 1024;
        private static readonly byte[] EmptyCatalog = Encoding.UTF8.GetBytes(
            "{\"api_version\":1,\"full_snapshot\":true,\"revision\":\"" + new string('a', 64) + "\",\"mappings\":[]}");

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CatalogBeyondBudget_StopsAtOneSentinelByteWithoutRetry(bool misleadingLength)
        {
            var stream = new PaddedStream(EmptyCatalog, CatalogBudget + 8192);
            using (var content = new StreamContent(stream))
            {
                if (misleadingLength) content.Headers.ContentLength = 1;
                var handler = new ContentHandler(content);
                using (var http = new HttpClient(handler))
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        new M3uEditorClient(http).GetCatalogAsync("https://editor.example", "account", "credential", CancellationToken.None));
                    Assert.Contains("response limit", error.Message);
                    Assert.Equal(CatalogBudget + 1, stream.BytesRead);
                    Assert.Equal(1, handler.Requests);
                    Assert.True(stream.Disposed);
                }
            }
        }

        [Fact]
        public async Task DeclaredCatalogBeyondBudget_IsRejectedBeforeOpeningBody()
        {
            var stream = new PaddedStream(EmptyCatalog, EmptyCatalog.Length);
            using (var content = new StreamContent(stream))
            {
                content.Headers.ContentLength = CatalogBudget + 1;
                var handler = new ContentHandler(content);
                using (var http = new HttpClient(handler))
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => new M3uEditorClient(http).GetCatalogAsync(
                        "https://editor.example", "account", "credential", CancellationToken.None));
                    Assert.Equal(0, stream.BytesRead);
                    Assert.Equal(1, handler.Requests);
                }
            }
        }

        [Fact]
        public async Task CatalogExactlyAtBudget_WithTrailingWhitespace_IsReadThroughEof()
        {
            var stream = new PaddedStream(EmptyCatalog, CatalogBudget);
            using (var http = new HttpClient(new ContentHandler(new StreamContent(stream))))
            {
                var catalog = await new M3uEditorClient(http).GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None);
                Assert.Empty(catalog.Mappings);
                Assert.Equal(CatalogBudget, stream.BytesRead);
                Assert.True(stream.EofObserved);
                Assert.True(stream.Disposed);
            }
        }

        [Fact]
        public async Task TrailingNonWhitespace_IsInvalidRatherThanAnAcceptedPartialSnapshot()
        {
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(EmptyCatalog) + " {}");
            using (var http = new HttpClient(new ContentHandler(new ByteArrayContent(bytes))))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new M3uEditorClient(http).GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None));
                Assert.Contains("invalid JSON", error.Message);
            }
        }

        [Fact]
        public async Task CancellationWhileWaitingForEof_IsHonoredWithoutRetry()
        {
            var stream = new PaddedStream(EmptyCatalog, EmptyCatalog.Length, true);
            var handler = new ContentHandler(new StreamContent(stream));
            using (var http = new HttpClient(handler))
            using (var cancellation = new CancellationTokenSource())
            {
                var request = new M3uEditorClient(http).GetCatalogAsync(
                    "https://editor.example", "account", "credential", cancellation.Token);
                await stream.WaitingAtEof.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(request.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
                Assert.Equal(1, handler.Requests);
                Assert.True(stream.Disposed);
            }
        }

        [Fact]
        public async Task ConflictErrorResponse_RetainsTheSmallNonCatalogBudget()
        {
            var stream = new PaddedStream(EmptyCatalog, M3uEditorClient.MaximumResponseBytes + 8192L);
            var handler = new ContentHandler(new StreamContent(stream), HttpStatusCode.Conflict);
            using (var http = new HttpClient(handler))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new M3uEditorClient(http).GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None));
                Assert.Contains("response limit", error.Message);
                Assert.Equal(M3uEditorClient.MaximumResponseBytes + 1L, stream.BytesRead);
                Assert.Equal(1, handler.Requests);
            }
        }

        [Fact]
        public async Task LargeValidCatalog_PreservesAllItemsBeyondTheOldItemCeiling()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(M3uEditorClientTests.CatalogJson);
            var mapping = catalog.Mappings[0];
            var itemJson = JsonSerializer.Serialize(mapping.Items[0]);
            mapping.Items.Clear();
            for (var index = 0; index < 16000; index++)
            {
                var item = JsonSerializer.Deserialize<M3uEditorCatalogItem>(itemJson);
                item.CanonicalId = "movie:synthetic:" + index;
                item.RelativeFolder = "synthetic-movie-" + index;
                item.BaseFilename = item.RelativeFolder;
                mapping.Items.Add(item);
            }
            using (var http = new HttpClient(new ContentHandler(new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(catalog)))))
            {
                var result = await new M3uEditorClient(http).GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None);
                Assert.Equal(16000, Assert.Single(result.Mappings).Items.Count);
                Assert.Equal("movie:synthetic:15999", result.Mappings[0].Items[15999].CanonicalId);
            }
        }

        [Fact]
        public void OutputFileBudget_RemainsIndependentOfTheLargerItemBudget()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(M3uEditorClientTests.CatalogJson);
            var template = catalog.Mappings[0].Items[0];
            catalog.Mappings[0].Items.Clear();
            for (var index = 0; index <= M3uEditorCatalogValidator.MaximumGeneratedFiles / 2; index++)
            {
                catalog.Mappings[0].Items.Add(new M3uEditorCatalogItem
                {
                    CanonicalId = "movie:synthetic:" + index,
                    MediaType = "movie",
                    DisplayTitle = "Synthetic movie",
                    RelativeFolder = "synthetic-" + index,
                    BaseFilename = "synthetic-" + index,
                    Nfo = template.Nfo,
                    Variants = template.Variants
                });
            }

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));
            Assert.Contains("generated file limits", error.Message);
            Assert.Equal(50000, M3uEditorCatalogValidator.MaximumGeneratedFiles);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LimitedReader_AlsoBoundsSyncAndMemoryReadOverloads(bool asynchronous)
        {
            using (var input = new MemoryStream(new byte[32]))
            using (var limited = new LimitedResponseStream(input, 8))
            {
                var buffer = new byte[32];
                if (asynchronous)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await limited.ReadAsync(buffer.AsMemory(), CancellationToken.None));
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(() => limited.Read(buffer, 0, buffer.Length));
                }
                Assert.Equal(9, input.Position);
            }
        }

        private sealed class ContentHandler : HttpMessageHandler
        {
            private readonly HttpContent _content;
            private readonly HttpStatusCode _status;
            internal int Requests { get; private set; }
            internal ContentHandler(HttpContent content, HttpStatusCode status = HttpStatusCode.OK)
            {
                _content = content;
                _status = status;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(_status) { Content = _content });
            }
        }

        private sealed class PaddedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly long _length;
            private readonly bool _waitAtEof;
            internal long BytesRead { get; private set; }
            internal bool EofObserved { get; private set; }
            internal bool Disposed { get; private set; }
            internal TaskCompletionSource<bool> WaitingAtEof { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal PaddedStream(byte[] prefix, long length, bool waitAtEof = false)
            {
                _prefix = prefix;
                _length = length;
                _waitAtEof = waitAtEof;
            }
            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var size = (int)Math.Min(count, _length - BytesRead);
                if (size == 0) { EofObserved = true; return 0; }
                Array.Fill(buffer, (byte)' ', offset, size);
                var prefixSize = (int)Math.Min(size, Math.Max(0, _prefix.Length - BytesRead));
                if (prefixSize > 0) Array.Copy(_prefix, (int)BytesRead, buffer, offset, prefixSize);
                BytesRead += size;
                return size;
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_waitAtEof && BytesRead == _length)
                {
                    WaitingAtEof.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return Read(buffer, offset, count);
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
