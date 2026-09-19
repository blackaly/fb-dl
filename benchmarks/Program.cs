using System.Diagnostics;
using System.Security.Cryptography;
using FbDl.Tests;
using VideoDownloaderConsole;

// A controlled per-connection bottleneck, not a prediction of Facebook/CDN performance.
var directory = Path.Combine(Path.GetTempPath(), "fb-benchmark-" + Guid.NewGuid().ToString("N"));
try
{
    var times = new List<double>();
    foreach (var connections in new[] { 1, 4 })
    {
        await using var server = new RangeServer { BlockDelayMilliseconds = 16 };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var elapsed = Stopwatch.StartNew();
        var file = await new VideoTransferService(client).DownloadAsync(server.Url, $"connections-{connections}.mp4", "benchmark",
            new DownloadOptions { OutputDirectory = directory, Connections = connections, MaxRetries = 0 });
        elapsed.Stop();
        await using var saved = File.OpenRead(file);
        var actualHash = await SHA256.HashDataAsync(saved);
        if (!SHA256.HashData(server.Bytes).SequenceEqual(actualHash))
            throw new InvalidDataException("Benchmark output does not match the source.");
        var seconds = elapsed.Elapsed.TotalSeconds;
        times.Add(seconds);
        Console.WriteLine($"RESULT connections={connections} seconds={seconds:F3} MiB/s={server.Bytes.Length / 1048576.0 / seconds:F2} SHA256=verified");
    }
    Console.WriteLine($"Synthetic speedup: {times[0] / times[1]:F2}x (32 MiB, 64 KiB per 16 ms per connection).");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
