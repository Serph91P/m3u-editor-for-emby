using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.M3uEditor.Plugin.Client
{
    /// <summary>Non-seekable response reader that consumes at most one byte beyond its budget.</summary>
    internal sealed class LimitedResponseStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumBytes;
        private long _bytesRead;

        internal LimitedResponseStream(Stream inner, long maximumBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (maximumBytes < 0 || maximumBytes == long.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            _maximumBytes = maximumBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateArguments(buffer, offset, count);
            return Count(_inner.Read(buffer, offset, ReadSize(count)));
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateArguments(buffer, offset, count);
            cancellationToken.ThrowIfCancellationRequested();
            return Count(await _inner.ReadAsync(buffer, offset, ReadSize(count), cancellationToken).ConfigureAwait(false));
        }

        private int ReadSize(int requested)
        {
            if (_bytesRead > _maximumBytes)
                throw new InvalidOperationException("Managed backend response limit exceeded.");
            return (int)Math.Min(requested, _maximumBytes - _bytesRead + 1);
        }

        private int Count(int read)
        {
            _bytesRead += read;
            if (_bytesRead > _maximumBytes)
                throw new InvalidOperationException("Managed backend response limit exceeded.");
            return read;
        }

        private static void ValidateArguments(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
