using System.IO;
using SpaceMaid.App.Extensions;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ExifToolReplyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task SplitMarkersAndReadAheadPreserveReplyBoundaries(int chunkSize)
    {
        using var source = new ChunkedReader("alpha\r\n{ready7}\n{ready8}\r\nbeta\n{ready9}\n", chunkSize);
        var reader = new ExifToolBroker.ReplyReader(source);

        Assert.Equal("alpha" + Environment.NewLine + "{ready7}" + Environment.NewLine,
            await reader.ReadAsync("{ready8}", CancellationToken.None));
        Assert.Equal("beta" + Environment.NewLine, await reader.ReadAsync("{ready9}", CancellationToken.None));
    }

    [Fact]
    public async Task ExactReplyLimitAllowsReadyMarkerWithoutCountingFraming()
    {
        const int limit = 32;
        string payload = new('x', limit - Environment.NewLine.Length);
        using var source = new StringReader(payload + "\n{ready1}\n");
        var reader = new ExifToolBroker.ReplyReader(source, limit);
        Assert.Equal(payload + Environment.NewLine, await reader.ReadAsync("{ready1}", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplyLimitIncludesLineSeparatorsAndUnterminatedLines(bool newlinesOnly)
    {
        using var source = new StringReader(newlinesOnly ? new string('\n', 33) : new string('x', 33));
        var reader = new ExifToolBroker.ReplyReader(source, 32);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync("{ready1}", CancellationToken.None));
    }

    [Fact]
    public async Task EndOfStreamWithoutCompleteReadyLineIsRejected()
    {
        using var source = new StringReader("reply\n{ready1}");
        var reader = new ExifToolBroker.ReplyReader(source);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync("{ready1}", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAppliesToAlreadyBufferedReplyData()
    {
        using var source = new StringReader("first\n{ready1}\nsecond\n{ready2}\n");
        var reader = new ExifToolBroker.ReplyReader(source);
        Assert.Equal("first" + Environment.NewLine, await reader.ReadAsync("{ready1}", CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync("{ready2}", cancellation.Token));
    }

    private sealed class ChunkedReader(string text, int chunkSize) : TextReader
    {
        private int _offset;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, chunkSize), text.Length - _offset);
            text.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            return ValueTask.FromResult(count);
        }
    }
}
