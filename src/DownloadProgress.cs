using System.Diagnostics;

namespace VideoDownloaderConsole;

internal sealed class DownloadProgress(long? total, Func<double>? clock = null)
{
    private readonly object gate = new();
    private readonly Dictionary<int, long> positions = [];
    private readonly Func<double> seconds = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private double? started;
    private double lastRender = double.NegativeInfinity;
    private long network;

    internal void Update(int chunk, long bytes, int received = 0)
    {
        lock (gate)
        {
            started ??= seconds();
            positions[chunk] = bytes;
            network += received;
            var now = seconds();
            if (Console.IsOutputRedirected || now - lastRender < 0.2) return;
            lastRender = now;
            var snapshot = Snapshot();
            var size = total is > 0 ? $"{snapshot.Percent:F1}% ({Utils.FormatFileSize(snapshot.Bytes)} / {Utils.FormatFileSize(total.Value)})"
                : Utils.FormatFileSize(snapshot.Bytes);
            var eta = snapshot.Eta is { } remaining ? $" | ETA {remaining:hh\\:mm\\:ss}" : "";
            Console.Write($"\r{size} | {Utils.FormatFileSize((long)snapshot.BytesPerSecond)}/s{eta}     ");
        }
    }

    internal ProgressSnapshot Snapshot()
    {
        lock (gate)
        {
            var bytes = positions.Values.Sum();
            if (total is > 0) bytes = Math.Min(bytes, total.Value);
            var elapsed = started is { } start ? seconds() - start : 0;
            var rate = elapsed > 0 ? network / elapsed : 0;
            var remaining = total is > 0 && rate > 0 ? (total.Value - bytes) / rate : double.NaN;
            return new(bytes, total is > 0 ? 100.0 * bytes / total.Value : null, rate,
                double.IsFinite(remaining) && remaining <= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(remaining) : null);
        }
    }
}

internal sealed record ProgressSnapshot(long Bytes, double? Percent, double BytesPerSecond, TimeSpan? Eta);
