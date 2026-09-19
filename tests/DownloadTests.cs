using System.Net;
using VideoDownloaderConsole;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FbDl.Tests;

public class DownloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fb-dl-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TextWriter output = Console.Out;
    private readonly StringWriter capture = new();
    private readonly List<HttpClient> clients = [];

    public DownloadTests()
    {
        Directory.CreateDirectory(directory);
        Console.SetOut(capture);
    }

    [Fact]
    public async Task ExistingFileIsPreservedAndDownloadGetsANewName()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "video.mp4"), "original");
        await File.WriteAllTextAsync(Path.Combine(directory, "video (1).mp4"), "also original");
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = MediaFixture.Content("new video") });
        await service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(directory, "video.mp4")));
        Assert.Equal("also original", await File.ReadAllTextAsync(Path.Combine(directory, "video (1).mp4")));
        Assert.Equal(MediaFixture.Bytes("new video"), await File.ReadAllBytesAsync(Path.Combine(directory, "video (2).mp4")));
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
    }

    [Fact]
    public async Task SuccessfulDownloadDoesNotReportAFolderLaunchError()
    {
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = MediaFixture.Content() });
        await service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory);
        Assert.Equal(MediaFixture.Bytes(), await File.ReadAllBytesAsync(Path.Combine(directory, "video.mp4")));
        Assert.DoesNotContain("error", capture.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpFailurePropagatesAndDoesNotCreateAFile()
    {
        using var service = CreateService(() => new(HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task InterruptedTransferLeavesNoFinalOrPartialFile()
    {
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream()) });
        await Assert.ThrowsAsync<IOException>(() => service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.DoesNotContain("Download successful", capture.ToString());
    }

    [Fact]
    public async Task ShortResponseIsNotReportedAsACompleteDownload()
    {
        using var service = CreateService(() =>
        {
            var content = MediaFixture.Content();
            content.Headers.ContentLength = 200;
            return new(HttpStatusCode.OK) { Content = content };
        });
        await Assert.ThrowsAsync<IOException>(() => service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.DoesNotContain("Download successful", capture.ToString());
    }

    [Theory]
    [InlineData("a/b\\c:d?e", "a_b_c_d_e.mp4")]
    [InlineData("...", "video.mp4")]
    [InlineData("CON", "_CON.mp4")]
    [InlineData("CON.txt", "_CON.txt.mp4")]
    [InlineData("  ", "video.mp4")]
    [InlineData("hello. ", "hello.mp4")]
    public void FileNamesArePortableAndNonempty(string title, string expected)
    {
        Assert.Equal(expected, Utils.GetSafeFileName(title));
    }

    [Fact]
    public async Task LongUnicodeTitleProducesAUsableFilename()
    {
        var fileName = Utils.GetSafeFileName(string.Concat(Enumerable.Repeat("🎥", 100)));
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = MediaFixture.Content() });
        await service.DownloadFile("https://cdn.example/video.mp4", fileName, directory);
        Assert.Equal(MediaFixture.Bytes(), await File.ReadAllBytesAsync(Path.Combine(directory, fileName)));
        Assert.DoesNotContain('\uFFFD', fileName);
    }

    [Fact]
    public async Task CancellationDuringTransferRemovesThePartialFile()
    {
        using var stream = new WaitingStream();
        using var cancellation = new CancellationTokenSource();
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var download = service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory, cancellation.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Directory.GetFiles(directory, "*.part"));
        Assert.False(File.Exists(Path.Combine(directory, "video.mp4")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.DoesNotContain("Download successful", capture.ToString());
    }

    [Fact]
    public async Task StalledTransferTimesOutAndRemovesThePartialFile()
    {
        using var stream = new WaitingStream();
        using var client = new HttpClient(new ResponseHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }))
        {
            Timeout = TimeSpan.FromMilliseconds(200)
        };
        using var service = new VideoDownloaderService(client);
        await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task ConcurrentDownloadsCannotOverwriteEachOther()
    {
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = MediaFixture.Content() });
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory)));
        Assert.Equal(3, Directory.GetFiles(directory, "*.mp4").Length);
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
        foreach (var file in Directory.GetFiles(directory))
            Assert.Equal(MediaFixture.Bytes(), await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task UnknownContentLengthStillAllowsACompleteDownload()
    {
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableStream(MediaFixture.Bytes())) });
        await service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory);
        Assert.Equal(MediaFixture.Bytes(), await File.ReadAllBytesAsync(Path.Combine(directory, "video.mp4")));
    }

    [Fact]
    public async Task EmptyResponseIsNotReportedAsACompleteVideo()
    {
        using var service = CreateService(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        await Assert.ThrowsAsync<IOException>(() => service.DownloadFile("https://cdn.example/video.mp4", "video.mp4", directory));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task FilenameCannotEscapeTheOutputDirectory()
    {
        using var service = CreateService(() => throw new InvalidOperationException("Unexpected network request."));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DownloadFile("https://cdn.example/video.mp4", "../outside.mp4", directory));
        Assert.Empty(Directory.GetFiles(directory));
    }

    private VideoDownloaderService CreateService(Func<HttpResponseMessage> respond)
    {
        var client = new HttpClient(new ResponseHandler(respond));
        clients.Add(client);
        return new VideoDownloaderService(client);
    }

    public void Dispose()
    {
        Console.SetOut(output);
        capture.Dispose();
        foreach (var client in clients)
            client.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }

    private sealed class InterruptedStream : MemoryStream
    {
        private bool sent;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (sent)
                throw new IOException("Connection lost during the transfer.");
            sent = true;
            buffer[offset] = 42;
            return Task.FromResult(1);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent)
                throw new IOException("Connection lost during the transfer.");
            sent = true;
            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class WaitingStream : MemoryStream
    {
        private bool sent;
        public override bool CanSeek => false;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!sent)
            {
                sent = true;
                buffer.Span[0] = 42;
                return 1;
            }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
