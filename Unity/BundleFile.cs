namespace UnityBrowser.Unity;

public sealed record StorageBlock(uint UncompressedSize, uint CompressedSize, ushort Flags)
{
    public uint CompressionType => Flags & 0x3Fu;
}

public sealed record BundleNode(long Offset, long Size, uint Flags, string Path)
{
    public bool IsSerializedFile => (Flags & 0x4) != 0;
}

/// <summary>UnityFS asset bundle. All blocks are decompressed into <see cref="Data"/> on load.</summary>
public sealed class BundleFile
{
    public const uint FlagBlocksInfoAtEnd = 0x80;
    public const uint FlagBlockInfoPadding = 0x200;

    public string Signature { get; private set; } = "";
    public uint Version { get; private set; }
    public string UnityVersion { get; private set; } = "";
    public string UnityRevision { get; private set; } = "";
    public long Size { get; private set; }
    public uint CompressedBlocksInfoSize { get; private set; }
    public uint UncompressedBlocksInfoSize { get; private set; }
    public uint Flags { get; private set; }
    public List<StorageBlock> Blocks { get; } = new();
    public List<BundleNode> Nodes { get; } = new();
    public byte[] Data { get; private set; } = Array.Empty<byte>();

    public uint BlocksInfoCompression => Flags & 0x3Fu;

    public static bool IsUnityFS(ReadOnlySpan<byte> file) =>
        file.Length >= 8 && file.Slice(0, 8).SequenceEqual("UnityFS\0"u8);

    public static BundleFile Load(byte[] file)
    {
        var b = new BundleFile();
        var r = new EndianReader(file, bigEndian: true);

        b.Signature = r.CString(64);
        if (b.Signature != "UnityFS")
            throw new NotSupportedException($"Unsupported bundle signature '{b.Signature}'.");

        b.Version = r.U32();
        b.UnityVersion = r.CString(256);
        b.UnityRevision = r.CString(256);
        b.Size = r.I64();
        b.CompressedBlocksInfoSize = r.U32();
        b.UncompressedBlocksInfoSize = r.U32();
        b.Flags = r.U32();

        if (b.Version >= 7) r.Align(16);

        bool atEnd = (b.Flags & FlagBlocksInfoAtEnd) != 0;
        long infoPos = atEnd ? file.Length - b.CompressedBlocksInfoSize : r.Position;
        long dataPos = atEnd ? r.Position : infoPos + b.CompressedBlocksInfoSize;
        if ((b.Flags & FlagBlockInfoPadding) != 0) dataPos = (dataPos + 15) & ~15L;

        var info = new byte[b.UncompressedBlocksInfoSize];
        Compression.Decompress(b.BlocksInfoCompression,
            file.AsSpan((int)infoPos, (int)b.CompressedBlocksInfoSize), info);

        var ir = new EndianReader(info, bigEndian: true);
        ir.Skip(16); // uncompressed data hash
        int blockCount = ir.I32();
        for (int i = 0; i < blockCount; i++)
            b.Blocks.Add(new StorageBlock(ir.U32(), ir.U32(), ir.U16()));
        int nodeCount = ir.I32();
        for (int i = 0; i < nodeCount; i++)
            b.Nodes.Add(new BundleNode(ir.I64(), ir.I64(), ir.U32(), ir.CString()));

        long total = b.Blocks.Sum(x => (long)x.UncompressedSize);
        if (total > Array.MaxLength) throw new NotSupportedException("Bundle is larger than 2 GB uncompressed.");
        var data = new byte[total];
        long src = dataPos, dst = 0;
        foreach (var block in b.Blocks)
        {
            if (src + block.CompressedSize > file.Length)
                throw new InvalidDataException("Bundle block runs past end of file (truncated or encrypted?).");
            Compression.Decompress(block.CompressionType,
                file.AsSpan((int)src, (int)block.CompressedSize),
                data.AsSpan((int)dst, (int)block.UncompressedSize));
            src += block.CompressedSize;
            dst += block.UncompressedSize;
        }
        b.Data = data;
        return b;
    }
}
