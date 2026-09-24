using System.Buffers.Binary;
using System.IO.Compression;

namespace UnityBrowser.Export;

/// <summary>Minimal RGBA8 PNG encoder (thread-safe, no WPF dependency).</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes a top-down BGRA8 image as an RGBA PNG.</summary>
    public static void Write(string path, int width, int height, byte[] bgra)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: RGBA
        WriteChunk(fs, "IHDR", ihdr);

        // Each row: filter byte 1 (Sub), then RGBA deltas against the pixel to the left.
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride, dst = y * (stride + 1);
            raw[dst++] = 1;
            for (int x = 0; x < stride; x += 4)
            {
                byte r = bgra[src + x + 2], g = bgra[src + x + 1], b = bgra[src + x], a = bgra[src + x + 3];
                if (x == 0)
                {
                    raw[dst + x] = r; raw[dst + x + 1] = g; raw[dst + x + 2] = b; raw[dst + x + 3] = a;
                }
                else
                {
                    raw[dst + x] = (byte)(r - bgra[src + x - 2]);
                    raw[dst + x + 1] = (byte)(g - bgra[src + x - 3]);
                    raw[dst + x + 2] = (byte)(b - bgra[src + x - 4]);
                    raw[dst + x + 3] = (byte)(a - bgra[src + x - 1]);
                }
            }
        }

        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw);
            WriteChunk(fs, "IDAT", ms.ToArray());
        }
        WriteChunk(fs, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        uint crc = Crc(0xFFFFFFFF, typeBytes);
        crc = Crc(crc, data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
