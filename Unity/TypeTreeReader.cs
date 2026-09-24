namespace UnityBrowser.Unity;

/// <summary>A decoded field from an object, driven by its type tree.</summary>
public sealed class FieldValue
{
    public FieldValue(string name, string type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }
    public string Type { get; }
    public object? Value { get; set; }
    public List<FieldValue> Children { get; } = new();

    public FieldValue? this[string name] => Children.FirstOrDefault(c => c.Name == name);

    public override string ToString() => Value is null ? $"{Type} {Name}" : $"{Type} {Name} = {Value}";
}

/// <summary>A run of raw bytes inside the reader's buffer (e.g. TypelessData).</summary>
public sealed record DataRange(long Offset, int Length)
{
    public override string ToString() => $"({Length} bytes)";
}

/// <summary>A primitive array left in place in the buffer (e.g. a mesh index buffer).</summary>
public sealed record PrimitiveArray(long Offset, int Count, string ElementType, int ElementSize)
{
    public int ByteLength => Count * ElementSize;
    public override string ToString() => $"[{Count}]";
}

/// <summary>Reads object data using the serialised type tree.</summary>
public static class TypeTreeReader
{
    /// <summary>Primitive arrays longer than this are skipped rather than stored element by element.</summary>
    public const int MaxStoredPrimitiveElements = 256;

    public static FieldValue Read(List<TypeTreeNode> nodes, EndianReader r)
    {
        int i = 0;
        return ReadField(nodes, ref i, r, keep: true)!;
    }

    /// <summary>Walks the top-level fields until m_Name is found. Returns null if the type has none.</summary>
    public static string? ReadName(List<TypeTreeNode> nodes, EndianReader r)
    {
        if (nodes.Count == 0) return null;
        int end = SubtreeEnd(nodes, 0);
        int i = 1;
        while (i < end)
        {
            var node = nodes[i];
            if (node.Level == 1 && node.Name == "m_Name" && node.Type == "string")
                return r.AlignedString(4096);
            ReadField(nodes, ref i, r, keep: false);
        }
        return null;
    }

    private static int SubtreeEnd(List<TypeTreeNode> nodes, int index)
    {
        int level = nodes[index].Level;
        int j = index + 1;
        while (j < nodes.Count && nodes[j].Level > level) j++;
        return j;
    }

    public static int PrimitiveSize(string type) => type switch
    {
        "bool" or "UInt8" or "SInt8" or "char" => 1,
        "SInt16" or "UInt16" or "short" or "unsigned short" => 2,
        "int" or "SInt32" or "UInt32" or "unsigned int" or "float" or "Type*" => 4,
        "SInt64" or "UInt64" or "long long" or "unsigned long long" or "double" or "FileSize" => 8,
        _ => 0,
    };

    private static object ReadPrimitive(string type, EndianReader r) => type switch
    {
        "bool" => r.Bool(),
        "UInt8" => r.U8(),
        "SInt8" => r.I8(),
        "char" => (char)r.U8(),
        "SInt16" or "short" => r.I16(),
        "UInt16" or "unsigned short" => r.U16(),
        "int" or "SInt32" or "Type*" => r.I32(),
        "UInt32" or "unsigned int" => r.U32(),
        "float" => r.F32(),
        "SInt64" or "long long" => r.I64(),
        "UInt64" or "unsigned long long" or "FileSize" => r.U64(),
        "double" => r.F64(),
        _ => throw new InvalidOperationException(type),
    };

    private static FieldValue? ReadField(List<TypeTreeNode> nodes, ref int i, EndianReader r, bool keep)
    {
        var node = nodes[i];
        int end = SubtreeEnd(nodes, i);
        var fv = keep ? new FieldValue(node.Name, node.Type) : null;
        bool align = node.AlignAfter;

        int primSize = end == i + 1 ? PrimitiveSize(node.Type) : 0;
        if (primSize > 0)
        {
            if (keep) fv!.Value = ReadPrimitive(node.Type, r);
            else r.Skip(primSize);
        }
        else if (node.Type == "string")
        {
            var s = r.AlignedString();
            if (keep) fv!.Value = s;
        }
        else if (node.Type == "TypelessData")
        {
            int len = r.I32();
            if (keep) fv!.Value = new DataRange(r.Position, len);
            r.Skip(len);
        }
        else if (node.Type == "Array" || (node.TypeFlags & 1) != 0)
        {
            ReadArray(nodes, i, r, keep, fv);
        }
        else if (i + 1 < end && nodes[i + 1].Type == "Array")
        {
            // vector / map / set / staticvector: a wrapper holding a single Array child.
            ReadArray(nodes, i + 1, r, keep, fv);
            if (nodes[i + 1].AlignAfter) r.Align(4);
        }
        else
        {
            int j = i + 1;
            while (j < end)
            {
                var child = ReadField(nodes, ref j, r, keep);
                if (child != null) fv!.Children.Add(child);
            }
        }

        if (align) r.Align(4);
        i = end;
        return fv;
    }

    /// <summary>Array node layout: Array { int size; T data; }.</summary>
    private static void ReadArray(List<TypeTreeNode> nodes, int arrayIndex, EndianReader r, bool keep, FieldValue? fv)
    {
        int size = r.I32();
        if (size < 0) throw new InvalidDataException($"Negative array size at 0x{r.Position - 4:X}.");
        int dataIndex = arrayIndex + 2;
        var elem = nodes[dataIndex];
        int elemEnd = SubtreeEnd(nodes, dataIndex);
        int primSize = elemEnd == dataIndex + 1 && !elem.AlignAfter ? PrimitiveSize(elem.Type) : 0;

        if (fv != null) fv.Value = $"[{size}]";

        if (primSize > 0)
        {
            // Primitive arrays always record where their data lives; small ones are also expanded.
            if (fv != null) fv.Value = new PrimitiveArray(r.Position, size, elem.Type, primSize);
            if (!keep || size > MaxStoredPrimitiveElements)
            {
                r.Skip((long)size * primSize);
                return;
            }
        }

        for (int k = 0; k < size; k++)
        {
            int j = dataIndex;
            var child = ReadField(nodes, ref j, r, keep);
            if (child != null) fv!.Children.Add(child);
        }
    }
}
