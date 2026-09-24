namespace UnityBrowser.Unity;

public static class Compression
{
    public static string Name(uint type) => type switch
    {
        0 => "None",
        1 => "LZMA",
        2 => "LZ4",
        3 => "LZ4HC",
        4 => "LZHAM",
        _ => $"Unknown ({type})",
    };

    public static void Decompress(uint type, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        switch (type)
        {
            case 0:
                src.Slice(0, dst.Length).CopyTo(dst);
                break;
            case 1:
                DecodeLzma(src, dst);
                break;
            case 2:
            case 3:
                int written = DecodeLz4(src, dst);
                if (written != dst.Length)
                    throw new InvalidDataException($"LZ4 produced {written} bytes, expected {dst.Length}.");
                break;
            default:
                throw new NotSupportedException($"Compression type {Name(type)} is not supported.");
        }
    }

    /// <summary>Raw LZ4 block decoder (no frame header).</summary>
    public static int DecodeLz4(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (s < src.Length)
        {
            int token = src[s++];

            int literals = token >> 4;
            if (literals == 15)
            {
                byte b;
                do { b = src[s++]; literals += b; } while (b == 255);
            }
            src.Slice(s, literals).CopyTo(dst.Slice(d));
            s += literals;
            d += literals;

            if (s >= src.Length) break; // last sequence has literals only

            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            if (offset == 0) throw new InvalidDataException("LZ4 offset of zero.");

            int matchLength = token & 15;
            if (matchLength == 15)
            {
                byte b;
                do { b = src[s++]; matchLength += b; } while (b == 255);
            }
            matchLength += 4;

            int match = d - offset;
            if (match < 0) throw new InvalidDataException("LZ4 offset before start of output.");
            if (offset >= matchLength)
            {
                dst.Slice(match, matchLength).CopyTo(dst.Slice(d));
            }
            else
            {
                // Overlapping copy must run byte by byte.
                for (int i = 0; i < matchLength; i++) dst[d + i] = dst[match + i];
            }
            d += matchLength;
        }
        return d;
    }

    /// <summary>Unity LZMA: 5-byte properties followed by the raw stream.</summary>
    private static void DecodeLzma(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var decoder = new SevenZip.Compression.LZMA.Decoder();
        decoder.SetDecoderProperties(src.Slice(0, 5).ToArray());
        using var input = new MemoryStream(src.Slice(5).ToArray());
        using var output = new MemoryStream(dst.Length);
        decoder.Code(input, output, input.Length, dst.Length, null);
        output.GetBuffer().AsSpan(0, dst.Length).CopyTo(dst);
    }
}
