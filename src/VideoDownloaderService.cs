using System;
using System.Collections.Generic;
using System.Diagnostics; // Required for opening the folder
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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
                    await DisplayFacebookVideoInfo(result);
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
            var videoId = ExtractFacebookVideoId(url);
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = GetRandomUserAgent(),
                ["Referer"] = "https://www.facebook.com/",
                ["DNT"] = "1",
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9",
                ["Accept-Language"] = "en-GB,en;q=0.9,tr-TR;q=0.8,tr;q=0.7,en-US;q=0.6",
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

            // *** FIXED: The title is now cleaned before being used ***
            var rawTitle = ExtractMetaProperty(html, "og:title") ?? "Facebook Video";

            var result = new FacebookVideoResult
            {
                Success = true,
                VideoId = videoId,
                Title = CleanVideoTitle(rawTitle), // Clean the title
                PageName = ExtractPageName(html) ?? "Facebook Page",
                Thumbnail = ExtractMetaProperty(html, "og:image") ?? "https://via.placeholder.com/200x300"
            };

            var sdLink = ExtractVideoLink(html, @"browser_native_sd_url"":""([^""]+)""");
            if (!string.IsNullOrEmpty(sdLink))
            {
                result.Downloads.Add(new DownloadOption { Quality = "SD", Format = "mp4", Url = sdLink });
            }

            var hdLink = ExtractVideoLink(html, @"browser_native_hd_url"":""([^""]+)""");
            if (!string.IsNullOrEmpty(hdLink))
            {
                result.Downloads.Add(new DownloadOption { Quality = "HD", Format = "mp4", Url = hdLink });
            }

            if (result.Downloads.Count == 0)
            {
                throw new Exception("Video link not found. Make sure the video is public.");
            }

            return result;
        }

        private async Task DisplayFacebookVideoInfo(FacebookVideoResult result)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"\nPage: {result.PageName}");
            Console.WriteLine($"Title: {result.Title}");
            Console.ResetColor();

            if (result.Downloads.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("No downloadable video links were found.");
                Console.ResetColor();
                return;
            }

            // --- Choose Quality ---
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nChoose a format to download:");
            Console.ResetColor();

            for (int i = 0; i < result.Downloads.Count; i++)
            {
                var option = result.Downloads[i];
                Console.WriteLine($"  {i + 1}. {option.Quality} quality ({option.Format})");
            }
            Console.WriteLine($"  {result.Downloads.Count + 1}. Cancel");

            Console.Write("\nEnter your choice: ");
            string qualityChoiceStr = Console.ReadLine();

            if (!int.TryParse(qualityChoiceStr, out int qualityChoice) || qualityChoice <= 0 || qualityChoice > result.Downloads.Count)
            {
                Console.WriteLine("Invalid selection or cancel.");
                return;
            }
            var selectedOption = result.Downloads[qualityChoice - 1];

            // --- Automatically format the filename ---
            string baseFileName = $"{result.Title} by {result.PageName}";
            string safeFileName = Utils.GetSafeFileName(baseFileName, "mp4");

            await DownloadFile(selectedOption.Url, safeFileName);
        }
        
        // *** NEW METHOD: Cleans the messy title from Facebook ***
        private string CleanVideoTitle(string rawTitle)
        {
            if (string.IsNullOrEmpty(rawTitle))
            {
                return "Facebook Video";
            }

            // Split the title by the underscore, which often separates metadata from the real title
            var parts = rawTitle.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);

            // If the split gives us multiple parts, we assume the second part is the most likely candidate for the real title.
            // This handles cases like: "1M Views _ The Real Title _ Some other text"
            if (parts.Length > 1)
            {
                return parts[1].Trim();
            }

            // If there's no underscore, it might be a cleaner title already, so return it as is.
            return rawTitle.Trim();
        }

        private string GetRandomUserAgent()
        {
            var agents = _userAgents.Values.ToArray();
            var random = new Random();
            return agents[random.Next(agents.Length)];
        }

        private string ExtractFacebookVideoId(string url)
        {
            var match = Regex.Match(url, @"/videos/(\d+)/?$");
            if (match.Success)
                return match.Groups[1].Value;

            match = Regex.Match(url, @"/watch/\?v=(\d+)");
            if (match.Success)
                return match.Groups[1].Value;

            return Regex.Replace(url, @"[^0-9]", "");
        }

        private string ExtractVideoLink(string html, string pattern)
        {
            var match = Regex.Match(html, pattern);
            if (match.Success)
            {
                return JsonSerializer.Deserialize<string>($"\"{match.Groups[1].Value}\"");
            }
            return null;
        }

        private string ExtractMetaProperty(string html, string propertyName)
        {
            var pattern = $@"<meta\s+(?:name|property)=""{Regex.Escape(propertyName)}""\s+content=""([^""]+)""";
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            }
            return null;
        }

        private string ExtractPageName(string html)
        {
            string pageName = ExtractMetaProperty(html, "author");
            if (!string.IsNullOrEmpty(pageName))
            {
                return pageName;
            }

            var match = Regex.Match(html, @"""page_name"":""([^""]+)""");
            if (match.Success)
            {
                return Regex.Unescape(match.Groups[1].Value);
            }

            return null;
        }

        #endregion

        public async Task DownloadFile(string url, string fileName, string outputDirectory = null)
        {
            if (string.IsNullOrEmpty(outputDirectory))
            {
                outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }

            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\nDownloading to: {Path.Combine(outputDirectory, fileName)}");
                Console.ResetColor();

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
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        var progressPercentage = (double)downloadedBytes / totalBytes * 100;
                        Console.Write($"\r📊 Progress: {progressPercentage:F1}% ({Utils.FormatFileSize(downloadedBytes)} / {Utils.FormatFileSize(totalBytes)})");
                    }
                    else
                    {
                        Console.Write($"\r📊 Progress: {Utils.FormatFileSize(downloadedBytes)} downloaded");
                    }
                }

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✅ Download successful: {filePath}");
                Console.ResetColor();

                Process.Start("explorer.exe", outputDirectory);
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