using System.Net;
using VideoDownloaderConsole;
using Xunit;

namespace FbDl.Tests;

public class FlowTests : IDisposable
{
    private const string PageUrl = "https://www.facebook.com/watch/?v=123";
    private const string Html = """
        <meta property="og:title" content="A video">
        <meta name="author" content="Demo">
        <script>{"browser_native_sd_url":"https:\/\/cdn.example\/sd.mp4?x=1\u0026y=2","browser_native_hd_url":"https:\/\/cdn.example\/hd.mp4"}</script>
        """;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fb-dl-flow-" + Guid.NewGuid().ToString("N"));
    private readonly TextReader originalInput = Console.In;
    private readonly TextWriter originalOutput = Console.Out;
    private readonly TextWriter originalError = Console.Error;
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public FlowTests()
    {
        Directory.CreateDirectory(directory);
        Console.SetOut(output);
        Console.SetError(error);
    }

    [Theory]
    [InlineData("1\n", "sd video", "--output")]
    [InlineData("2\n", "hd video", "-o")]
    [InlineData("invalid\n2\n", "hd video", "--output")]
    public async Task UrlAndOutputOptionDownloadTheSelectedQuality(string input, string expected, string option)
    {
        Console.SetIn(new StringReader(input));
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(VideoResponse(request))));
        using var service = new VideoDownloaderService(client);
        var target = Path.Combine(directory, "new folder");
        Assert.Equal(0, await Program.RunAsync([PageUrl, option, target], service));
        Assert.Equal(MediaFixture.Bytes(expected), await File.ReadAllBytesAsync(Path.Combine(target, "A video by Demo.mp4")));
        Assert.Single(Directory.GetFiles(target));
    }

    [Fact]
    public async Task InteractiveModeAlsoUsesTheOutputOption()
    {
        Console.SetIn(new StringReader(PageUrl + "\n1\nexit\n"));
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(VideoResponse(request))));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(0, await Program.RunAsync(["--output", directory], service));
        Assert.Equal(MediaFixture.Bytes("sd video"), await File.ReadAllBytesAsync(Path.Combine(directory, "A video by Demo.mp4")));
    }

    [Theory]
    [InlineData("[https://www.facebook.com/watch/?v=123](https://www.facebook.com/watch/?v=123)")]
    [InlineData("  [My video](https://www.facebook.com/watch/?v=123)  ")]
    [InlineData("<https://www.facebook.com/watch/?v=123>")]
    [InlineData("[My video](<https://www.facebook.com/watch/?v=123>)")]
    public async Task PastedLinksDownloadTheActualDestination(string input)
    {
        Console.SetIn(new StringReader("1\n"));
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(VideoResponse(request))));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(0, await Program.RunAsync([input, "-o", directory], service));
        Assert.Equal(MediaFixture.Bytes("sd video"), await File.ReadAllBytesAsync(Path.Combine(directory, "A video by Demo.mp4")));
    }

    [Theory]
    [InlineData("[https://facebook.com/watch/?v=123](https://example.com/video)")]
    [InlineData("[My video](https://facebook.com.evil.example/video)")]
    [InlineData("[My video](javascript:alert(1))")]
    [InlineData("[My video](https://facebook.com/watch/?v=123) extra text")]
    public async Task PastedLinkDestinationsStillRequireAFacebookUrl(string input)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network request.")));
        using var service = new VideoDownloaderService(client);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ProcessVideoUrl(input, directory));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task RetryOptionLimitsPageRequests(int retries, int expectedRequests)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(1, await Program.RunAsync([PageUrl, "-o", directory, "--retries", retries.ToString()], service));
        Assert.Equal(expectedRequests, requests);
        Assert.Contains("503", error.ToString());
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 2)]
    public async Task RetryOptionAlsoControlsTheVideoRequest(int retries, int expectedExit, int expectedRequests)
    {
        Console.SetIn(new StringReader("1\n"));
        var videoRequests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "cdn.example" && ++videoRequests == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            }
            return Task.FromResult(VideoResponse(request));
        }));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(expectedExit, await Program.RunAsync([PageUrl, "-o", directory, "--retries", retries.ToString()], service));
        Assert.Equal(expectedRequests, videoRequests);
        if (expectedExit == 0)
            Assert.Equal(MediaFixture.Bytes("sd video"), await File.ReadAllBytesAsync(Path.Combine(directory, "A video by Demo.mp4")));
        else
            Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryRequestFailureRetriesThePage(bool timeout)
    {
        Console.SetIn(new StringReader("1\n"));
        var failedOnce = false;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (!failedOnce)
            {
                failedOnce = true;
                if (timeout)
                    throw new TaskCanceledException("Request timed out.");
                throw new HttpRequestException("Connection reset.");
            }
            return Task.FromResult(VideoResponse(request));
        }));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(0, await Program.RunAsync([PageUrl, "-o", directory], service));
        Assert.Equal(MediaFixture.Bytes("sd video"), await File.ReadAllBytesAsync(Path.Combine(directory, "A video by Demo.mp4")));
    }

    [Fact]
    public async Task BadRequestFailsWithoutRetrying()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(1, await Program.RunAsync([PageUrl, "-o", directory], service));
        Assert.Equal(1, requests);
        Assert.Contains("400", error.ToString());
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task LongRetryAfterIsReportedEvenWhenRetriesAreExhausted(int retries, int earlierFailures)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                requests <= earlierFailures ? TimeSpan.Zero : TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        }));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(1, await Program.RunAsync([PageUrl, "-o", directory, "--retries", retries.ToString()], service));
        Assert.Equal(earlierFailures + 1, requests);
        Assert.Contains("60", error.ToString());
    }

    [Fact]
    public async Task CancellationDuringRetryDelayStopsFurtherRequests()
    {
        using var retryOutput = new RetrySignalWriter();
        Console.SetError(retryOutput);
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        }));
        using var service = new VideoDownloaderService(client);
        var run = Program.RunAsync([PageUrl, "-o", directory], service, cancellation.Token);
        await retryOutput.Retrying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(130, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("https://facebook.com.evil.example/video")]
    [InlineData("https://notfacebook.com/video")]
    [InlineData("https://example.com/facebook.com")]
    [InlineData("https://facebook.com@example.com/video")]
    [InlineData("https://example.com/?url=fb.com")]
    [InlineData("ftp://facebook.com/video")]
    [InlineData("file:///facebook.com/video")]
    [InlineData("facebook.com/video")]
    public async Task InvalidFacebookUrlsAreRejectedBeforeFetching(string url)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network request.")));
        using var service = new VideoDownloaderService(client);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ProcessVideoUrl(url, directory));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("https://FACEBOOK.COM/watch/?v=123")]
    [InlineData("https://m.facebook.com/reel/123")]
    [InlineData("https://fb.com/video")]
    [InlineData("https://fb.watch/abc")]
    public async Task FacebookHostsReachTheQualityMenu(string url)
    {
        Console.SetIn(new StringReader("3\n"));
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Html)
        })));
        using var service = new VideoDownloaderService(client);
        await service.ProcessVideoUrl(url, directory);
        Assert.Contains("SD", output.ToString());
        Assert.Contains("HD", output.ToString());
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("3\n", 0)]
    public async Task ClosedQualityInputOrCancelNeverCreatesAFile(string input, int expectedCode)
    {
        Console.SetIn(new StringReader(input));
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(VideoResponse(request))));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(expectedCode, await Program.RunAsync([PageUrl, "-o", directory], service));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.DoesNotContain("Download successful", output.ToString());
    }

    [Fact]
    public async Task FailedPageFetchReportsTheHttpStatus()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(1, await Program.RunAsync([PageUrl, "-o", directory], service));
        Assert.Contains("403", error.ToString());
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task MissingVideoReportsAPublicVideoOrPageFormatError()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Login required</html>")
        })));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(1, await Program.RunAsync([PageUrl, "-o", directory], service));
        Assert.Contains("No downloadable video", error.ToString());
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CancellationDuringPageFetchReturns130()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }));
        using var service = new VideoDownloaderService(client);
        var run = Program.RunAsync([PageUrl, "-o", directory], service, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(130, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileWaitingForInputReturns130(bool atQualityMenu)
    {
        using var reader = new BlockingReader();
        Console.SetIn(reader);
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(VideoResponse(request))));
        using var service = new VideoDownloaderService(client);
        var run = Program.RunAsync(atQualityMenu ? [PageUrl, "-o", directory] : [], service, cancellation.Token);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(130, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task TransientPageFailureRetriesAndThenDownloads()
    {
        Console.SetIn(new StringReader("1\n"));
        var failedOnce = false;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (!failedOnce)
            {
                failedOnce = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            return Task.FromResult(VideoResponse(request));
        }));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(0, await Program.RunAsync([PageUrl, "-o", directory], service));
        Assert.Equal(MediaFixture.Bytes("sd video"), await File.ReadAllBytesAsync(Path.Combine(directory, "A video by Demo.mp4")));
    }

    [Theory]
    [InlineData("best", "hd video")]
    [InlineData("hd", "hd video")]
    [InlineData("sd", "sd video")]
    public async Task AutomaticQualityDoesNotPrompt(string quality, string expected)
    {
        Console.SetIn(new StringReader(""));
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(VideoResponse(request))));
        using var service = new VideoDownloaderService(client);
        Assert.Equal(0, await Program.RunAsync([PageUrl, "--output=" + directory, "--quality=" + quality, "--connections=1", "--retries=0"], service));
        Assert.Equal(MediaFixture.Bytes(expected), await File.ReadAllBytesAsync(Path.Combine(directory, "A video by Demo.mp4")));
        Assert.DoesNotContain("Enter your choice", output.ToString());
    }

    private static HttpResponseMessage VideoResponse(HttpRequestMessage request) => new(HttpStatusCode.OK)
    {
        Content = request.RequestUri!.AbsoluteUri switch
        {
            PageUrl => new StringContent(Html),
            "https://cdn.example/sd.mp4?x=1&y=2" => MediaFixture.Content("sd video"),
            "https://cdn.example/hd.mp4" => MediaFixture.Content("hd video"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }
    };

    public void Dispose()
    {
        Console.SetIn(originalInput);
        Console.SetOut(originalOutput);
        Console.SetError(originalError);
        output.Dispose();
        error.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    private sealed class BlockingReader : TextReader
    {
        private readonly ManualResetEventSlim released = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override string? ReadLine()
        {
            Started.SetResult();
            released.Wait();
            return null;
        }
        protected override void Dispose(bool disposing) => released.Set();
    }

    private sealed class RetrySignalWriter : StringWriter
    {
        public TaskCompletionSource Retrying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("Retrying", StringComparison.OrdinalIgnoreCase) == true)
                Retrying.TrySetResult();
        }
    }
}
