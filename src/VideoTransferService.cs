using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VideoDownloaderConsole;

internal sealed class VideoTransferService(HttpClient client)
{
    internal const int ChunkSize = 8 * 1024 * 1024;
    internal const long ParallelThreshold = 16 * 1024 * 1024;
    private sealed class RepresentationException(string message) : Exception(message);
    private sealed record Representation(long Length, EntityTagHeaderValue ETag);

    internal async Task<string> DownloadAsync(string url, string fileName, string identity,
        DownloadOptions options, CancellationToken token = default)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        if (!Utils.IsValidUrl(url)) throw new ArgumentException("The video download URL must use HTTP or HTTPS.", nameof(url));
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.IndexOfAny(['/', '\\']) >= 0 || Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("Provide a filename without a directory path.", nameof(fileName));
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") : options.OutputDirectory);
        if (options.Connections > 1 || options.Resume)
        {
            try
            {
                using var probe = await HttpRetry.SendAsync(client, () => Request(url, 0, MediaValidator.PrefixLength - 1),
                    HttpCompletionOption.ResponseHeadersRead, options.MaxRetries, token);
                if (probe.StatusCode == HttpStatusCode.OK)
                {
                    if (options.Resume) Console.Error.WriteLine("The server ignored ranges; downloading from the beginning without a checkpoint.");
                    return await SequentialAsync(probe, directory, fileName, token);
                }
                var representation = ProbeRepresentation(probe);
                await ValidateProbeAsync(probe, representation, token);
                if (options.Resume || representation.Length >= ParallelThreshold)
                    return await RangedAsync(url, directory, fileName, identity, representation, options, token);
            }
            catch (RepresentationException ex)
            { Console.Error.WriteLine($"{ex.Message} Downloading from the beginning with one connection."); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            { Console.Error.WriteLine("The server rejected ranges. Downloading from the beginning with one connection."); }
        }
        using var response = await HttpRetry.SendAsync(client, () => Request(url), HttpCompletionOption.ResponseHeadersRead, options.MaxRetries, token);
        return await SequentialAsync(response, directory, fileName, token);
    }

    private static HttpRequestMessage Request(string url, long? start = null, long? end = null, EntityTagHeaderValue? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.Referrer = new Uri("https://www.facebook.com/");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (start.HasValue) request.Headers.Range = new RangeHeaderValue(start, end);
        if (etag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(etag);
        return request;
    }

    private static bool IdentityEncoding(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.All(e => e.Equals("identity", StringComparison.OrdinalIgnoreCase));

    private static Representation ProbeRepresentation(HttpResponseMessage response)
    {
        var range = response.Content.Headers.ContentRange;
        var etag = response.Headers.ETag;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != 0 ||
            range.Length is not > 0 || range.To != Math.Min(MediaValidator.PrefixLength, range.Length.Value) - 1 ||
            etag is null || etag.IsWeak || etag.Tag == "*" || !IdentityEncoding(response))
            throw new RepresentationException("The server does not provide verifiable ranges and a strong ETag.");
        return new(range.Length.Value, etag);
    }

    private async Task ValidateProbeAsync(HttpResponseMessage response, Representation representation, CancellationToken token)
    {
        MediaValidator.ValidateContentType(response);
        var expected = (int)Math.Min(MediaValidator.PrefixLength, representation.Length);
        if (response.Content.Headers.ContentLength is { } length && length != expected)
            throw new RepresentationException("The range probe has an inconsistent length.");
        var bytes = new byte[expected];
        await using var source = await response.Content.ReadAsStreamAsync(token);
        var received = 0;
        while (received < expected)
        {
            var count = await ReadAsync(source, bytes.AsMemory(received), token);
            if (count == 0) throw new RepresentationException("The range probe was truncated.");
            received += count;
        }
        if (await ReadAsync(source, new byte[1], token) != 0) throw new RepresentationException("The range probe exceeded its length.");
        MediaValidator.ValidatePrefix(bytes);
    }

    private async Task<string> RangedAsync(string url, string directory, string fileName, string identity,
        Representation representation, DownloadOptions options, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var store = options.Resume ? await ResumeStore.OpenAsync(directory, identity, fileName,
            representation.Length, representation.ETag.ToString(), token) : null;
        var partial = store?.DataPath ?? Path.Combine(directory, $".fb-dl-{Guid.NewGuid():N}.part");
        var owned = false;
        var preserve = false;
        try
        {
            var chunkCount = checked((int)((representation.Length - 1) / ChunkSize + 1));
            var progress = new DownloadProgress(representation.Length);
            var completed = store?.Completed.Keys.ToHashSet() ?? [];
            foreach (var index in completed)
                progress.Update(index, Math.Min(ChunkSize, representation.Length - (long)index * ChunkSize));
            if (completed.Count > 0) Console.WriteLine($"Resuming {completed.Count} verified chunk(s).");
            var missing = Enumerable.Range(0, chunkCount).Where(i => !completed.Contains(i)).ToArray();
            await using (var file = new FileStream(partial, store is null ? FileMode.CreateNew : FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                owned = true;
                file.SetLength(representation.Length);
                using var workers = CancellationTokenSource.CreateLinkedTokenSource(token);
                var next = -1;
                Exception? failure = null;
                var count = Math.Min(missing.Length, representation.Length >= ParallelThreshold ? options.Connections : 1);
                Console.WriteLine($"Downloading with {count} connection(s) to: {directory}");
                async Task Worker()
                {
                    try
                    {
                        int position;
                        while ((position = Interlocked.Increment(ref next)) < missing.Length)
                        {
                            var index = missing[position];
                            var hash = await DownloadChunkAsync(url, file.SafeFileHandle, index, representation,
                                options.MaxRetries, progress, workers.Token);
                            if (store is not null)
                            {
                                RandomAccess.FlushToDisk(file.SafeFileHandle);
                                await store.CompleteChunkAsync(index, hash, workers.Token);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is RepresentationException or InvalidDataException) Interlocked.Exchange(ref failure, ex);
                        else if (ex is not OperationCanceledException || !workers.IsCancellationRequested)
                            Interlocked.CompareExchange(ref failure, ex, null);
                        await workers.CancelAsync();
                    }
                }
                await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Worker()));
                if (failure is RepresentationException or InvalidDataException)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                token.ThrowIfCancellationRequested();
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                await file.FlushAsync(token);
                var prefix = new byte[(int)Math.Min(MediaValidator.PrefixLength, representation.Length)];
                file.Position = 0;
                await file.ReadExactlyAsync(prefix, token);
                MediaValidator.ValidatePrefix(prefix);
            }
            token.ThrowIfCancellationRequested();
            var final = MoveWithoutOverwrite(partial, directory, fileName);
            owned = false;
            store?.Invalidate();
            Console.WriteLine($"\nDownload successful: {final}");
            return final;
        }
        catch (Exception ex) when (store is not null && ex is OperationCanceledException or HttpRequestException or TimeoutException)
        {
            preserve = true;
            Console.Error.WriteLine("Verified chunks were kept. Run the same command with --resume to continue.");
            throw;
        }
        finally
        {
            if (!preserve)
            {
                if (store is not null) store.Invalidate();
                else if (owned) File.Delete(partial);
            }
        }
    }

    private async Task<string> DownloadChunkAsync(string url, SafeFileHandle handle, int index,
        Representation representation, int retries, DownloadProgress progress, CancellationToken token)
    {
        var start = (long)index * ChunkSize;
        var end = Math.Min(representation.Length, start + ChunkSize) - 1;
        try
        {
            return await HttpRetry.TransferAsync(client, () => Request(url, start, end, representation.ETag), retries,
                async (response, ct) =>
                {
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                        range.From != start || range.To != end || range.Length != representation.Length ||
                        response.Headers.ETag?.ToString() != representation.ETag.ToString() || !IdentityEncoding(response) ||
                        response.Content.Headers.ContentLength is { } size && size != end - start + 1)
                        throw new RepresentationException("The server changed the video or returned an inconsistent range.");
                    MediaValidator.ValidateContentType(response);
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    long received = 0;
                    progress.Update(index, 0);
                    try
                    {
                        while (true)
                        {
                            int read;
                            try { read = await ReadAsync(source, buffer, ct); }
                            catch (IOException ex) { throw new HttpRequestException("The range connection was interrupted.", ex); }
                            if (read == 0) break;
                            if (read > end - start + 1 - received) throw new RepresentationException("A video range exceeded its expected length.");
                            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), start + received, ct);
                            hash.AppendData(buffer, 0, read);
                            received += read;
                            progress.Update(index, received, read);
                        }
                        if (received != end - start + 1) throw new HttpRequestException("The video range was incomplete.");
                        return Convert.ToHexString(hash.GetHashAndReset());
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }, token);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        { throw new RepresentationException("The server no longer accepts this video range."); }
    }

    private async Task<string> SequentialAsync(HttpResponseMessage response, string directory, string fileName, CancellationToken token)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("A full video response was expected.");
        MediaValidator.ValidateContentType(response);
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, $".fb-dl-{Guid.NewGuid():N}.part");
        var owned = false;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            Console.WriteLine($"Downloading to: {directory}");
            var total = response.Content.Headers.ContentLength;
            var progress = new DownloadProgress(total);
            var prefix = new byte[MediaValidator.PrefixLength];
            var prefixCount = 0;
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                owned = true;
                while (true)
                {
                    var count = await ReadAsync(source, buffer, token);
                    if (count == 0) break;
                    if (total.HasValue && count > total.Value - received) throw new IOException("The video transfer exceeded its advertised length.");
                    var copy = Math.Min(count, prefix.Length - prefixCount);
                    buffer.AsSpan(0, copy).CopyTo(prefix.AsSpan(prefixCount));
                    prefixCount += copy;
                    if (prefixCount == prefix.Length && received < prefix.Length) MediaValidator.ValidatePrefix(prefix);
                    await file.WriteAsync(buffer.AsMemory(0, count), token);
                    received += count;
                    progress.Update(0, received, count);
                }
                if (received == 0 || total.HasValue && received != total.Value) throw new IOException("The video transfer was incomplete. Please try again.");
                MediaValidator.ValidatePrefix(prefix.AsSpan(0, prefixCount));
                await file.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            var final = MoveWithoutOverwrite(partial, directory, fileName);
            owned = false;
            Console.WriteLine($"\nDownload successful: {final}");
            return final;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (owned) File.Delete(partial);
        }
    }

    private async ValueTask<int> ReadAsync(Stream source, Memory<byte> buffer, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(client.Timeout);
        try { return await source.ReadAsync(buffer, idle.Token); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The download stalled while waiting for video data. Please try again.", ex); }
    }

    internal static string MoveWithoutOverwrite(string partial, string directory, string fileName)
    {
        for (var suffix = 0; ; suffix++)
        {
            var name = suffix == 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)} ({suffix}){Path.GetExtension(fileName)}";
            var destination = Path.Combine(directory, name);
            try { AtomicFile.MoveNew(partial, destination); return destination; }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
        }
    }
}
