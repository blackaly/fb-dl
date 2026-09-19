# Transfer benchmark and verification

Run from the repository root with .NET 10:

```bash
dotnet run --project benchmarks/fb-dl.Benchmark.csproj -c Release
```

The loopback server serves the same 32 MiB + 123 byte MP4 fixture, delaying 16 ms per 64 KiB per connection. The benchmark invokes the production transfer engine with one and four connections and checks output SHA-256. Temporary output is removed automatically. No Facebook requests occur.

Measured on this Linux development machine on 2026-09-19:

| Connections | Elapsed | Throughput | Integrity |
| --- | --- | --- | --- |
| 1 | 8.357 s | 3.83 MiB/s | SHA-256 matches |
| 4 | 2.068 s | 15.48 MiB/s | SHA-256 matches |

This is a **4.04× synthetic speedup** under a per-connection bottleneck. It is not a guaranteed Internet/CDN speedup and is not an ordinary test timing assertion.

A separate live check successfully downloaded public reel `2691379201315776` using `--quality best --resume`. That CDN response lacked the range/strong-ETag guarantees required for safe chunk reuse, and the tool fell back to a fresh sequential download. The saved file was 25,014,485 bytes; an independent `ffprobe` check identified 87.56 seconds of H.264 video at 720×1280 and AAC audio. No parallel Facebook speedup was established by that check.

The Linux self-contained single executable was built and ran successfully inside a network-disabled container with `/usr/share/dotnet` hidden. The .NET 10 Docker image built and its help command passed. The Release regression suite passed 123/123 checks. Windows and macOS builds are configured in the manual workflow and require their native CI runs for validation. No workflow or release was published during development.
