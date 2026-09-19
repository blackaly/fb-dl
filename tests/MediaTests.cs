using System.Net;
using VideoDownloaderConsole;
using Xunit;

namespace FbDl.Tests;

public sealed class MediaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fb-media-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("video/mp4")]
    [InlineData("application/octet-stream")]
    public async Task ErrorPagesAreNeverSavedAsVideos(string contentType)
    {
        var content = new ByteArrayContent("<html>Please log in</html>"u8.ToArray());
        content.Headers.ContentType = new(contentType);
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(content));
        Assert.Empty(Directory.Exists(directory) ? Directory.GetFiles(directory) : []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShortReadsAndUnknownLengthPreserveAllBytes(bool split)
    {
        var bytes = MediaFixture.Bytes();
        await Download(new StreamContent(new FragmentStream(bytes, split ? 3 : bytes.Length)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, "video.mp4")));
    }

    [Fact]
    public async Task TruncatedHeaderIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(new ByteArrayContent(MediaFixture.Bytes()[..18])));
    }

    private async Task Download(HttpContent content)
    {
        using var client = new HttpClient(new Handler(content));
        using var service = new VideoDownloaderService(client);
        await service.DownloadFile("https://cdn.example/video", "video.mp4", directory, maxRetries: 0);
    }

    private sealed class Handler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
    private sealed class FragmentStream(byte[] bytes, int maximum) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(maximum, buffer.Length)], ct);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
