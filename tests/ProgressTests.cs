using VideoDownloaderConsole;
using Xunit;

namespace FbDl.Tests;

public class ProgressTests
{
    [Fact]
    public void RestoredBytesAndRetransmissionsDoNotInflateCompletion()
    {
        double time = 0;
        var progress = new DownloadProgress(1000, () => time);
        progress.Update(0, 500); // restored, no traffic
        time = 1;
        progress.Update(1, 100, 100);
        Assert.Equal(100, progress.Snapshot().BytesPerSecond);
        Assert.Equal(60, progress.Snapshot().Percent);
        progress.Update(1, 0); // retry discards this attempt's unique bytes
        time = 2;
        progress.Update(1, 500, 500);
        var snapshot = progress.Snapshot();
        Assert.Equal(1000, snapshot.Bytes);
        Assert.Equal(100, snapshot.Percent);
        Assert.Equal(300, snapshot.BytesPerSecond);
        Assert.Equal(TimeSpan.Zero, snapshot.Eta);
    }

    [Fact]
    public void UnknownLengthOmitsPercentageAndEta()
    {
        var progress = new DownloadProgress(null, () => 0);
        progress.Update(0, 20, 20);
        Assert.Null(progress.Snapshot().Percent);
        Assert.Null(progress.Snapshot().Eta);
        Assert.Equal(0, progress.Snapshot().BytesPerSecond);
    }
}
