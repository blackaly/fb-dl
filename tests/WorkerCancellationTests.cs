using System.Net;
using System.Net.Http.Headers;
using VideoDownloaderConsole;
using Xunit;

namespace FbDl.Tests;

public class WorkerCancellationTests
{
    [Fact]
    public async Task FailedWorkerStopsAndDisposesReadingPeerBeforeReturningAndCleaningUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fb-peer-" + Guid.NewGuid().ToString("N"));
        using var stream = new BlockedStream();
        using var client = new HttpClient(new Handler(stream)) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var pending = new VideoTransferService(client).DownloadAsync("https://cdn.example/video", "video.mp4", "video",
                new DownloadOptions { OutputDirectory = directory, Connections = 2, MaxRetries = 0 });
            await Assert.ThrowsAsync<HttpRequestException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(stream.Stopped);
            Assert.True(stream.Disposed);
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Equal(2, stream.ReadCalls); // one write completed; blocked read cancelled, no later reads
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Handler(BlockedStream stream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var range = Assert.Single(request.Headers.Range!.Ranges);
            if (range.From == 0 && range.To > 65535)
            {
                await stream.Waiting.Task.WaitAsync(token);
                return new(HttpStatusCode.Forbidden);
            }
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = range.To == 65535 ? new ByteArrayContent(MediaFixture.Bytes(length: 65536)) : new StreamContent(stream)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"video\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From!.Value, range.To!.Value, 16 * 1024 * 1024);
            response.Content.Headers.ContentLength = range.To - range.From + 1;
            return response;
        }
    }

    private sealed class BlockedStream : MemoryStream
    {
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Stopped;
        internal bool Disposed;
        internal int ReadCalls;
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (++ReadCalls == 1) { buffer.Span[..128].Clear(); return 128; }
            Waiting.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return 0; }
            finally { Stopped = true; }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
