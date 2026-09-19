using VideoDownloaderConsole;
using Xunit;

namespace FbDl.Tests;

public sealed class RangeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fb-range-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadsExactBytesWithBoundedParallelRequests()
    {
        await using var server = new RangeServer();
        var path = await Download(server);
        Assert.Equal(server.Bytes, await File.ReadAllBytesAsync(path));
        Assert.InRange(server.MaximumActiveRequests, 2, 4);
        Assert.All(server.IfRanges, tag => Assert.Equal(server.ETag, tag));
        Assert.Equal(0, server.FullRequests);
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
    }

    [Theory]
    [InlineData("ignore")]
    [InlineData("ignore-chunk")]
    [InlineData("changed")]
    [InlineData("bounds")]
    [InlineData("416")]
    [InlineData("no-etag")]
    [InlineData("weak-etag")]
    public async Task UnsuitableRangesFallBackSafely(string mode)
    {
        await using var server = new RangeServer { Mode = mode };
        var path = await Download(server);
        Assert.Equal(server.Bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(mode == "ignore" ? 0 : 1, server.FullRequests);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("429")]
    [InlineData("503")]
    public async Task TransientChunkFailuresRetryWithoutCorruptingTheFile(string mode)
    {
        await using var server = new RangeServer { Mode = mode };
        var path = await Download(server);
        Assert.Equal(server.Bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(7, server.Ranges.Count); // probe, five chunks, one retry
        Assert.Equal(0, server.FullRequests);
    }

    [Fact]
    public async Task OneConnectionUsesOneFullRequest()
    {
        await using var server = new RangeServer();
        var path = await Download(server, 1);
        Assert.Equal(server.Bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(server.Ranges);
        Assert.Equal(1, server.FullRequests);
    }

    private async Task<string> Download(RangeServer server, int connections = 4)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return await new VideoTransferService(client).DownloadAsync(server.Url, "video.mp4", "video:123|hd",
            new DownloadOptions { OutputDirectory = directory, Connections = connections });
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
