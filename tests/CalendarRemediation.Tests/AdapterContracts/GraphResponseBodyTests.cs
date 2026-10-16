using System.Text;
using System.Text.Json;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Tests.AdapterContracts;

public sealed class GraphResponseBodyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 8)]
    [InlineData(8, 8)]
    [InlineData(8193, 8193)]
    public async Task ChunkedReadsAcceptEmptyUnderBudgetAndExactBudgetWithoutOwningStream(int length, int budget)
    {
        var expected = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new ChunkedStream(expected);

        var actual = await GraphResponseBody.ReadBoundedAsync(stream, budget);

        Assert.Equal(expected, actual);
        Assert.False(stream.IsDisposed);
        Assert.True(stream.ReadCalls > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(8192)]
    public async Task FirstByteBeyondBudgetFailsWithoutOwningStream(int budget)
    {
        using var stream = new ChunkedStream(new byte[budget + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => GraphResponseBody.ReadBoundedAsync(stream, budget));

        Assert.False(stream.IsDisposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveBudgetIsRejectedBeforeReading(int budget)
    {
        using var stream = new ChunkedStream(new byte[1]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GraphResponseBody.ReadBoundedAsync(stream, budget));

        Assert.Equal(0, stream.ReadCalls);
        Assert.False(stream.IsDisposed);
    }

    [Fact]
    public async Task CancellationAfterFirstChunkPropagatesAndLeavesCallerStreamOpen()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ChunkedStream(new byte[12], cancellation.Cancel);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GraphResponseBody.ReadBoundedAsync(stream, 12, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, stream.ChunksRead);
        Assert.False(stream.IsDisposed);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(299)]
    public void SuccessfulJsonIsDetachedFromParserAndInputStorage(int status)
    {
        var content = Encoding.UTF8.GetBytes("{\"value\":[{\"subject\":\"original\"}]}");

        var body = GraphResponseBody.ParseJson(status, content);
        Array.Fill(content, (byte)' ');

        var element = Assert.IsType<JsonElement>(body);
        Assert.Equal("original", element.GetProperty("value")[0].GetProperty("subject").GetString());
        Assert.Contains("original", element.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(412)]
    [InlineData(429)]
    [InlineData(500)]
    public void EvenValidErrorJsonIsSuppressed(int status)
    {
        var content = Encoding.UTF8.GetBytes("{\"subject\":\"private calendar data\"}");

        Assert.Null(GraphResponseBody.ParseJson(status, content));
    }

    [Theory]
    [InlineData(200, "")]
    [InlineData(204, "")]
    [InlineData(200, " ")]
    [InlineData(200, "{\"value\":")]
    [InlineData(200, "{} trailing")]
    [InlineData(503, "not json")]
    public void EmptyAndMalformedBodiesPreserveStatusWithoutAJsonBody(int status, string content)
    {
        Assert.Null(GraphResponseBody.ParseJson(status, Encoding.UTF8.GetBytes(content)));
    }

    private sealed class ChunkedStream(byte[] bytes, Action? afterFirstChunk = null) : Stream
    {
        private int _position;
        public int ReadCalls { get; private set; }
        public int ChunksRead { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var count = Math.Min(Math.Min(3, buffer.Length), bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            if (count > 0)
            {
                ChunksRead++;
                if (ChunksRead == 1)
                {
                    afterFirstChunk?.Invoke();
                }
            }
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
