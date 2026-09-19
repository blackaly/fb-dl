using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FbDl.Tests;

internal sealed class RangeServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopped = new();
    private readonly ConcurrentBag<Task> requests = [];
    private readonly Task accepting;
    private int active;
    private int maximum;
    private int failed;
    internal byte[] Bytes { get; } = MediaFixture.Bytes(length: 32 * 1024 * 1024 + 123);
    internal byte[] Replacement { get; } = MediaFixture.Bytes("replacement", 32 * 1024 * 1024 + 123);
    internal TaskCompletionSource ChangeRepresentation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int? BlockDelayMilliseconds { get; set; }
    internal string Mode { get; set; } = "normal";
    internal string ETag { get; set; } = "\"video-1\"";
    internal string Url { get; }
    internal int MaximumActiveRequests => maximum;
    internal int ActiveRequests => active;
    internal ConcurrentQueue<(long Start, long End)> Ranges { get; } = [];
    internal ConcurrentQueue<string?> IfRanges { get; } = [];
    internal int FullRequests;
    internal TaskCompletionSource FirstChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RangeServer()
    {
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/video.mp4";
        accepting = Accept();
    }
    private async Task Accept()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopped.Token);
                requests.Add(Serve(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stopped.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var firstLine = await reader.ReadLineAsync(stopped.Token);
            if (firstLine is null) return;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(stopped.Token) is { Length: > 0 } line)
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
            }
            long start = 0, end = Bytes.Length - 1;
            var ranged = headers.TryGetValue("Range", out var range);
            if (ranged)
            {
                var parts = range![6..].Split('-');
                start = long.Parse(parts[0]); end = Math.Min(long.Parse(parts[1]), end);
                Ranges.Enqueue((start, end));
            }
            var chunk = ranged && end - start + 1 > 65536;
            if (chunk) IfRanges.Enqueue(headers.GetValueOrDefault("If-Range"));
            if (Mode == "block-after-first" && chunk && start > 0)
                await Task.Delay(Timeout.Infinite, stopped.Token);
            if (Mode == "change-after-first" && chunk && start > 0)
                await ChangeRepresentation.Task.WaitAsync(stopped.Token);
            var status = ranged ? 206 : 200;
            var etag = ETag;
            var payload = Bytes;
            if (Mode == "change-after-first" && (!ranged || chunk && start > 0))
            {
                etag = "\"replacement\"";
                payload = Replacement;
            }
            var returnedStart = start;
            if (Mode == "ignore" || chunk && Mode == "ignore-chunk") { status = 200; start = 0; end = Bytes.Length - 1; }
            if (chunk && Mode == "changed") etag = "\"video-2\"";
            if (chunk && Mode == "bounds") returnedStart++;
            if (chunk && Mode == "416") status = 416;
            if (chunk && (Mode == "429" || Mode == "503") && Interlocked.Exchange(ref failed, 1) == 0) status = int.Parse(Mode);
            if (Mode == "no-etag") etag = "";
            if (Mode == "weak-etag") etag = "W/\"video-1\"";
            if (!ranged) Interlocked.Increment(ref FullRequests);
            var length = status is 200 or 206 ? end - start + 1 : 0;
            var response = $"HTTP/1.1 {status} Response\r\nContent-Length: {length}\r\nContent-Type: video/mp4\r\nConnection: close\r\n";
            if (etag.Length > 0) response += $"ETag: {etag}\r\n";
            if (status == 206) response += $"Content-Range: bytes {returnedStart}-{end}/{Bytes.Length}\r\n";
            if (status is 429 or 503) response += "Retry-After: 0\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), stopped.Token);
            if (length == 0) return;
            var tracked = chunk && status == 206;
            if (tracked)
            {
                var count = Interlocked.Increment(ref active);
                int prior;
                do { prior = maximum; } while (count > prior && Interlocked.CompareExchange(ref maximum, count, prior) != prior);
            }
            try
            {
                if (chunk && Mode == "truncate" && Interlocked.Exchange(ref failed, 1) == 0) end = start + 1000;
                for (var offset = start; offset <= end;)
                {
                    var count = (int)Math.Min(65536, end - offset + 1);
                    await stream.WriteAsync(payload.AsMemory((int)offset, count), stopped.Token);
                    offset += count;
                    if ((BlockDelayMilliseconds ?? (tracked ? 1 : 0)) is var delay && delay > 0) await Task.Delay(delay, stopped.Token);
                }
                if (chunk && start == 0) FirstChunk.TrySetResult();
            }
            finally { if (tracked) Interlocked.Decrement(ref active); }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel(); listener.Stop();
        await accepting;
        await Task.WhenAll(requests);
        stopped.Dispose();
    }
}
