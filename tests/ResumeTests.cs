using System.Text.Json;
using VideoDownloaderConsole;
using Xunit;

namespace FbDl.Tests;

public sealed class ResumeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fb-resume-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("valid")]
    [InlineData("damaged")]
    [InlineData("json")]
    [InlineData("etag")]
    [InlineData("missing")]
    [InlineData("truncated")]
    public async Task ReusesOnlyVerifiedChunksAfterInterruption(string state)
    {
        await using var first = new RangeServer { Mode = "block-after-first" };
        using var cancel = new CancellationTokenSource();
        var pending = Download(first.Url, 1, cancel.Token);
        await WaitForCheckpoint();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var partial = Assert.Single(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.part"));
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.json"));
        if (state == "damaged")
        {
            await using var file = File.OpenWrite(partial);
            file.Position = 100; file.WriteByte(123);
        }
        if (state == "json") await File.WriteAllTextAsync(manifest, "{broken");
        if (state == "missing") File.Move(partial, Path.Combine(directory, "existing.mp4"));
        if (state == "truncated") { using var file = File.OpenWrite(partial); file.SetLength(1024); }

        await using var next = new RangeServer(); // changed signed URL / port with the same video identity
        if (state == "etag") next.ETag = "\"replacement\"";
        var path = await Download(next.Url, 4);
        Assert.Equal(next.Bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(state != "valid", next.Ranges.Any(r => r.Start == 0 && r.End == VideoTransferService.ChunkSize - 1));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.part"));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.json"));
        if (state == "missing") Assert.True(File.Exists(Path.Combine(directory, "existing.mp4")));
    }

    [Fact]
    public async Task ChangedRepresentationAfterVerifiedChunkDiscardsEveryOldByte()
    {
        await using var server = new RangeServer { Mode = "change-after-first" };
        var pending = Download(server.Url, 4);
        await WaitForCheckpoint();
        var partial = Assert.Single(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.part"));
        // Chunk 0 is persisted from A before any remaining chunk receives B's headers.
        Assert.True(File.Exists(partial));
        server.ChangeRepresentation.SetResult();
        var path = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(server.Replacement, await File.ReadAllBytesAsync(path));
        Assert.False(server.Bytes.SequenceEqual(server.Replacement));
        Assert.Equal(1, server.FullRequests);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.part"));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.json"));
    }

    [Fact]
    public async Task SameCheckpointCannotBeUsedConcurrently()
    {
        await using var server = new RangeServer { Mode = "block-after-first" };
        using var cancel = new CancellationTokenSource();
        var pending = Download(server.Url, 1, cancel.Token);
        try
        {
            await WaitForCheckpoint();
            var ex = await Assert.ThrowsAsync<IOException>(() => Download(server.Url, 1));
            Assert.Contains("already active", ex.Message);
        }
        finally
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
    }

    [Fact]
    public async Task RepresentationChangeRemovesCheckpointBeforeFallback()
    {
        await using var server = new RangeServer { Mode = "changed" };
        var path = await Download(server.Url, 4);
        Assert.Equal(server.Bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, server.FullRequests);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.part"));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".fb-dl"), "*.json"));
    }

    private async Task WaitForCheckpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var state = Path.Combine(directory, ".fb-dl");
            if (Directory.Exists(state))
                foreach (var path in Directory.GetFiles(state, "*.json"))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, timeout.Token));
                        if (json.RootElement.GetProperty("Completed").EnumerateObject().Any()) return;
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
            await Task.Delay(10, timeout.Token);
        }
    }
    private async Task<string> Download(string url, int connections, CancellationToken token = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await new VideoTransferService(client).DownloadAsync(url, "video.mp4", "facebook:123|hd|mp4",
            new DownloadOptions { OutputDirectory = directory, Resume = true, Connections = connections, MaxRetries = 0 }, token);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
