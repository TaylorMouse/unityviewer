using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace UnityBrowser.Unity;

/// <summary>A decoded image: top-down rows, BGRA8 (ready for WPF's Bgra32).</summary>
public sealed class DecodedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }
    public List<PropertyInfo> Info { get; } = new();
}

public sealed record PropertyInfo(string Key, string Value);

public static class TextureDecoder
{
    public static string FormatName(int format) => format switch
    {
        1 => "Alpha8", 2 => "ARGB4444", 3 => "RGB24", 4 => "RGBA32", 5 => "ARGB32", 7 => "RGB565",
        9 => "R16", 10 => "DXT1", 11 => "DXT3", 12 => "DXT5", 13 => "RGBA4444", 14 => "BGRA32",
        15 => "RHalf", 16 => "RGHalf", 17 => "RGBAHalf", 18 => "RFloat", 19 => "RGFloat", 20 => "RGBAFloat",
        22 => "RGB9e5Float", 24 => "BC6H", 25 => "BC7", 26 => "BC4", 27 => "BC5",
        28 => "DXT1Crunched", 29 => "DXT5Crunched", 30 => "PVRTC_RGB2", 31 => "PVRTC_RGBA2",
        32 => "PVRTC_RGB4", 33 => "PVRTC_RGBA4", 34 => "ETC_RGB4", 41 => "EAC_R", 42 => "EAC_R_SIGNED",
        43 => "EAC_RG", 44 => "EAC_RG_SIGNED", 45 => "ETC2_RGB", 46 => "ETC2_RGBA1", 47 => "ETC2_RGBA8",
        48 => "ASTC_4x4", 49 => "ASTC_5x5", 50 => "ASTC_6x6", 51 => "ASTC_8x8", 52 => "ASTC_10x10",
        53 => "ASTC_12x12", 62 => "RG16", 63 => "R8", 64 => "ETC_RGB4Crunched", 65 => "ETC2_RGBA8Crunched",
        72 => "RG32", 73 => "RGB48", 74 => "RGBA64",
        _ => $"Format {format}",
    };

    /// <summary>Bytes needed for the top mip level, or -1 if the format is unknown.</summary>
    public static long Mip0Size(int format, int w, int h)
    {
        long blocks = (long)((w + 3) / 4) * ((h + 3) / 4);
        return format switch
        {
            10 or 26 => blocks * 8,
            11 or 12 or 24 or 25 or 27 => blocks * 16,
            1 or 63 => (long)w * h,
            2 or 7 or 9 or 13 or 15 or 62 => (long)w * h * 2,
            3 => (long)w * h * 3,
            4 or 5 or 14 or 16 or 18 or 22 or 72 => (long)w * h * 4,
            17 or 19 or 74 => (long)w * h * 8,
            73 => (long)w * h * 6,
            20 => (long)w * h * 16,
            _ => -1,
        };
    }

    /// <summary>Decodes the top mip of a Unity texture. Unity stores rows bottom-up; the result is top-down.</summary>
    public static byte[] Decode(byte[] data, int format, int w, int h)
    {
        long need = Mip0Size(format, w, h);
        if (need < 0) throw new NotSupportedException($"{FormatName(format)} textures are not supported yet.");
        if (data.Length < need)
            throw new InvalidDataException($"Image data is {data.Length} bytes, {FormatName(format)} {w}x{h} needs {need}.");

        byte[] rgba = format switch
        {
            10 => Bcn(data, need, w, h, CompressionFormat.Bc1WithAlpha),
            11 => Bcn(data, need, w, h, CompressionFormat.Bc2),
            12 => Bcn(data, need, w, h, CompressionFormat.Bc3),
            26 => Grey(Bcn(data, need, w, h, CompressionFormat.Bc4)),
            27 => Bcn(data, need, w, h, CompressionFormat.Bc5),
            25 => Bcn(data, need, w, h, CompressionFormat.Bc7),
            24 => Bc6(data, need, w, h),
            _ => DecodeUncompressed(data, format, w, h),
        };

        return FlipToBgra(rgba, w, h);
    }

    private static byte[] Bcn(byte[] data, long size, int w, int h, CompressionFormat fmt)
    {
        var input = data.Length == size ? data : data.AsSpan(0, (int)size).ToArray();
        ColorRgba32[] pixels = new BcDecoder().DecodeRaw(input, w, h, fmt);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }
        return rgba;
    }

    private static byte[] Bc6(byte[] data, long size, int w, int h)
    {
        var input = data.AsSpan(0, (int)size).ToArray();
        ColorRgbFloat[] pixels = new BcDecoder().DecodeRawHdr(input, w, h, CompressionFormat.Bc6U);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = ToneMap(pixels[i].r);
            rgba[i * 4 + 1] = ToneMap(pixels[i].g);
            rgba[i * 4 + 2] = ToneMap(pixels[i].b);
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private static byte ToneMap(float v) => (byte)Math.Clamp(Math.Pow(v / (1 + v), 1 / 2.2) * 255 + 0.5, 0, 255);
    private static byte Unorm(float v) => (byte)Math.Clamp(v * 255 + 0.5f, 0, 255);

    /// <summary>Single-channel formats: show R as grey, opaque.</summary>
    private static byte[] Grey(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 1] = rgba[i + 2] = rgba[i];
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeUncompressed(byte[] d, int format, int w, int h)
    {
        int n = w * h;
        var o = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            byte r, g, b, a = 255;
            switch (format)
            {
                case 1: r = g = b = d[i]; break;                                    // Alpha8 shown as grey
                case 63: r = g = b = d[i]; break;                                   // R8
                case 2:                                                             // ARGB4444
                {
                    int v = d[i * 2] | (d[i * 2 + 1] << 8);
                    a = (byte)(((v >> 12) & 15) * 17); r = (byte)(((v >> 8) & 15) * 17);
                    g = (byte)(((v >> 4) & 15) * 17); b = (byte)((v & 15) * 17);
                    break;
                }
                case 13:                                                            // RGBA4444
                {
                    int v = d[i * 2] | (d[i * 2 + 1] << 8);
                    r = (byte)(((v >> 12) & 15) * 17); g = (byte)(((v >> 8) & 15) * 17);
                    b = (byte)(((v >> 4) & 15) * 17); a = (byte)((v & 15) * 17);
                    break;
                }
                case 7:                                                             // RGB565
                {
                    int v = d[i * 2] | (d[i * 2 + 1] << 8);
                    r = (byte)(((v >> 11) & 31) * 255 / 31); g = (byte)(((v >> 5) & 63) * 255 / 63);
                    b = (byte)((v & 31) * 255 / 31);
                    break;
                }
                case 9: r = g = b = d[i * 2 + 1]; break;                           // R16 (high byte)
                case 62: r = d[i * 2]; g = d[i * 2 + 1]; b = 0; break;             // RG16
                case 3: r = d[i * 3]; g = d[i * 3 + 1]; b = d[i * 3 + 2]; break;   // RGB24
                case 4: r = d[i * 4]; g = d[i * 4 + 1]; b = d[i * 4 + 2]; a = d[i * 4 + 3]; break;
                case 5: a = d[i * 4]; r = d[i * 4 + 1]; g = d[i * 4 + 2]; b = d[i * 4 + 3]; break;
                case 14: b = d[i * 4]; g = d[i * 4 + 1]; r = d[i * 4 + 2]; a = d[i * 4 + 3]; break;
                case 72: r = d[i * 4 + 1]; g = d[i * 4 + 3]; b = 0; break;         // RG32 (high bytes)
                case 73: r = d[i * 6 + 1]; g = d[i * 6 + 3]; b = d[i * 6 + 5]; break;
                case 74: r = d[i * 8 + 1]; g = d[i * 8 + 3]; b = d[i * 8 + 5]; a = d[i * 8 + 7]; break;
                case 15: r = g = b = Unorm((float)BitConverter.ToHalf(d, i * 2)); break;
                case 16:
                    r = Unorm((float)BitConverter.ToHalf(d, i * 4)); g = Unorm((float)BitConverter.ToHalf(d, i * 4 + 2)); b = 0;
                    break;
                case 17:
                    r = Unorm((float)BitConverter.ToHalf(d, i * 8)); g = Unorm((float)BitConverter.ToHalf(d, i * 8 + 2));
                    b = Unorm((float)BitConverter.ToHalf(d, i * 8 + 4)); a = Unorm((float)BitConverter.ToHalf(d, i * 8 + 6));
                    break;
                case 18: r = g = b = Unorm(BitConverter.ToSingle(d, i * 4)); break;
                case 19: r = Unorm(BitConverter.ToSingle(d, i * 8)); g = Unorm(BitConverter.ToSingle(d, i * 8 + 4)); b = 0; break;
                case 20:
                    r = Unorm(BitConverter.ToSingle(d, i * 16)); g = Unorm(BitConverter.ToSingle(d, i * 16 + 4));
                    b = Unorm(BitConverter.ToSingle(d, i * 16 + 8)); a = Unorm(BitConverter.ToSingle(d, i * 16 + 12));
                    break;
                case 22:                                                            // RGB9e5
                {
                    uint v = BitConverter.ToUInt32(d, i * 4);
                    double scale = Math.Pow(2, (int)(v >> 27) - 15 - 9);
                    r = Unorm((float)((v & 0x1FF) * scale));
                    g = Unorm((float)(((v >> 9) & 0x1FF) * scale));
                    b = Unorm((float)(((v >> 18) & 0x1FF) * scale));
                    break;
                }
                default:
                    throw new NotSupportedException($"{FormatName(format)} textures are not supported yet.");
            }
            o[i * 4] = r; o[i * 4 + 1] = g; o[i * 4 + 2] = b; o[i * 4 + 3] = a;
        }
        return o;
    }

    /// <summary>RGBA bottom-up to BGRA top-down.</summary>
    private static byte[] FlipToBgra(byte[] rgba, int w, int h)
    {
        var bgra = new byte[rgba.Length];
        int stride = w * 4;
        for (int y = 0; y < h; y++)
        {
            int src = (h - 1 - y) * stride;
            int dst = y * stride;
            for (int x = 0; x < stride; x += 4)
            {
                bgra[dst + x] = rgba[src + x + 2];
                bgra[dst + x + 1] = rgba[src + x + 1];
                bgra[dst + x + 2] = rgba[src + x];
                bgra[dst + x + 3] = rgba[src + x + 3];
            }
        }
        return bgra;
    }
}
