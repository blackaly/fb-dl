namespace VideoDownloaderConsole;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            // A second interrupt must still work if graceful cleanup is stuck.
            if (cancellation.IsCancellationRequested)
                return;
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            using var downloader = new VideoDownloaderService();
            return await RunAsync(args, downloader, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            Console.ResetColor();
        }
    }

    internal static async Task<int> RunAsync(string[] args, VideoDownloaderService downloader,
        CancellationToken cancellationToken = default)
    {
        string? url = null;
        var options = new DownloadOptions();
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            var equals = argument.StartsWith("--", StringComparison.Ordinal) ? argument.IndexOf('=') : -1;
            var name = equals >= 0 ? argument[..equals] : argument;
            string? Value() => equals >= 0 ? argument[(equals + 1)..] :
                i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
            switch (name)
            {
                case "--help" or "-h" when equals < 0:
                    help = true;
                    break;
                case "--resume" when equals < 0:
                    options = options with { Resume = true };
                    break;
                case "--output" or "-o":
                    var folder = Value();
                    if (string.IsNullOrWhiteSpace(folder)) return UsageError("--output requires a folder path.");
                    options = options with { OutputDirectory = folder };
                    break;
                case "--quality":
                    var quality = Value();
                    if (quality is not ("best" or "hd" or "sd")) return UsageError("--quality requires best, hd, or sd.");
                    options = options with { Quality = quality };
                    break;
                case "--connections":
                    if (!int.TryParse(Value(), out var connections) || connections is < 1 or > 8)
                        return UsageError("--connections requires a number from 1 to 8.");
                    options = options with { Connections = connections };
                    break;
                case "--retries":
                    if (!int.TryParse(Value(), out var retries) || retries is < 0 or > HttpRetry.MaximumRetries)
                        return UsageError("--retries requires a number from 0 to 10.");
                    options = options with { MaxRetries = retries };
                    break;
                default:
                    if (argument.StartsWith('-')) return UsageError($"Unknown option: {argument}");
                    if (url != null || string.IsNullOrWhiteSpace(argument)) return UsageError("Provide one video URL at a time.");
                    url = argument;
                    break;
            }
        }

        if (help)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (url != null)
            {
                await downloader.ProcessVideoUrl(url, options, cancellationToken);
                return 0;
            }

            Console.WriteLine("=== Facebook Downloader By Ali Mashally ===");
            Console.WriteLine("Enter a public Facebook video URL, or type 'exit' to quit.\n");
            var exitCode = 0;
            while (true)
            {
                Console.Write("Enter video URL: ");
                var input = (await Utils.ReadLineAsync(cancellationToken))?.Trim();
                if (input == null || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    return exitCode;
                if (input.Length == 0)
                    continue;
                try
                {
                    await downloader.ProcessVideoUrl(input, options, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    exitCode = 1;
                }
                Console.WriteLine();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Download cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage(Console.Error);
        return 2;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: fb-dl [URL] [--output FOLDER] [--quality best|hd|sd] [--resume] [--connections N] [--retries N]");
        writer.WriteLine("  -o, --output FOLDER  Save videos here (default: ~/Downloads).");
        writer.WriteLine("      --retries N      Retry temporary request failures 0-10 times (default: 2).");
        writer.WriteLine("      --quality Q      Choose best, hd, or sd without prompting.");
        writer.WriteLine("      --resume         Keep and verify completed chunks after interruption.");
        writer.WriteLine("      --connections N  Concurrent connections 1-8 (default: 4; large videos).");
        writer.WriteLine("  -h, --help           Show this help.");
        writer.WriteLine("Omit URL for interactive mode. Choose SD or HD when prompted.");
        writer.WriteLine("Ctrl+C cancels. Existing downloads are never overwritten.");
    }
}
