using VideoDownloaderConsole;
using Xunit;

namespace FbDl.Tests;

public class ExtractionTests
{
    [Fact]
    public void ReadsEmbeddedJsonInsideFunctionWrappers()
    {
        var html = """<script>requireLazy(["ServerJS"],function(m){m.handle({"id":"123","browser_native_sd_url":"https://cdn.example/a.mp4"});});</script>""";
        var result = FacebookVideoExtractor.Extract(html, "https://facebook.com/reel/123");
        Assert.Equal("https://cdn.example/a.mp4", Assert.Single(result.Downloads).Url);
    }

    [Fact]
    public void KeepsLegacyCreatorMetadataWithTheSelectedVideo()
    {
        var result = Extract("""{"videos":[{"id":"123","page_name":"Actual creator","playable_url":"https://cdn.example/a.mp4"},{"id":"456","page_name":"Other creator","playable_url":"https://cdn.example/b.mp4"}]}""");
        Assert.Equal("Actual creator", result.PageName);
    }

    [Fact]
    public void KeepsRequestedVideoSeparateFromRecommendations()
    {
        var result = Extract("""{"videos":[{"id":"123","playable_url":"https://cdn.example/a.mp4"},{"id":"456","playable_url_quality_hd":"https://cdn.example/b.mp4"}]}""");
        Assert.Equal("123", result.VideoId);
        Assert.Equal("https://cdn.example/a.mp4", Assert.Single(result.Downloads).Url);
        Assert.Throws<InvalidOperationException>(() => FacebookVideoExtractor.SelectQuality(result, "hd"));
    }

    [Fact]
    public void ReadsNestedProgressiveVariantsAndMetadata()
    {
        var result = FacebookVideoExtractor.Extract("""
            <meta content='My_title &amp; day' property='og:title'>
            <meta content="Creator" name="author">
            <script>{"video":{"id":"123","videoDeliveryResponseFragment":{"videoDeliveryResponseResult":{"progressive_urls":[
              {"progressive_url":"https:\/\/cdn.example\/sd.mp4?a=1\u0026b=2","metadata":{"quality":"SD","height":360}},
              {"progressive_url":"https://cdn.example/hd.mp4","metadata":{"quality":"HD","height":1080}}
            ]}}}}</script>
            """, "https://facebook.com/watch/?other=9&v=123");
        Assert.Equal("My_title & day", result.Title);
        Assert.Equal("Creator", result.PageName);
        Assert.Equal(2, result.Downloads.Count);
        Assert.Equal("https://cdn.example/hd.mp4", FacebookVideoExtractor.SelectQuality(result, "best").Url);
        Assert.Contains("a=1&b=2", FacebookVideoExtractor.SelectQuality(result, "sd").Url);
    }

    [Fact]
    public void ReadsEscapedJsonAndDeduplicatesVariants()
    {
        var payload = """{"id":"123","browser_native_sd_url":"https://cdn.example/a.mp4","playable_url":"https://cdn.example/a.mp4"}""";
        var result = Extract(System.Text.Json.JsonSerializer.Serialize(payload));
        Assert.Single(result.Downloads);
    }

    [Fact]
    public void AmbiguousOrWrongVideoIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Extract("""{"videos":[{"id":"456","playable_url":"https://cdn.example/a.mp4"},{"id":"789","playable_url":"https://cdn.example/b.mp4"}]}"""));
        Assert.Throws<InvalidOperationException>(() => Extract("""{"id":"456","playable_url":"https://cdn.example/a.mp4"}"""));
    }

    [Fact]
    public void FinalRedirectProvidesVideoIdentity()
    {
        var result = FacebookVideoExtractor.Extract("""<script>{"id":"456","playable_url":"https://cdn.example/a.mp4"}</script>""",
            "https://fb.watch/abc", "https://www.facebook.com/reel/456");
        Assert.Equal("456", result.VideoId);
    }

    [Theory]
    [InlineData("<html>Login required</html>", "login")]
    [InlineData("<html>This content isn't available</html>", "unavailable")]
    [InlineData("<script>{\"dash_manifest\":\"x\"}</script>", "progressive")]
    [InlineData("<script>{\"playable_url\":\"javascript:bad()\"}</script>", "No downloadable video")]
    public void ExplainsUnsupportedPages(string html, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FacebookVideoExtractor.Extract(html, "https://facebook.com/reel/123"));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FacebookVideoResult Extract(string json) => FacebookVideoExtractor.Extract($"<script>{json}</script>", "https://facebook.com/reel/123");
}
