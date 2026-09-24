namespace UnityBrowser.Unity;

/// <summary>One file from disk: either a UnityFS bundle or a bare serialised file.</summary>
public sealed class LoadedFile
{
    public string Path { get; private init; } = "";
    public long DiskSize { get; private init; }
    public BundleFile? Bundle { get; private init; }
    public List<SerializedFile> SerializedFiles { get; } = new();

    public static LoadedFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (BundleFile.IsUnityFS(bytes))
        {
            var bundle = BundleFile.Load(bytes);
            var lf = new LoadedFile { Path = path, DiskSize = bytes.Length, Bundle = bundle };
            foreach (var node in bundle.Nodes)
            {
                bool candidate = node.IsSerializedFile ||
                                 !(node.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ||
                                   node.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
                if (candidate && SerializedFile.LooksLikeSerializedFile(bundle.Data, node.Offset, node.Size))
                {
                    var sf = SerializedFile.Parse(node.Path, bundle.Data, node.Offset, node.Size);
                    sf.Owner = lf;
                    lf.SerializedFiles.Add(sf);
                }
            }
            return lf;
        }

        if (SerializedFile.LooksLikeSerializedFile(bytes, 0, bytes.Length))
        {
            var lf = new LoadedFile { Path = path, DiskSize = bytes.Length };
            var sf = SerializedFile.Parse(System.IO.Path.GetFileName(path), bytes, 0, bytes.Length);
            sf.Owner = lf;
            lf.SerializedFiles.Add(sf);
            return lf;
        }

        var sig = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(8, bytes.Length)).TrimEnd('\0');
        throw new NotSupportedException($"Not a UnityFS bundle or serialised file (starts with '{sig}').");
    }

    /// <summary>
    /// Reads a range from a streamed resource. <paramref name="path"/> is Unity's reference,
    /// e.g. "archive:/CAB-xxxx/CAB-xxxx.resS" inside a bundle, or "sharedassets0.assets.resS" on disk.
    /// </summary>
    public byte[] ReadResource(string path, long offset, long size)
    {
        string fileName = path.Replace('\\', '/').Split('/').Last();

        if (Bundle != null)
        {
            var node = Bundle.Nodes.FirstOrDefault(n => n.Path.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new FileNotFoundException($"Resource '{fileName}' is not in this bundle.");
            if (offset < 0 || offset + size > node.Size)
                throw new InvalidDataException($"Range {offset}+{size} is outside '{fileName}' ({node.Size} bytes).");
            return Bundle.Data.AsSpan((int)(node.Offset + offset), (int)size).ToArray();
        }

        string diskPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", fileName);
        if (!File.Exists(diskPath)) throw new FileNotFoundException($"Resource file '{fileName}' not found next to the asset file.");
        using var fs = File.OpenRead(diskPath);
        fs.Position = offset;
        var buffer = new byte[size];
        fs.ReadExactly(buffer);
        return buffer;
    }
}
