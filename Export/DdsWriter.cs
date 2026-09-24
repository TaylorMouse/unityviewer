using System.Buffers.Binary;
using UnityBrowser.Unity;

namespace UnityBrowser.Export;

/// <summary>
/// DDS writer. BC1/BC2/BC3/BC4/BC5 textures keep their original compressed data (all mips),
/// flipped block-wise to top-down. Everything else is written as uncompressed BGRA8.
/// </summary>
public static class DdsWriter
{
    private const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PITCH = 0x8,
        DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000, DDSD_LINEARSIZE = 0x80000;
    private const uint DDPF_ALPHAPIXELS = 0x1, DDPF_FOURCC = 0x4, DDPF_RGB = 0x40;
    private const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;

    /// <summary>Writes a Unity texture. Returns a short description of what was written.</summary>
    public static string WriteTexture(string path, TextureData t)
    {
        int mips = CountAvailableMips(t);
        var levels = new List<(int W, int H, byte[] Data)>();

        if (BlockBytes(t.Format) > 0 && CanFlipBlocks(t.Width, t.Height, mips))
        {
            long offset = 0;
            for (int i = 0; i < mips; i++)
            {
                int w = Math.Max(1, t.Width >> i), h = Math.Max(1, t.Height >> i);
                int size = (int)TextureDecoder.Mip0Size(t.Format, w, h);
                levels.Add((w, h, FlipBlocks(t.Data.AsSpan((int)offset, size), t.Format, w, h)));
                offset += size;
            }
            Write(path, t.Width, t.Height, levels, FourCC(t.Format));
            return $"{TextureDecoder.FormatName(t.Format)}, {mips} mips";
        }

        // Decode each level to BGRA8 (decoder output is already top-down).
        long pos = 0;
        for (int i = 0; i < mips; i++)
        {
            int w = Math.Max(1, t.Width >> i), h = Math.Max(1, t.Height >> i);
            int size = (int)TextureDecoder.Mip0Size(t.Format, w, h);
            var slice = t.Data.AsSpan((int)pos, size).ToArray();
            levels.Add((w, h, TextureDecoder.Decode(slice, t.Format, w, h)));
            pos += size;
        }
        Write(path, t.Width, t.Height, levels, fourCC: null);
        return $"BGRA8 (from {TextureDecoder.FormatName(t.Format)}), {mips} mips";
    }

    /// <summary>Writes a single top-down BGRA8 image (used for sprites).</summary>
    public static void WriteBgra(string path, int width, int height, byte[] bgra) =>
        Write(path, width, height, [(width, height, bgra)], fourCC: null);

    private static int CountAvailableMips(TextureData t)
    {
        long total = 0;
        int count = 0;
        for (int i = 0; i < t.MipCount; i++)
        {
            int w = Math.Max(1, t.Width >> i), h = Math.Max(1, t.Height >> i);
            long size = TextureDecoder.Mip0Size(t.Format, w, h);
            if (size < 0) throw new NotSupportedException($"{TextureDecoder.FormatName(t.Format)} textures are not supported yet.");
            if (total + size > t.Data.Length) break;
            total += size;
            count++;
        }
        if (count == 0) throw new InvalidDataException("Texture data is smaller than its top mip level.");
        return count;
    }

    private static int BlockBytes(int format) => format switch
    {
        10 or 26 => 8,
        11 or 12 or 27 => 16,
        _ => 0,
    };

    private static string FourCC(int format) => format switch
    {
        10 => "DXT1",
        11 => "DXT3",
        12 => "DXT5",
        26 => "ATI1",
        27 => "ATI2",
        _ => throw new InvalidOperationException(),
    };

    /// <summary>A block flip is exact only when every level's height is a multiple of 4, or under 4.</summary>
    private static bool CanFlipBlocks(int width, int height, int mips)
    {
        for (int i = 0; i < mips; i++)
        {
            int h = Math.Max(1, height >> i);
            if (h >= 4 && h % 4 != 0) return false;
        }
        return true;
    }

    private static byte[] FlipBlocks(ReadOnlySpan<byte> src, int format, int w, int h)
    {
        int bs = BlockBytes(format);
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        int rows = Math.Min(h, 4);
        var dst = new byte[bw * bh * bs];
        for (int by = 0; by < bh; by++)
        {
            int dby = bh - 1 - by;
            for (int bx = 0; bx < bw; bx++)
            {
                var block = dst.AsSpan((dby * bw + bx) * bs, bs);
                src.Slice((by * bw + bx) * bs, bs).CopyTo(block);
                FlipBlock(block, format, rows);
            }
        }
        return dst;
    }

    /// <summary>Reverses the first <paramref name="n"/> pixel rows inside one block.</summary>
    private static void FlipBlock(Span<byte> b, int format, int n)
    {
        switch (format)
        {
            case 10:
                FlipColour(b, n);
                break;
            case 11:
                FlipExplicitAlpha(b.Slice(0, 8), n);
                FlipColour(b.Slice(8), n);
                break;
            case 12:
                FlipInterpolatedAlpha(b.Slice(0, 8), n);
                FlipColour(b.Slice(8), n);
                break;
            case 26:
                FlipInterpolatedAlpha(b, n);
                break;
            case 27:
                FlipInterpolatedAlpha(b.Slice(0, 8), n);
                FlipInterpolatedAlpha(b.Slice(8, 8), n);
                break;
        }
    }

    // BC1 colour block: 2 endpoints (4 bytes), then one byte of 2-bit indices per row.
    private static void FlipColour(Span<byte> b, int n) => b.Slice(4, n).Reverse();

    // BC2 alpha: 4-bit values, two bytes per row.
    private static void FlipExplicitAlpha(Span<byte> b, int n)
    {
        Span<byte> tmp = stackalloc byte[8];
        b.CopyTo(tmp);
        for (int y = 0; y < n; y++)
        {
            b[y * 2] = tmp[(n - 1 - y) * 2];
            b[y * 2 + 1] = tmp[(n - 1 - y) * 2 + 1];
        }
    }

    // BC3/BC4 alpha: 2 endpoints, then 48 bits of 3-bit indices, 12 bits per row.
    private static void FlipInterpolatedAlpha(Span<byte> b, int n)
    {
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        ulong result = bits;
        for (int y = 0; y < n; y++)
        {
            ulong row = (bits >> (12 * (n - 1 - y))) & 0xFFF;
            result &= ~(0xFFFUL << (12 * y));
            result |= row << (12 * y);
        }
        for (int i = 0; i < 6; i++) b[2 + i] = (byte)(result >> (8 * i));
    }

    private static void Write(string path, int width, int height, List<(int W, int H, byte[] Data)> levels, string? fourCC)
    {
        var header = new byte[128];
        var h = header.AsSpan();
        "DDS "u8.CopyTo(h);

        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT;
        if (levels.Count > 1) flags |= DDSD_MIPMAPCOUNT;
        flags |= fourCC != null ? DDSD_LINEARSIZE : DDSD_PITCH;

        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], flags);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], height);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], fourCC != null ? levels[0].Data.Length : width * 4);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], levels.Count);

        // DDS_PIXELFORMAT at offset 76.
        BinaryPrimitives.WriteUInt32LittleEndian(h[76..], 32);
        if (fourCC != null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[80..], DDPF_FOURCC);
            System.Text.Encoding.ASCII.GetBytes(fourCC).CopyTo(h[84..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[80..], DDPF_RGB | DDPF_ALPHAPIXELS);
            BinaryPrimitives.WriteUInt32LittleEndian(h[88..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(h[92..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(h[96..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(h[100..], 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(h[104..], 0xFF000000);
        }

        uint caps = DDSCAPS_TEXTURE;
        if (levels.Count > 1) caps |= DDSCAPS_COMPLEX | DDSCAPS_MIPMAP;
        BinaryPrimitives.WriteUInt32LittleEndian(h[108..], caps);

        using var fs = File.Create(path);
        fs.Write(header);
        foreach (var level in levels) fs.Write(level.Data);
    }
}
