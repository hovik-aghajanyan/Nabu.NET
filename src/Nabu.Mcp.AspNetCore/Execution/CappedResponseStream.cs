using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>
    /// Write-only response body that keeps at most <see cref="NabuMcpOptions.MaxResponseBytes"/> bytes
    /// in memory. Bytes past the cap are counted but discarded as they arrive, so an action that
    /// produces hundreds of megabytes costs the process no more than the cap - the truncation limit is
    /// a memory bound, not just an output bound - while truncation remains detectable.
    /// </summary>
    internal sealed class CappedResponseStream : Stream
    {
        private readonly MemoryStream _buffer = new MemoryStream();
        private readonly int _capacity;
        private long _totalWritten;

        public CappedResponseStream(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
        }

        /// <summary>Total bytes the application wrote, including the discarded tail.</summary>
        public long TotalBytesWritten
        {
            get { return _totalWritten; }
        }

        /// <summary>True when the application wrote past the cap and the tail was discarded.</summary>
        public bool Overflowed
        {
            get { return _totalWritten > _capacity; }
        }

        /// <summary>Decodes the retained bytes as UTF-8 without copying the buffer.</summary>
        public string ReadBufferedText()
        {
            return _buffer.Length == 0
                ? string.Empty
                : Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { return _totalWritten; }
        }

        public override long Position
        {
            get { return _totalWritten; }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || buffer.Length - offset < count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var keep = (int)Math.Min(count, Math.Max(0L, _capacity - _buffer.Length));
            if (keep > 0)
            {
                _buffer.Write(buffer, offset, keep);
            }

            _totalWritten += count;
        }

        public override void WriteByte(byte value)
        {
            if (_buffer.Length < _capacity)
            {
                _buffer.WriteByte(value);
            }

            _totalWritten++;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            try
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

#if !NETSTANDARD2_0
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var keep = (int)Math.Min(buffer.Length, Math.Max(0L, _capacity - _buffer.Length));
            if (keep > 0)
            {
                _buffer.Write(buffer.Slice(0, keep));
            }

            _totalWritten += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            Write(buffer.Span);
            return default;
        }
#endif

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
