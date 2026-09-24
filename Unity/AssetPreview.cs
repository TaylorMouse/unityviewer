namespace UnityBrowser.Unity;

/// <summary>A texture's raw encoded data, as stored by Unity (bottom-up rows, mips largest first).</summary>
public sealed class TextureData
{
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Format { get; init; }
    public int MipCount { get; init; }
    public int ImageCount { get; init; }
    public int ColorSpace { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string Source { get; init; } = "";
}

/// <summary>Builds image previews for Texture2D and Sprite objects.</summary>
public static class AssetPreview
{
    private sealed record CacheKey(SerializedFile File, long PathId);

    // Sprites usually share an atlas; keep the last few decoded textures around.
    private static readonly LinkedList<(CacheKey Key, DecodedImage Image)> Cache = new();
    private const int CacheSize = 6;

    public static bool CanPreview(ObjectInfo o) => o.TypeName is "Texture2D" or "Sprite";

    public static DecodedImage GetImage(SerializedFile sf, ObjectInfo o) => o.TypeName switch
    {
        "Texture2D" => GetTexture(sf, o),
        "Sprite" => GetSprite(sf, o),
        _ => throw new NotSupportedException($"No preview for {o.TypeName}."),
    };

    public static FieldValue ReadObject(SerializedFile sf, ObjectInfo o)
    {
        if (o.Type is not { Nodes.Count: > 0 } type)
            throw new NotSupportedException("This file has no type trees, so the object layout is unknown.");
        return TypeTreeReader.Read(type.Nodes, sf.CreateReader(o.ByteStart));
    }

    /// <summary>Reads a texture's header fields and raw (still encoded) pixel data, all mips.</summary>
    public static TextureData ReadTextureData(SerializedFile sf, ObjectInfo o)
    {
        var root = ReadObject(sf, o);
        int width = Convert.ToInt32(root["m_Width"]?.Value);
        int height = Convert.ToInt32(root["m_Height"]?.Value);
        if (width <= 0 || height <= 0) throw new InvalidDataException($"Texture is {width}x{height}.");

        var stream = root["m_StreamData"];
        long streamSize = Convert.ToInt64(stream?["size"]?.Value ?? 0u);
        string source;
        byte[] data;
        if (streamSize > 0)
        {
            long offset = Convert.ToInt64(stream!["offset"]?.Value ?? 0ul);
            string path = stream["path"]?.Value as string ?? "";
            if (sf.Owner == null) throw new InvalidOperationException("No owning file to read the stream from.");
            data = sf.Owner.ReadResource(path, offset, streamSize);
            source = $"{Path.GetFileName(path)} @ 0x{offset:X}";
        }
        else if (root["image data"]?.Value is DataRange range && range.Length > 0)
        {
            data = sf.Data.AsSpan((int)range.Offset, range.Length).ToArray();
            source = "Embedded";
        }
        else
        {
            throw new InvalidDataException("Texture has no image data.");
        }

        return new TextureData
        {
            Name = root["m_Name"]?.Value as string ?? "",
            Width = width,
            Height = height,
            Format = Convert.ToInt32(root["m_TextureFormat"]?.Value),
            MipCount = Math.Max(1, Convert.ToInt32(root["m_MipCount"]?.Value ?? 1)),
            ImageCount = Math.Max(1, Convert.ToInt32(root["m_ImageCount"]?.Value ?? 1)),
            ColorSpace = root["m_ColorSpace"]?.Value is int cs ? cs : -1,
            Data = data,
            Source = source,
        };
    }

    private static DecodedImage GetTexture(SerializedFile sf, ObjectInfo o)
    {
        var key = new CacheKey(sf, o.PathId);
        lock (Cache)
        {
            var hit = Cache.FirstOrDefault(c => c.Key == key);
            if (hit.Image != null) return hit.Image;
        }

        var t = ReadTextureData(sf, o);
        var image = new DecodedImage
        {
            Width = t.Width,
            Height = t.Height,
            Bgra = TextureDecoder.Decode(t.Data, t.Format, t.Width, t.Height),
        };
        image.Info.Add(new("Dimensions", $"{t.Width} x {t.Height}"));
        image.Info.Add(new("Format", $"{TextureDecoder.FormatName(t.Format)} ({t.Format})"));
        image.Info.Add(new("Mip levels", t.MipCount.ToString()));
        if (t.ImageCount > 1) image.Info.Add(new("Images", $"{t.ImageCount} (showing the first)"));
        image.Info.Add(new("Pixel data", $"{TreeBuilderSize(t.Data.Length)} ({t.Source})"));
        if (t.ColorSpace >= 0) image.Info.Add(new("Colour space", t.ColorSpace == 1 ? "sRGB" : "Linear"));

        lock (Cache)
        {
            Cache.AddFirst((key, image));
            while (Cache.Count > CacheSize) Cache.RemoveLast();
        }
        return image;
    }

    private static DecodedImage GetSprite(SerializedFile sf, ObjectInfo o)
    {
        var root = ReadObject(sf, o);
        var rd = root["m_RD"] ?? throw new InvalidDataException("Sprite has no render data.");
        var texRef = rd["texture"] ?? throw new InvalidDataException("Sprite has no texture reference.");
        int fileId = Convert.ToInt32(texRef["m_FileID"]?.Value);
        long pathId = Convert.ToInt64(texRef["m_PathID"]?.Value);
        if (pathId == 0) throw new InvalidDataException("Sprite has no texture.");

        var texFile = ResolveFile(sf, fileId);
        var texObj = texFile.Objects.FirstOrDefault(x => x.PathId == pathId)
                     ?? throw new InvalidDataException($"Texture {pathId} not found in {texFile.Name}.");
        var tex = GetTexture(texFile, texObj);

        var rect = rd["textureRect"];
        if (rect == null || Convert.ToSingle(rect["width"]?.Value) <= 0) rect = root["m_Rect"];
        float rx = Convert.ToSingle(rect?["x"]?.Value), ry = Convert.ToSingle(rect?["y"]?.Value);
        float rw = Convert.ToSingle(rect?["width"]?.Value), rh = Convert.ToSingle(rect?["height"]?.Value);

        // Unity rects have a bottom-left origin; the decoded image is top-down.
        int x0 = Math.Clamp((int)MathF.Floor(rx), 0, tex.Width);
        int w = Math.Clamp((int)MathF.Round(rw), 1, tex.Width - x0);
        int h = Math.Clamp((int)MathF.Round(rh), 1, tex.Height);
        int y0 = Math.Clamp(tex.Height - (int)MathF.Floor(ry) - h, 0, tex.Height - h);

        var crop = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(tex.Bgra, ((y0 + y) * tex.Width + x0) * 4, crop, y * w * 4, w * 4);

        var image = new DecodedImage { Width = w, Height = h, Bgra = crop };
        image.Info.Add(new("Dimensions", $"{w} x {h}"));
        image.Info.Add(new("Texture", ObjectNames.GetName(texFile, texObj) ?? $"#{pathId}"));
        image.Info.Add(new("Texture rect", $"x {rx}, y {ry}, {rw} x {rh}"));
        foreach (var info in tex.Info.Where(i => i.Key is "Format" or "Dimensions"))
            image.Info.Add(new($"Texture {info.Key.ToLowerInvariant()}", info.Value));
        return image;
    }

    /// <summary>fileID 0 is the same file; others index the externals table (1-based).</summary>
    private static SerializedFile ResolveFile(SerializedFile sf, int fileId)
    {
        if (fileId == 0) return sf;
        if (fileId < 0 || fileId > sf.Externals.Count) throw new InvalidDataException($"Bad file ID {fileId}.");
        string name = Path.GetFileName(sf.Externals[fileId - 1].PathName.Replace('\\', '/'));
        return sf.Owner?.SerializedFiles.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new FileNotFoundException($"Texture is in another file ({name}), which is not loaded.");
    }

    private static string TreeBuilderSize(long bytes) =>
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";
}
