using System.Diagnostics;
using System.Text;
using Xunit;

namespace FbDl.Tests;

public class CliTests
{
    [Fact]
    public async Task SingleUrlIsProcessedInsteadOfOpeningTheUrlPrompt()
    {
        var result = await Run("exit\n", "https://example.invalid/video");
        Assert.False(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Enter video URL", result.Output);
    }

    [Fact]
    public async Task ClosedInputExitsWithoutLooping()
    {
        var result = await Run("");
        Assert.False(result.TimedOut, "The CLI kept running after stdin closed.");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HelpPrintsUsageWithoutPrompting()
    {
        var result = await Run("exit\n", "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--output", result.Output);
        Assert.DoesNotContain("Enter video URL", result.Output);
    }

    [Theory]
    [InlineData("--quality")]
    [InlineData("--quality=ultra")]
    [InlineData("--connections=0")]
    [InlineData("--connections", "9")]
    [InlineData("--resume=true")]
    [InlineData("--unknown")]
    [InlineData("--output")]
    [InlineData("--output", "")]
    [InlineData("one", "two")]
    [InlineData("--retries")]
    [InlineData("--retries", "-1")]
    [InlineData("--retries", "11")]
    [InlineData("--retries", "invalid")]
    public async Task InvalidArgumentsReturnUsageError(params string[] args)
    {
        var result = await Run("exit\n", args);
        Assert.Equal(2, result.ExitCode);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("Enter video URL", result.Output);
    }

    [Fact]
    public async Task InteractiveErrorsAreReportedAndTheUserCanExit()
    {
        var result = await Run("https://example.invalid/video\nexit\n");
        Assert.False(result.TimedOut);
        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.Error);
    }

    private static async Task<CliResult> Run(string input, params string[] args)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "fb-dl.csproj")))
            root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Also allows these checks on a developer machine with only a newer runtime.
        start.ArgumentList.Add("--roll-forward");
        start.ArgumentList.Add("Major");
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "bin", configuration, "net10.0", "fb-dl.dll"));
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = ReadBounded(process.StandardOutput);
        var error = ReadBounded(process.StandardError);
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        return new(process.ExitCode, await output, await error, timedOut);
    }

    private static async Task<string> ReadBounded(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            if (text.Length < 16384)
                text.Append(buffer, 0, Math.Min(count, 16384 - text.Length));
        }
        return text.ToString();
    }

    private record CliResult(int ExitCode, string Output, string Error, bool TimedOut);
}
