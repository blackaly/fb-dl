using System.Buffers.Binary;

namespace VideoDownloaderConsole;

internal static class MediaValidator
{
    internal const int PrefixLength = 64 * 1024;

    internal static void ValidateContentType(HttpResponseMessage response)
    {
        var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") ||
            type.Contains("mpegurl") || type.Contains("dash") || type.Contains("playlist"))
            throw new InvalidDataException($"The server returned {type}, not an MP4 video. The link may have expired or require login.");
    }

    internal static void ValidatePrefix(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (offset <= bytes.Length - 8)
        {
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            var header = 8;
            if (size == 1)
            {
                if (bytes.Length - offset < 16) break;
                size = BinaryPrimitives.ReadUInt64BigEndian(bytes[(offset + 8)..]);
                header = 16;
            }
            if (size < (ulong)header || size > (ulong)(bytes.Length - offset)) break;
            if (bytes.Slice(offset + 4, 4).SequenceEqual("ftyp"u8))
            {
                if (size < (ulong)header + 8 || (size - (ulong)header - 8) % 4 != 0) break;
                // A major brand is a printable four-character code, not an HTML fragment or zeros.
                var brand = bytes.Slice(offset + header, 4);
                if (brand.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0) break;
                return;
            }
            // Only small padding boxes may precede the file type; arbitrary text is never skipped.
            if (!bytes.Slice(offset + 4, 4).SequenceEqual("free"u8) &&
                !bytes.Slice(offset + 4, 4).SequenceEqual("skip"u8) &&
                !bytes.Slice(offset + 4, 4).SequenceEqual("wide"u8)) break;
            offset += (int)size;
        }
        throw new InvalidDataException("The response does not contain a valid MP4 header; no video was saved.");
    }
}
