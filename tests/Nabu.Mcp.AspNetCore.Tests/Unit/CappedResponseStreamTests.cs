using System;
using System.Text;
using System.Threading.Tasks;
using Nabu.Mcp.AspNetCore.Execution;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class CappedResponseStreamTests
    {
        [Fact]
        public void A_body_under_the_cap_is_kept_verbatim()
        {
            using var stream = new CappedResponseStream(64);

            var payload = Encoding.UTF8.GetBytes("hello world");
            stream.Write(payload, 0, payload.Length);

            Assert.False(stream.Overflowed);
            Assert.Equal(payload.Length, stream.TotalBytesWritten);
            Assert.Equal("hello world", stream.ReadBufferedText());
        }

        [Fact]
        public void A_single_write_past_the_cap_keeps_only_the_cap_and_flags_overflow()
        {
            using var stream = new CappedResponseStream(8);

            var payload = Encoding.UTF8.GetBytes(new string('x', 1000));
            stream.Write(payload, 0, payload.Length);

            Assert.True(stream.Overflowed);
            Assert.Equal(1000, stream.TotalBytesWritten);
            Assert.Equal(new string('x', 8), stream.ReadBufferedText());
        }

        [Fact]
        public void Many_small_writes_crossing_the_cap_are_cut_at_the_boundary()
        {
            using var stream = new CappedResponseStream(10);

            for (var i = 0; i < 25; i++)
            {
                var chunk = Encoding.UTF8.GetBytes(((char)('a' + (i % 26))).ToString());
                stream.Write(chunk, 0, chunk.Length);
            }

            Assert.True(stream.Overflowed);
            Assert.Equal(25, stream.TotalBytesWritten);
            Assert.Equal("abcdefghij", stream.ReadBufferedText());
        }

        [Fact]
        public void WriteByte_respects_the_cap()
        {
            using var stream = new CappedResponseStream(3);

            for (var i = 0; i < 5; i++)
            {
                stream.WriteByte((byte)('0' + i));
            }

            Assert.True(stream.Overflowed);
            Assert.Equal(5, stream.TotalBytesWritten);
            Assert.Equal("012", stream.ReadBufferedText());
        }

        [Fact]
        public async Task Async_and_span_writes_behave_like_synchronous_ones()
        {
            using var stream = new CappedResponseStream(6);

            await stream.WriteAsync(Encoding.UTF8.GetBytes("abc"), 0, 3);
            stream.Write(Encoding.UTF8.GetBytes("def").AsSpan());
            await stream.WriteAsync(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("ghi")));

            Assert.True(stream.Overflowed);
            Assert.Equal(9, stream.TotalBytesWritten);
            Assert.Equal("abcdef", stream.ReadBufferedText());
        }

        [Fact]
        public void An_empty_body_reads_as_an_empty_string()
        {
            using var stream = new CappedResponseStream(16);

            Assert.False(stream.Overflowed);
            Assert.Equal(0, stream.TotalBytesWritten);
            Assert.Equal(string.Empty, stream.ReadBufferedText());
        }

        [Fact]
        public void The_stream_is_write_only()
        {
            using var stream = new CappedResponseStream(16);

            Assert.True(stream.CanWrite);
            Assert.False(stream.CanRead);
            Assert.False(stream.CanSeek);
            Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
            Assert.Throws<NotSupportedException>(() => stream.Seek(0, System.IO.SeekOrigin.Begin));
            Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        }
    }
}
