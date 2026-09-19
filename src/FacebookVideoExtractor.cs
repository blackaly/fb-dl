using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloaderConsole;

internal static class FacebookVideoExtractor
{
    private sealed record Candidate(string Id, List<DownloadOption> Downloads, string? PageName = null);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 128 };

    internal static FacebookVideoResult Extract(string html, string sourceUrl, string? finalUrl = null)
    {
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("The Facebook page was empty.");
        if (html.Length > 32 * 1024 * 1024)
            throw new InvalidOperationException("The Facebook page is too large to parse safely.");
        var candidates = new List<Candidate>();
        foreach (Match script in Regex.Matches(html, @"<script\b[^>]*>([\s\S]*?)</script\s*>", RegexOptions.IgnoreCase))
        {
            ParseScript(script.Groups[1].Value.Trim(), candidates);
        }

        var requested = VideoId(sourceUrl);
        if (requested.Length == 0 && finalUrl is not null)
            requested = VideoId(finalUrl);
        var grouped = candidates.GroupBy(c => c.Id.Length > 0 ? c.Id :
            string.Join('|', c.Downloads.Select(d => d.Url).Order(StringComparer.Ordinal)))
            .Select(g => new Candidate(g.First().Id, g.SelectMany(c => c.Downloads)
                .DistinctBy(d => (d.Url, d.Quality)).ToList(), g.FirstOrDefault(c => c.PageName is not null)?.PageName)).ToList();
        var selected = requested.Length > 0 ? grouped.FirstOrDefault(c => c.Id == requested) : null;
        selected ??= grouped.Count == 1 && (requested.Length == 0 || grouped[0].Id.Length == 0) ? grouped[0] : null;
        if (selected is null)
        {
            if (grouped.Count > 0)
                throw new InvalidOperationException("Could not identify the requested video unambiguously; the page contains other video candidates.");
            var reason = html.Contains("dash_manifest", StringComparison.OrdinalIgnoreCase) || html.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? "Only playlist media was found; direct progressive MP4 is required."
                : Regex.IsMatch(html, "login required|log in to continue|login_form", RegexOptions.IgnoreCase)
                    ? "This video requires login."
                    : Regex.IsMatch(html, "content isn't available|video is unavailable|content not found", RegexOptions.IgnoreCase)
                        ? "The video is unavailable."
                        : "The video may be private, require login, or use an unsupported page format.";
            throw new InvalidOperationException("No downloadable video found. " + reason);
        }
        return new FacebookVideoResult
        {
            Success = true,
            VideoId = selected.Id.Length > 0 ? selected.Id : requested,
            Title = Meta(html, "og:title") ?? "Facebook Video",
            PageName = Meta(html, "author") ?? selected.PageName ?? "Facebook Page",
            Thumbnail = Meta(html, "og:image") ?? "",
            Downloads = selected.Downloads.OrderBy(d => d.Height ?? (d.Quality == "HD" ? 720 : 360)).ToList()
        };
    }

    internal static DownloadOption SelectQuality(FacebookVideoResult video, string quality) =>
        video.Downloads.Where(d => quality == "best" || d.Quality.Equals(quality, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Height ?? (d.Quality == "HD" ? 720 : 360))
            .FirstOrDefault() ?? throw new InvalidOperationException($"Requested quality {quality} is unavailable.");

    private static string VideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var match = Regex.Match(uri.AbsolutePath, @"/(?:reel|videos)/(\d+)(?:/|$)");
        if (match.Success) return match.Groups[1].Value;
        var id = System.Web.HttpUtility.ParseQueryString(uri.Query)["v"];
        return id is not null && Regex.IsMatch(id, @"\A\d+\z") ? id : "";
    }

    private static string? Meta(string html, string property)
    {
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attributes = Regex.Matches(tag.Value, "([\\w:-]+)\\s*=\\s*([\"'])(.*?)\\2", RegexOptions.Singleline)
                .Cast<Match>().GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Groups[3].Value, StringComparer.OrdinalIgnoreCase);
            if ((attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name"))?.Equals(property, StringComparison.OrdinalIgnoreCase) == true &&
                attributes.TryGetValue("content", out var value))
                return WebUtility.HtmlDecode(value).Trim();
        }
        return null;
    }

    private static void ParseScript(string text, List<Candidate> candidates, int nesting = 0)
    {
        if (Parse(text, "", candidates, 0) || nesting >= 8) return;
        foreach (var fragment in JsonObjects(text))
        {
            // Function bodies are not JSON. Search their contents only when the container failed,
            // so a valid video's nested objects never lose their surrounding video identity.
            if (!Parse(fragment, "", candidates, 0))
                ParseScript(fragment[1..^1], candidates, nesting + 1);
        }
    }

    private static bool Parse(string json, string id, List<Candidate> candidates, int decoding, string? pageName = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            Walk(document.RootElement, id, candidates, decoding, pageName);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void Walk(JsonElement element, string id, List<Candidate> candidates, int decoding, string? pageName = null)
    {
        if (element.ValueKind == JsonValueKind.String && decoding < 2)
        {
            var value = element.GetString()!.Trim();
            if (value.StartsWith('{') || value.StartsWith('[')) Parse(value, id, candidates, decoding + 1, pageName);
        }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Walk(child, id, candidates, decoding, pageName);
        if (element.ValueKind != JsonValueKind.Object) return;
        id = Text(element, "video_id") ?? Text(element, "id") ?? id;
        pageName = Text(element, "page_name") ?? pageName;
        var downloads = new List<DownloadOption>();
        Add(downloads, Text(element, "browser_native_sd_url") ?? Text(element, "playable_url"), "SD");
        Add(downloads, Text(element, "browser_native_hd_url") ?? Text(element, "playable_url_quality_hd"), "HD");
        if (element.TryGetProperty("progressive_urls", out var progressive) && progressive.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in progressive.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var metadata = entry.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object ? meta : entry;
                int? height = int.TryParse(Text(metadata, "height"), out var h) && h > 0 ? h : null;
                var quality = Text(metadata, "quality")?.ToUpperInvariant();
                if (quality is not ("HD" or "SD")) quality = height >= 720 ? "HD" : "SD";
                Add(downloads, Text(entry, "progressive_url"), quality, height);
            }
        }
        if (downloads.Count > 0) candidates.Add(new(id, downloads, pageName));
        foreach (var property in element.EnumerateObject()) Walk(property.Value, id, candidates, decoding, pageName);
    }

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;

    private static void Add(List<DownloadOption> downloads, string? url, string quality, int? height = null)
    {
        if (url is null || !Utils.IsValidUrl(url)) return;
        downloads.Add(new() { Url = WebUtility.HtmlDecode(url), Quality = quality, Format = "mp4", Height = height });
    }

    // Scan balanced objects once, ignoring braces inside JSON strings. Never execute script text.
    private static IEnumerable<string> JsonObjects(string text)
    {
        var start = -1;
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (start < 0)
            {
                if (c == '{') { start = i; depth = 1; }
                continue;
            }
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                yield return text[start..(i + 1)];
                start = -1;
            }
        }
    }
}
