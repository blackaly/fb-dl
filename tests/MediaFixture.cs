using System.Buffers.Binary;
using System.Text;

namespace FbDl.Tests;

internal static class MediaFixture
{
    internal static byte[] Bytes(string label = "video", int length = 128)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 24);
        "ftypisom"u8.CopyTo(bytes.AsSpan(4));
        "isommp42"u8.CopyTo(bytes.AsSpan(16));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24), length - 24);
        "mdat"u8.CopyTo(bytes.AsSpan(28));
        Encoding.UTF8.GetBytes(label).CopyTo(bytes, 32);
        return bytes;
    }
    internal static HttpContent Content(string label = "video") => new ByteArrayContent(Bytes(label));
}
