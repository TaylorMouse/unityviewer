using System.Buffers.Binary;

namespace UnityBrowser.Unity;

public sealed class SubMeshData
{
    /// <summary>Triangle list, three vertex indices per triangle.</summary>
    public required int[] Triangles { get; init; }
}

/// <summary>
/// A Unity mesh converted to a right-handed coordinate system (X mirrored, winding reversed),
/// which is what WPF 3D, OBJ and COLLADA expect.
/// </summary>
public sealed class MeshData
{
    public string Name { get; init; } = "";
    public int VertexCount { get; init; }
    public float[] Positions { get; init; } = Array.Empty<float>();   // xyz
    public float[]? Normals { get; init; }                             // xyz
    public float[]? Uv0 { get; init; }                                 // uv
    public float[]? Colors { get; init; }                              // rgba
    public bool IsSkinned { get; init; }
    public int BoneCount { get; init; }
    /// <summary>Up to 4 influences per vertex: bone slot indices and weights (weight 0 = unused).</summary>
    public int[]? BoneIndices { get; init; }
    public float[]? BoneWeights { get; init; }
    /// <summary>Inverse bind matrices per bone slot, row-major, already converted to right-handed space.</summary>
    public List<double[]> BindPoses { get; } = new();
    /// <summary>Per bone slot: CRC32 of the bone's path from the rig root (matches Avatar m_TOS keys).</summary>
    public uint[] BoneNameHashes { get; init; } = Array.Empty<uint>();
    public List<SubMeshData> SubMeshes { get; } = new();

    public int TriangleCount => SubMeshes.Sum(s => s.Triangles.Length / 3);

    public (float[] Min, float[] Max) Bounds()
    {
        float[] min = { float.MaxValue, float.MaxValue, float.MaxValue };
        float[] max = { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < Positions.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                min[k] = Math.Min(min[k], Positions[i + k]);
                max[k] = Math.Max(max[k], Positions[i + k]);
            }
        if (Positions.Length == 0) min = max = new float[3];
        return (min, max);
    }
}

/// <summary>Reads Mesh objects (Unity 2019+ vertex layout, uncompressed meshes).</summary>
public static class MeshReader
{
    private const int ChannelPosition = 0, ChannelNormal = 1, ChannelColor = 3, ChannelUv0 = 4,
        ChannelBlendWeight = 12, ChannelBlendIndices = 13;

    private sealed record Channel(int Stream, int Offset, int Format, int Dimension);

    public static bool CanRead(ObjectInfo o) => o.TypeName == "Mesh";

    public static MeshData Read(SerializedFile sf, ObjectInfo o)
    {
        var root = AssetPreview.ReadObject(sf, o);
        string name = root["m_Name"]?.Value as string ?? "";

        if (Convert.ToInt32(root["m_MeshCompression"]?.Value ?? 0) != 0)
            throw new NotSupportedException("Compressed meshes are not supported yet.");

        var vd = root["m_VertexData"] ?? throw new InvalidDataException("Mesh has no vertex data.");
        int vertexCount = Convert.ToInt32(vd["m_VertexCount"]?.Value);
        var channels = vd["m_Channels"]!.Children.Select(c => new Channel(
            Convert.ToInt32(c["stream"]?.Value), Convert.ToInt32(c["offset"]?.Value),
            Convert.ToInt32(c["format"]?.Value), Convert.ToInt32(c["dimension"]?.Value) & 0xF)).ToList();
        if (channels.Count < 14)
            throw new NotSupportedException($"Mesh vertex layout with {channels.Count} channels (pre Unity 2019) is not supported yet.");

        byte[] vertexBytes = ReadVertexBytes(sf, root, vd);

        // Streams are stored one after another, each aligned to 16 bytes.
        int streamCount = channels.Where(c => c.Dimension > 0).Select(c => c.Stream).DefaultIfEmpty(0).Max() + 1;
        var streamOffset = new int[streamCount];
        var streamStride = new int[streamCount];
        int offset = 0;
        for (int s = 0; s < streamCount; s++)
        {
            int stride = channels.Where(c => c.Stream == s && c.Dimension > 0).Sum(c => c.Dimension * FormatSize(c.Format));
            streamOffset[s] = offset;
            streamStride[s] = stride;
            offset = (offset + vertexCount * stride + 15) & ~15;
        }

        float[]? ReadChannel(int index)
        {
            var c = channels[index];
            if (c.Dimension == 0) return null;
            int size = FormatSize(c.Format);
            var result = new float[vertexCount * c.Dimension];
            for (int v = 0; v < vertexCount; v++)
            {
                int p = streamOffset[c.Stream] + c.Offset + v * streamStride[c.Stream];
                for (int k = 0; k < c.Dimension; k++)
                    result[v * c.Dimension + k] = ReadComponent(vertexBytes, p + k * size, c.Format);
            }
            return result;
        }

        var rawPos = ReadChannel(ChannelPosition) ?? throw new InvalidDataException("Mesh has no positions.");
        var rawNormals = ReadChannel(ChannelNormal);
        var rawUv = ReadChannel(ChannelUv0);
        var rawColors = ReadChannel(ChannelColor);

        int posDim = channels[ChannelPosition].Dimension;
        var positions = new float[vertexCount * 3];
        for (int v = 0; v < vertexCount; v++)
        {
            positions[v * 3] = -rawPos[v * posDim];            // mirror X: left-handed to right-handed
            positions[v * 3 + 1] = rawPos[v * posDim + 1];
            positions[v * 3 + 2] = rawPos[v * posDim + 2];
        }

        float[]? normals = null;
        if (rawNormals != null)
        {
            int nd = channels[ChannelNormal].Dimension;
            normals = new float[vertexCount * 3];
            for (int v = 0; v < vertexCount; v++)
            {
                normals[v * 3] = -rawNormals[v * nd];
                normals[v * 3 + 1] = rawNormals[v * nd + 1];
                normals[v * 3 + 2] = rawNormals[v * nd + 2];
            }
        }

        float[]? uv = null;
        if (rawUv != null)
        {
            int ud = channels[ChannelUv0].Dimension;
            uv = new float[vertexCount * 2];
            for (int v = 0; v < vertexCount; v++)
            {
                uv[v * 2] = rawUv[v * ud];
                uv[v * 2 + 1] = ud > 1 ? rawUv[v * ud + 1] : 0;
            }
        }

        float[]? colors = null;
        if (rawColors != null && channels[ChannelColor].Dimension == 4) colors = rawColors;

        var bindPoses = root["m_BindPose"]?.Children ?? new List<FieldValue>();
        int boneCount = bindPoses.Count;
        var (boneIndices, boneWeights) = boneCount > 0
            ? ReadSkin(ReadChannel(ChannelBlendWeight), channels[ChannelBlendWeight].Dimension,
                       ReadChannel(ChannelBlendIndices), channels[ChannelBlendIndices].Dimension, vertexCount)
            : (null, null);
        var mesh = new MeshData
        {
            Name = name,
            VertexCount = vertexCount,
            Positions = positions,
            Normals = normals,
            Uv0 = uv,
            Colors = colors,
            BoneCount = boneCount,
            IsSkinned = boneCount > 0 && boneIndices != null,
            BoneIndices = boneIndices,
            BoneWeights = boneWeights,
            BoneNameHashes = ReadHashes(sf, root["m_BoneNameHashes"]),
        };
        foreach (var bp in bindPoses)
        {
            var m = new double[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[r * 4 + c] = Convert.ToDouble(bp[$"e{r}{c}"]?.Value ?? 0f);
            mesh.BindPoses.Add(Mat4.MirrorX(m));
        }

        // Index buffer and sub-meshes.
        var indexArray = root["m_IndexBuffer"]?.Value as PrimitiveArray;
        byte[] indexBytes = indexArray is { Count: > 0 } ? sf.Data.AsSpan((int)indexArray.Offset, indexArray.ByteLength).ToArray() : Array.Empty<byte>();
        bool wide = Convert.ToInt32(root["m_IndexFormat"]?.Value ?? 0) == 1;
        int indexSize = wide ? 4 : 2;
        int IndexAt(int i) => wide
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(i * 4))
            : BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan(i * 2));

        foreach (var sm in root["m_SubMeshes"]!.Children)
        {
            int first = (int)(Convert.ToUInt32(sm["firstByte"]?.Value) / indexSize);
            int count = Convert.ToInt32(sm["indexCount"]?.Value);
            int topology = Convert.ToInt32(sm["topology"]?.Value);
            int baseVertex = Convert.ToInt32(sm["baseVertex"]?.Value);
            if (first + count > indexBytes.Length / indexSize) count = Math.Max(0, indexBytes.Length / indexSize - first);

            var tris = new List<int>(count);
            if (topology == 0) // triangles
            {
                for (int i = 0; i + 2 < count; i += 3)
                    AddTriangle(tris, IndexAt(first + i) + baseVertex, IndexAt(first + i + 1) + baseVertex, IndexAt(first + i + 2) + baseVertex, vertexCount);
            }
            else if (topology == 2) // quads
            {
                for (int i = 0; i + 3 < count; i += 4)
                {
                    int a = IndexAt(first + i) + baseVertex, b = IndexAt(first + i + 1) + baseVertex;
                    int c = IndexAt(first + i + 2) + baseVertex, d = IndexAt(first + i + 3) + baseVertex;
                    AddTriangle(tris, a, b, c, vertexCount);
                    AddTriangle(tris, a, c, d, vertexCount);
                }
            }
            // Lines and points have no surface to show or export.
            mesh.SubMeshes.Add(new SubMeshData { Triangles = tris.ToArray() });
        }
        return mesh;
    }

    private static uint[] ReadHashes(SerializedFile sf, FieldValue? array)
    {
        if (array?.Value is PrimitiveArray a)
            return Enumerable.Range(0, a.Count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(sf.Data.AsSpan((int)a.Offset + i * 4))).ToArray();
        return array?.Children.Select(c => Convert.ToUInt32(c.Value)).ToArray() ?? Array.Empty<uint>();
    }

    /// <summary>
    /// Packs skin influences as 4 per vertex. Unity may store fewer weights than indices
    /// (e.g. a single bone has indices only); missing weights take the remainder.
    /// </summary>
    private static (int[]?, float[]?) ReadSkin(float[]? weights, int weightDim, float[]? indices, int indexDim, int vertexCount)
    {
        if (indices == null || indexDim == 0) return (null, null);
        var outIdx = new int[vertexCount * 4];
        var outW = new float[vertexCount * 4];
        for (int v = 0; v < vertexCount; v++)
        {
            float sum = 0;
            for (int k = 0; k < Math.Min(4, indexDim); k++)
            {
                float w;
                if (weights != null && k < weightDim) w = weights[v * weightDim + k];
                else w = k == weightDim ? Math.Max(0, 1 - sum) : 0; // implicit last weight
                if (indexDim == 1 && weights == null) w = 1;
                outIdx[v * 4 + k] = (int)indices[v * indexDim + k];
                outW[v * 4 + k] = w;
                sum += w;
            }
            if (sum > 0)
                for (int k = 0; k < 4; k++) outW[v * 4 + k] /= sum;
        }
        return (outIdx, outW);
    }

    /// <summary>Adds a triangle with reversed winding (compensates for the mirrored X axis).</summary>
    private static void AddTriangle(List<int> tris, int a, int b, int c, int vertexCount)
    {
        if ((uint)a >= vertexCount || (uint)b >= vertexCount || (uint)c >= vertexCount) return;
        tris.Add(a);
        tris.Add(c);
        tris.Add(b);
    }

    private static byte[] ReadVertexBytes(SerializedFile sf, FieldValue root, FieldValue vd)
    {
        if (vd["m_DataSize"]?.Value is DataRange { Length: > 0 } range)
            return sf.Data.AsSpan((int)range.Offset, range.Length).ToArray();

        var stream = root["m_StreamData"];
        long size = Convert.ToInt64(stream?["size"]?.Value ?? 0u);
        if (size <= 0) throw new InvalidDataException("Mesh has no vertex data.");
        if (sf.Owner == null) throw new InvalidOperationException("No owning file to read the stream from.");
        return sf.Owner.ReadResource(stream!["path"]?.Value as string ?? "", Convert.ToInt64(stream["offset"]?.Value ?? 0ul), size);
    }

    /// <summary>Unity 2019+ VertexFormat sizes.</summary>
    private static int FormatSize(int format) => format switch
    {
        0 => 4,             // Float
        1 => 2,             // Float16
        2 or 3 => 1,        // UNorm8, SNorm8
        4 or 5 => 2,        // UNorm16, SNorm16
        6 or 7 => 1,        // UInt8, SInt8
        8 or 9 => 2,        // UInt16, SInt16
        10 or 11 => 4,      // UInt32, SInt32
        _ => throw new NotSupportedException($"Unknown vertex format {format}."),
    };

    private static float ReadComponent(byte[] d, int p, int format) => format switch
    {
        0 => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p)),
        1 => (float)BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(p)),
        2 => d[p] / 255f,
        3 => Math.Max((sbyte)d[p] / 127f, -1f),
        4 => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)) / 65535f,
        5 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(p)) / 32767f, -1f),
        6 => d[p],
        7 => (sbyte)d[p],
        8 => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)),
        9 => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(p)),
        10 => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)),
        11 => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)),
        _ => 0,
    };
}
