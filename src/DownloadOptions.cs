namespace VideoDownloaderConsole;

public sealed record DownloadOptions
{
    public string? OutputDirectory { get; init; }
    public string? Quality { get; init; }
    public bool Resume { get; init; }
    public int Connections { get; init; } = 4;
    public int MaxRetries { get; init; } = HttpRetry.DefaultRetries;

    internal void Validate()
    {
        if (Connections is < 1 or > 8)
            throw new ArgumentException("--connections requires a number from 1 to 8.");
        if (MaxRetries is < 0 or > HttpRetry.MaximumRetries)
            throw new ArgumentException("--retries requires a number from 0 to 10.");
        if (Quality is not null and not ("best" or "hd" or "sd"))
            throw new ArgumentException("--quality requires best, hd, or sd.");
    }
}
