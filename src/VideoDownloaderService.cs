using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloaderConsole;

public class VideoDownloaderService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:89.0) Gecko/20100101 Firefox/89.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.1 Safari/605.1.15",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.1 Mobile/15E148 Safari/604.1"
    ];

    public VideoDownloaderService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public Task ProcessVideoUrl(string url, string? outputDirectory = null,
        CancellationToken cancellationToken = default, int maxRetries = HttpRetry.DefaultRetries) =>
        ProcessVideoUrl(url, new DownloadOptions { OutputDirectory = outputDirectory, MaxRetries = maxRetries }, cancellationToken);

    public async Task ProcessVideoUrl(string url, DownloadOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        url = Utils.NormalizeUrlInput(url);
        if (!IsFacebookUrl(url))
            throw new ArgumentException("Please provide an HTTP or HTTPS URL on facebook.com, fb.com, or fb.watch.", nameof(url));
        Console.WriteLine($"Processing URL: {url}");
        Console.WriteLine("Retrieving Facebook video information...");
        var result = await FetchFacebookVideo(url, options.MaxRetries, cancellationToken);
        await DisplayFacebookVideoInfo(result, url, options, cancellationToken);
    }

    private static bool IsFacebookUrl(string url)
    {
        if (url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        var host = uri.IdnHost.TrimEnd('.');
        return new[] { "facebook.com", "fb.com", "fb.watch" }.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<FacebookVideoResult> FetchFacebookVideo(string url, int maxRetries, CancellationToken cancellationToken)
    {
        using var response = await HttpRetry.SendAsync(_httpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgents[Random.Shared.Next(UserAgents.Length)]);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.facebook.com/");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
            return request;
        }, HttpCompletionOption.ResponseContentRead, maxRetries, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return FacebookVideoExtractor.Extract(html, url, response.RequestMessage?.RequestUri?.AbsoluteUri);
    }

    private async Task DisplayFacebookVideoInfo(FacebookVideoResult result, string sourceUrl,
        DownloadOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine($"\nPage: {result.PageName}");
        Console.WriteLine($"Title: {result.Title}");
        if (options.Quality is not null)
        {
            await DownloadSelected(FacebookVideoExtractor.SelectQuality(result, options.Quality));
            return;
        }
        Console.WriteLine("\nChoose a format to download:");
        for (var i = 0; i < result.Downloads.Count; i++)
            Console.WriteLine($"  {i + 1}. {result.Downloads[i].Quality} quality ({result.Downloads[i].Format})");
        Console.WriteLine($"  {result.Downloads.Count + 1}. Cancel");

        while (true)
        {
            Console.Write("\nEnter your choice: ");
            var input = await Utils.ReadLineAsync(cancellationToken);
            if (input == null)
                throw new InvalidOperationException("Input closed before a quality was selected. Run in an interactive terminal.");
            if (!int.TryParse(input, out var choice) || choice < 1 || choice > result.Downloads.Count + 1)
            {
                Console.Error.WriteLine("Invalid selection. Enter one of the numbers shown above.");
                continue;
            }
            if (choice == result.Downloads.Count + 1)
            {
                Console.WriteLine("Download cancelled.");
                return;
            }
            await DownloadSelected(result.Downloads[choice - 1]);
            return;
        }

        Task DownloadSelected(DownloadOption option)
        {
            var fileName = Utils.GetSafeFileName($"{result.Title} by {result.PageName}", option.Format);
            var identity = $"{(result.VideoId.Length > 0 ? "facebook:" + result.VideoId : sourceUrl)}|{option.Quality}|{option.Height}|{option.Format}";
            return new VideoTransferService(_httpClient).DownloadAsync(option.Url, fileName, identity, options, cancellationToken);
        }
    }

    public async Task DownloadFile(string url, string fileName, string? outputDirectory = null,
        CancellationToken cancellationToken = default, int maxRetries = HttpRetry.DefaultRetries) =>
        await new VideoTransferService(_httpClient).DownloadAsync(url, fileName, url,
            new DownloadOptions { OutputDirectory = outputDirectory, MaxRetries = maxRetries }, cancellationToken);

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
