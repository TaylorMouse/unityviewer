namespace UnityBrowser.Unity;

public sealed class TypeTreeNode
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public int ByteSize { get; init; }
    public int Index { get; init; }
    public int TypeFlags { get; init; }
    public int Version { get; init; }
    public int MetaFlag { get; init; }
    public int Level { get; init; }

    public bool AlignAfter => (MetaFlag & 0x4000) != 0;
    public override string ToString() => $"{new string(' ', Level * 2)}{Type} {Name}";
}

public sealed class SerializedType
{
    public int ClassId { get; init; }
    public bool IsStripped { get; init; }
    public short ScriptTypeIndex { get; init; } = -1;
    public byte[]? ScriptId { get; init; }
    public byte[]? OldTypeHash { get; init; }
    public List<TypeTreeNode> Nodes { get; } = new();

    public string TypeName => Nodes.Count > 0 ? Nodes[0].Type : ClassIds.Name(ClassId);
}

public sealed class ObjectInfo
{
    public long PathId { get; init; }
    /// <summary>Absolute offset into <see cref="SerializedFile.Data"/>.</summary>
    public long ByteStart { get; init; }
    public uint ByteSize { get; init; }
    public int TypeIndex { get; init; }
    public int ClassId { get; init; }
    public SerializedType? Type { get; init; }

    public string TypeName => Type?.TypeName ?? ClassIds.Name(ClassId);
}

public sealed record FileIdentifier(string TempEmpty, Guid Guid, int Type, string PathName);

/// <summary>
/// Unity SerializedFile (the CAB-xxxx entries in bundles, or .assets files).
/// Parses the metadata only; objects are read on demand.
/// </summary>
public sealed class SerializedFile
{
    public string Name { get; init; } = "";
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public long BaseOffset { get; init; }
    public long Length { get; init; }

    /// <summary>The disk file this came from; used to resolve streamed resources (.resS).</summary>
    public LoadedFile? Owner { get; set; }

    public uint Version { get; private set; }
    public long MetadataSize { get; private set; }
    public long FileSize { get; private set; }
    public long DataOffset { get; private set; }
    public bool BigEndian { get; private set; }
    public string UnityVersion { get; private set; } = "";
    public int TargetPlatform { get; private set; }
    public bool EnableTypeTree { get; private set; } = true;
    public List<SerializedType> Types { get; } = new();
    public List<ObjectInfo> Objects { get; } = new();
    public List<FileIdentifier> Externals { get; } = new();

    /// <summary>Quick plausibility check of the big-endian header.</summary>
    public static bool LooksLikeSerializedFile(byte[] data, long offset, long length)
    {
        if (length < 20) return false;
        try
        {
            var r = new EndianReader(data, true, offset);
            long metadataSize = r.U32();
            long fileSize = r.U32();
            uint version = r.U32();
            long dataOffset = r.U32();
            if (version is < 5 or > 50) return false;
            if (version >= 22)
            {
                r.Skip(4);
                metadataSize = r.U32();
                fileSize = r.I64();
                dataOffset = r.I64();
            }
            return fileSize <= length && fileSize > 0 && dataOffset <= fileSize && metadataSize < fileSize;
        }
        catch
        {
            return false;
        }
    }

    public static SerializedFile Parse(string name, byte[] data, long offset, long length)
    {
        var sf = new SerializedFile { Name = name, Data = data, BaseOffset = offset, Length = length };
        sf.ParseMetadata();
        return sf;
    }

    public EndianReader CreateReader(long position) => new(Data, BigEndian, position, BaseOffset);

    private void ParseMetadata()
    {
        var r = new EndianReader(Data, true, BaseOffset, BaseOffset);
        MetadataSize = r.U32();
        FileSize = r.U32();
        Version = r.U32();
        DataOffset = r.U32();

        byte endian;
        if (Version >= 9)
        {
            endian = r.U8();
            r.Skip(3);
        }
        else
        {
            r.Position = BaseOffset + FileSize - MetadataSize;
            endian = r.U8();
        }

        if (Version >= 22)
        {
            MetadataSize = r.U32();
            FileSize = r.I64();
            DataOffset = r.I64();
            r.Skip(8);
        }

        BigEndian = endian != 0;
        r.BigEndian = BigEndian;

        if (Version >= 7) UnityVersion = r.CString(256);
        if (Version >= 8) TargetPlatform = r.I32();
        if (Version >= 13) EnableTypeTree = r.Bool();

        int typeCount = r.I32();
        for (int i = 0; i < typeCount; i++)
            Types.Add(ReadSerializedType(r, isRefType: false));

        bool bigIdEnabled = false;
        if (Version is >= 7 and < 14) bigIdEnabled = r.I32() != 0;

        int objectCount = r.I32();
        for (int i = 0; i < objectCount; i++)
        {
            long pathId;
            if (bigIdEnabled) pathId = r.I64();
            else if (Version < 14) pathId = r.I32();
            else
            {
                r.Align(4);
                pathId = r.I64();
            }

            long byteStart = Version >= 22 ? r.I64() : r.U32();
            byteStart += DataOffset;
            uint byteSize = r.U32();
            int typeId = r.I32();

            int classId;
            SerializedType? type = null;
            if (Version < 16)
            {
                classId = r.U16();
                type = Types.FirstOrDefault(t => t.ClassId == typeId);
            }
            else
            {
                type = typeId >= 0 && typeId < Types.Count ? Types[typeId] : null;
                classId = type?.ClassId ?? -1;
            }
            if (Version < 11) r.Skip(2);                     // isDestroyed
            if (Version is >= 11 and < 17) r.Skip(2);         // scriptTypeIndex
            if (Version is 15 or 16) r.Skip(1);               // stripped

            Objects.Add(new ObjectInfo
            {
                PathId = pathId,
                ByteStart = BaseOffset + byteStart,
                ByteSize = byteSize,
                TypeIndex = typeId,
                ClassId = classId,
                Type = type,
            });
        }

        // Script types and externals are useful but not essential; don't fail the file over them.
        try
        {
            if (Version >= 11)
            {
                int scriptCount = r.I32();
                for (int i = 0; i < scriptCount; i++)
                {
                    r.I32();
                    if (Version < 14) r.I32();
                    else
                    {
                        r.Align(4);
                        r.I64();
                    }
                }
            }

            int externalCount = r.I32();
            for (int i = 0; i < externalCount; i++)
            {
                string tempEmpty = Version >= 6 ? r.CString() : "";
                Guid guid = Guid.Empty;
                int type = 0;
                if (Version >= 5)
                {
                    guid = new Guid(r.Bytes(16));
                    type = r.I32();
                }
                Externals.Add(new FileIdentifier(tempEmpty, guid, type, r.CString()));
            }
        }
        catch
        {
            // ignore
        }
    }

    private SerializedType ReadSerializedType(EndianReader r, bool isRefType)
    {
        int classId = r.I32();
        bool stripped = Version >= 16 && r.Bool();
        short scriptTypeIndex = Version >= 17 ? r.I16() : (short)-1;
        byte[]? scriptId = null, oldTypeHash = null;
        if (Version >= 13)
        {
            if ((isRefType && scriptTypeIndex >= 0) || (Version < 16 && classId < 0) || (Version >= 16 && classId == 114))
                scriptId = r.Bytes(16);
            oldTypeHash = r.Bytes(16);
        }

        var type = new SerializedType
        {
            ClassId = classId,
            IsStripped = stripped,
            ScriptTypeIndex = scriptTypeIndex,
            ScriptId = scriptId,
            OldTypeHash = oldTypeHash,
        };

        if (EnableTypeTree)
        {
            if (Version >= 12 || Version == 10) ReadTypeTreeBlob(r, type.Nodes);
            else ReadTypeTreeLegacy(r, type.Nodes, 0);

            if (Version >= 21)
            {
                if (isRefType)
                {
                    r.CString(); r.CString(); r.CString();
                }
                else
                {
                    int depCount = r.I32();
                    r.Skip(depCount * 4L);
                }
            }
        }
        return type;
    }

    private void ReadTypeTreeBlob(EndianReader r, List<TypeTreeNode> nodes)
    {
        int nodeCount = r.I32();
        int stringSize = r.I32();

        var raw = new (ushort ver, byte level, byte flags, uint typeOff, uint nameOff, int size, int index, int meta)[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            raw[i] = (r.U16(), r.U8(), r.U8(), r.U32(), r.U32(), r.I32(), r.I32(), r.I32());
            if (Version >= 19) r.Skip(8); // refTypeHash
        }
        var strings = r.Bytes(stringSize);

        foreach (var n in raw)
        {
            nodes.Add(new TypeTreeNode
            {
                Version = n.ver,
                Level = n.level,
                TypeFlags = n.flags,
                Type = LookupString(strings, n.typeOff),
                Name = LookupString(strings, n.nameOff),
                ByteSize = n.size,
                Index = n.index,
                MetaFlag = n.meta,
            });
        }
    }

    private static string LookupString(byte[] local, uint offset)
    {
        if ((offset & 0x80000000) != 0) return CommonStrings.Get(offset & 0x7FFFFFFF);
        if (offset >= local.Length) return $"<str 0x{offset:X}>";
        int end = Array.IndexOf(local, (byte)0, (int)offset);
        if (end < 0) end = local.Length;
        return System.Text.Encoding.UTF8.GetString(local, (int)offset, end - (int)offset);
    }

    private void ReadTypeTreeLegacy(EndianReader r, List<TypeTreeNode> nodes, int level)
    {
        string type = r.CString();
        string name = r.CString();
        int byteSize = r.I32();
        if (Version == 2) r.I32(); // variableCount
        int index = Version != 3 ? r.I32() : 0;
        int typeFlags = r.I32();
        int version = r.I32();
        int metaFlag = Version != 3 ? r.I32() : 0;
        nodes.Add(new TypeTreeNode
        {
            Type = type, Name = name, ByteSize = byteSize, Index = index,
            TypeFlags = typeFlags, Version = version, MetaFlag = metaFlag, Level = level,
        });
        int childCount = r.I32();
        for (int i = 0; i < childCount; i++) ReadTypeTreeLegacy(r, nodes, level + 1);
    }
}
