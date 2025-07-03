using System.Text.Json;
using System.Text.RegularExpressions;


namespace VideoDownloaderConsole
{
    public class VideoDownloaderService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, string> _userAgents;
        private readonly Dictionary<string, string> _formatColors;

        public VideoDownloaderService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            _userAgents = new Dictionary<string, string>
            {
                ["chrome"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36",
                ["firefox"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:89.0) Gecko/20100101 Firefox/89.0",
                ["safari"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.1 Safari/605.1.15",
                ["mobile"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.1 Mobile/15E148 Safari/604.1"
            };

            _formatColors = new Dictionary<string, string>
            {
                ["green"] = "17,18,22",
                ["blue"] = "139,140,141,249,250,251,599,600",
                ["default"] = "#9e0cf2"
            };
        }

        public async Task ProcessVideoUrl(string url)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Processing URL: {url}");
            Console.ResetColor();


            if (IsFacebookUrl(url))
            {
                await DownloadFacebookVideo(url);
            }

            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Unsupported platform. Please provide a Facebook URL.");
                Console.ResetColor();
            }
        }

        private bool IsFacebookUrl(string url)
        {
            return url.Contains("facebook.com") || url.Contains("fb.com");
        }


        #region Facebook Download

        private async Task DownloadFacebookVideo(string url)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("Retrieving Facebook video information...");
                Console.ResetColor();

                var result = await FetchFacebookVideoWithRetry(url, 3);
                if (result != null)
                {
                    DisplayFacebookVideoInfo(result);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Facebook download error: {ex.Message}");
                Console.ResetColor();
            }
        }

        private async Task<FacebookVideoResult> FetchFacebookVideoWithRetry(string url, int maxAttempts)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Console.WriteLine($"Attempt {attempt}...");
                    var result = await FetchFacebookVideo(url);
                    if (result.Success)
                        return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Attempt {attempt} failed: {ex.Message}");
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(2000); // Wait 2 seconds before retry
                }
            }

            throw new Exception("Unable to retrieve Facebook video information after multiple attempts.");
        }

        private async Task<FacebookVideoResult> FetchFacebookVideo(string url)
        {
            // Extract video ID
            var videoId = ExtractFacebookVideoId(url);

            // Setup headers
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = GetRandomUserAgent(),
                ["Referer"] = "https://www.facebook.com/",
                ["DNT"] = "1",
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9",
                ["Accept-Language"] = "en-GB,en;q=0.9,tr-TR;q=0.8,tr;q=0.7,en-US;q=0.6",
                ["Cache-Control"] = "max-age=0",
                ["Sec-Fetch-Dest"] = "document",
                ["Sec-Fetch-Mode"] = "navigate",
                ["Sec-Fetch-Site"] = "none",
                ["Sec-Fetch-User"] = "?1",
                ["Upgrade-Insecure-Requests"] = "1"
            };

            _httpClient.DefaultRequestHeaders.Clear();
            foreach (var header in headers)
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Unable to load Facebook page: HTTP {response.StatusCode}");
            }

            var html = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(html))
            {
                throw new Exception("Unable to load page content.");
            }

            // Extract video links and metadata
            var result = new FacebookVideoResult
            {
                Success = true,
                VideoId = videoId,
                SdLink = ExtractVideoLink(html, @"browser_native_sd_url"":""([^""]+)"""),
                HdLink = ExtractVideoLink(html, @"browser_native_hd_url"":""([^""]+)"""),
                Title = ExtractTitle(html),
                Thumbnail = ExtractThumbnail(html)
            };

            if (string.IsNullOrEmpty(result.SdLink) && string.IsNullOrEmpty(result.HdLink))
            {
                throw new Exception("Video link not found. Make sure the video is public.");
            }

            return result;
        }

        private void DisplayFacebookVideoInfo(FacebookVideoResult result)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Title: {result.Title}");
            Console.WriteLine($"Thumbnail: {result.Thumbnail}");
            Console.WriteLine($"Video ID: {result.VideoId}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nDownload link:");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(result.SdLink))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"📱 Low quality (SD): {result.SdLink}&dl=1");
                Console.ResetColor();
            }

            if (!string.IsNullOrEmpty(result.HdLink))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"🎬 High quality (HD): {result.HdLink}&dl=1");
                Console.ResetColor();
            }
        }

        private string GetRandomUserAgent()
        {
            var agents = _userAgents.Values.ToArray();
            var random = new Random();
            return agents[random.Next(agents.Length)];
        }

        private string ExtractFacebookVideoId(string url)
        {
            var match = Regex.Match(url, @"(\d+)/?$");
            if (match.Success)
                return match.Groups[1].Value;

            return Regex.Replace(url, @"[^0-9]", "");
        }

        private string ExtractVideoLink(string html, string pattern)
        {
            var match = Regex.Match(html, pattern);
            if (match.Success)
            {
                try
                {
                    var jsonString = $"{{\"text\": \"{match.Groups[1].Value}\"}}";
                    var jsonDoc = JsonDocument.Parse(jsonString);
                    return jsonDoc.RootElement.GetProperty("text").GetString();
                }
                catch
                {
                    return match.Groups[1].Value;
                }
            }
            return null;
        }

        private string ExtractTitle(string html)
        {
            var patterns = new[]
            {
                @"<title>(.*?)</title>",
                @"title id=""pageTitle"">(.+?)</title>"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    try
                    {
                        var jsonString = $"{{\"text\": \"{match.Groups[1].Value}\"}}";
                        var jsonDoc = JsonDocument.Parse(jsonString);
                        return jsonDoc.RootElement.GetProperty("text").GetString();
                    }
                    catch
                    {
                        return match.Groups[1].Value;
                    }
                }
            }

            return "Video Facebook";
        }

        private string ExtractThumbnail(string html)
        {
            var match = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "https://via.placeholder.com/200x300";
        }

        #endregion


        public async Task DownloadFile(string url, string fileName, string outputDirectory = "Downloads")
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Downloading: {fileName}");
                Console.ResetColor();

                // Create directory if it doesn't exist
                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var filePath = Path.Combine(outputDirectory, fileName);

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var downloadedBytes = 0L;

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var isMoreToRead = true;

                do
                {
                    var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        isMoreToRead = false;
                    }
                    else
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        downloadedBytes += read;

                        if (totalBytes > 0)
                        {
                            var progressPercentage = (double)downloadedBytes / totalBytes * 100;
                            Console.Write($"\r📊 Progress: {progressPercentage:F1}% ({downloadedBytes:N0}/{totalBytes:N0} bytes)");
                        }
                    }
                }
                while (isMoreToRead);

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Download successful: {filePath}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Download error: {ex.Message}");
                Console.ResetColor();
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}