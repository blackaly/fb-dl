using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoDownloaderConsole;

internal sealed class ResumeStore : IDisposable
{
    private sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public string Identity { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Length { get; set; }
        public string ETag { get; set; } = "";
        public int ChunkSize { get; set; } = VideoTransferService.ChunkSize;
        public Dictionary<int, string> Completed { get; set; } = [];
    }

    private readonly FileStream lease;
    private readonly string manifestPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Manifest manifest;
    internal string DataPath { get; }
    internal IReadOnlyDictionary<int, string> Completed => manifest.Completed;

    private ResumeStore(string stem, FileStream lease, Manifest manifest)
    { this.lease = lease; this.manifest = manifest; DataPath = stem + ".part"; manifestPath = stem + ".json"; }

    internal static async Task<ResumeStore> OpenAsync(string directory, string identity, string fileName,
        long length, string etag, CancellationToken token)
    {
        var stateDirectory = Path.Combine(directory, ".fb-dl");
        Directory.CreateDirectory(stateDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var stem = Path.Combine(stateDirectory, key);
        FileStream lease;
        // Keep the lock inode permanently: unlinking it would allow two owners on Unix.
        try { lease = new FileStream(stem + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("This download is already active in the output folder. Wait for it to finish before resuming.", ex); }
        var store = new ResumeStore(stem, lease, new Manifest { Identity = identity, FileName = fileName, Length = length, ETag = etag });
        try
        {
            Manifest? old = null;
            if (File.Exists(store.manifestPath) && new FileInfo(store.manifestPath).Length <= 16 * 1024 * 1024)
            {
                try { old = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(store.manifestPath, token)); }
                catch (JsonException) { }
            }
            var count = (length - 1) / VideoTransferService.ChunkSize + 1;
            var valid = old is { Version: 1 } && old.Identity == identity && old.Length == length && old.ETag == etag &&
                old.ChunkSize == VideoTransferService.ChunkSize && old.Completed is not null && old.Completed.Count <= count &&
                old.Completed.All(pair => pair.Key >= 0 && pair.Key < count && pair.Value is { Length: 64 } && pair.Value.All(Uri.IsHexDigit)) &&
                File.Exists(store.DataPath) && new FileInfo(store.DataPath).Length == length;
            if (valid)
            {
                store.manifest = old!;
                // The current filename comes from trusted extraction; never use paths supplied by JSON.
                store.manifest.FileName = fileName;
                await using var file = new FileStream(store.DataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                var buffer = new byte[128 * 1024];
                foreach (var pair in old!.Completed!.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    var start = (long)pair.Key * VideoTransferService.ChunkSize;
                    var remaining = Math.Min(VideoTransferService.ChunkSize, length - start);
                    file.Position = start;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    while (remaining > 0)
                    {
                        var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                        if (read == 0) break;
                        hash.AppendData(buffer, 0, read);
                        remaining -= read;
                    }
                    if (remaining != 0 || !Convert.ToHexString(hash.GetHashAndReset()).Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                        store.manifest.Completed!.Remove(pair.Key);
                }
            }
            else store.Invalidate();
            await store.SaveAsync(token);
            return store;
        }
        catch { store.Dispose(); throw; }
    }

    internal async Task CompleteChunkAsync(int index, string hash, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { manifest.Completed[index] = hash; await SaveAsync(token); }
        finally { gate.Release(); }
    }

    private async Task SaveAsync(CancellationToken token)
    {
        var temporary = manifestPath + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(file, manifest, cancellationToken: token);
            await file.FlushAsync(token);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, manifestPath, overwrite: true);
    }

    internal void Invalidate()
    {
        File.Delete(DataPath);
        File.Delete(manifestPath);
        File.Delete(manifestPath + ".tmp");
        manifest.Completed.Clear();
    }

    public void Dispose() { lease.Dispose(); gate.Dispose(); }
}
