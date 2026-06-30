using NzbWebDAV.Extensions;
using Xunit;

namespace NzbWebDAV.Tests.Extensions;

public class StreamExtensionsTests
{
    [Fact]
    public async Task DiscardBytesAsync_DiscardsRequestedBytes()
    {
        var stream = new MemoryStream([1, 2, 3, 4, 5]);

        await stream.DiscardBytesAsync(3);

        Assert.Equal(3, stream.Position);
        Assert.Equal(4, stream.ReadByte());
    }

    [Fact]
    public async Task DiscardBytesAsync_ThrowsWhenStreamEndsEarly()
    {
        var stream = new MemoryStream([1, 2]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => stream.DiscardBytesAsync(3));
    }

    [Fact]
    public async Task DiscardBytesAsync_RejectsNegativeCounts()
    {
        var stream = new MemoryStream([1, 2]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.DiscardBytesAsync(-1));
    }
}
