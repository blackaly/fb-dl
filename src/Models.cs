using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace VideoDownloaderConsole
{

    public class VideoData
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("size")]
        public string Size { get; set; }

        [JsonPropertyName("thumbnail")]
        public string Thumbnail { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; }

        [JsonPropertyName("downloads")]
        public List<DownloadOption> Downloads { get; set; } = new List<DownloadOption>();
    }

    public class DownloadOption
    {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("quality")]
        public string Quality { get; set; }

        [JsonPropertyName("format")]
        public string Format { get; set; }
    }

    public class FacebookVideoResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string VideoId { get; set; }
        public string Title { get; set; } = "Video Facebook";
        public string PageName { get; set; } = "Facebook Page";
        public string Thumbnail { get; set; } = "https://via.placeholder.com/200x300";
        public List<DownloadOption> Downloads { get; set; } = new List<DownloadOption>();
    }

    public class ErrorInfo
    {
        [JsonPropertyName("message")]
        public string Message { get; set; }
    }
}