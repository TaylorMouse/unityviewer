namespace UnityBrowser.Unity;

/// <summary>
/// The skeleton stored in an Avatar. Rigs imported with "Optimize Game Objects" have no bone
/// Transforms in the scene (SkinnedMeshRenderer.m_Bones is empty); the Avatar still holds every
/// bone's path (m_TOS), its parent (m_AvatarSkeleton) and its rest pose (m_AvatarSkeletonPose).
/// </summary>
public sealed class AvatarRig
{
    public string Name { get; init; } = "";
    /// <summary>Path hash (CRC32 of the path from the rig root) to full path.</summary>
    public Dictionary<uint, string> Paths { get; } = new();
    /// <summary>One entry per skeleton node; node 0 is the rig root.</summary>
    public List<(uint Hash, int Parent, float[] Position, float[] Rotation, float[] Scale)> Nodes { get; } = new();

    public int NodeOf(uint hash) => Nodes.FindIndex(n => n.Hash == hash);

    public string NameOf(int node)
    {
        if (!Paths.TryGetValue(Nodes[node].Hash, out var path) || path.Length == 0) return node == 0 ? "root" : $"node_{node}";
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Local transform of a node, converted to right-handed (mirror X).</summary>
    public double[] LocalMatrix(int node)
    {
        var n = Nodes[node];
        return Mat4.Trs(-n.Position[0], n.Position[1], n.Position[2], n.Rotation[0], -n.Rotation[1], -n.Rotation[2], n.Rotation[3],
                        n.Scale[0], n.Scale[1], n.Scale[2]);
    }

    public double[] WorldMatrix(int node)
    {
        var m = LocalMatrix(node);
        for (int p = Nodes[node].Parent; p >= 0 && p < Nodes.Count; p = Nodes[p].Parent) m = Mat4.Multiply(LocalMatrix(p), m);
        return m;
    }

    public bool Covers(MeshData mesh) => mesh.BoneNameHashes.Length > 0 && mesh.BoneNameHashes.All(h => NodeOf(h) >= 0);

    /// <summary>
    /// How far this rig's rest pose is from the mesh's bind pose: for a matching pose, world * bindpose is
    /// the same matrix for every bone. Smaller is better; the model's own Avatar gives ~0.
    /// </summary>
    public double BindSpread(MeshData mesh)
    {
        double[]? first = null;
        double spread = 0;
        for (int s = 0; s < mesh.BoneNameHashes.Length && s < mesh.BindPoses.Count; s++)
        {
            int node = NodeOf(mesh.BoneNameHashes[s]);
            if (node < 0) return double.MaxValue;
            var p = Mat4.Multiply(WorldMatrix(node), mesh.BindPoses[s]);
            first ??= p;
            for (int i = 0; i < 16; i++) spread = Math.Max(spread, Math.Abs(p[i] - first[i]));
        }
        return spread;
    }

    /// <summary>The Avatar in the file that fits the mesh best (all bones present, rest pose closest to the bind pose).</summary>
    public static AvatarRig? BestFor(SerializedFile sf, MeshData mesh) =>
        ReadAll(sf).Where(r => r.Covers(mesh)).OrderBy(r => r.BindSpread(mesh)).FirstOrDefault();

    public static List<AvatarRig> ReadAll(SerializedFile sf)
    {
        var rigs = new List<AvatarRig>();
        foreach (var o in sf.Objects.Where(o => o.TypeName == "Avatar"))
        {
            try
            {
                rigs.Add(Read(sf, o));
            }
            catch
            {
                // An Avatar we can't read simply isn't offered as a rig.
            }
        }
        return rigs;
    }

    public static AvatarRig Read(SerializedFile sf, ObjectInfo o)
    {
        var root = AssetPreview.ReadObject(sf, o);
        var rig = new AvatarRig { Name = root["m_Name"]?.Value as string ?? "" };
        foreach (var pair in root["m_TOS"]?.Children ?? new List<FieldValue>())
            rig.Paths[Convert.ToUInt32(pair["first"]?.Value)] = pair["second"]?.Value as string ?? "";

        var avatar = root["m_Avatar"] ?? throw new InvalidDataException("Avatar has no constant data.");
        var skeleton = avatar["m_AvatarSkeleton"]?["data"] ?? avatar["m_AvatarSkeleton"] ?? throw new InvalidDataException("Avatar has no skeleton.");
        var pose = (avatar["m_AvatarSkeletonPose"]?["data"] ?? avatar["m_AvatarSkeletonPose"])?["m_X"]?.Children
                   ?? throw new InvalidDataException("Avatar has no skeleton pose.");
        var nodes = skeleton["m_Node"]?.Children ?? new List<FieldValue>();
        var ids = ReadUInts(sf, skeleton["m_ID"]);
        if (nodes.Count != ids.Length || pose.Count != nodes.Count)
            throw new InvalidDataException("Avatar skeleton tables disagree in size.");

        float[] V(FieldValue? f, params string[] c) => c.Select(n => Convert.ToSingle(f?[n]?.Value ?? 0f)).ToArray();
        for (int i = 0; i < nodes.Count; i++)
        {
            var x = pose[i];
            rig.Nodes.Add((ids[i], Convert.ToInt32(nodes[i]["m_ParentId"]?.Value),
                V(x["t"], "x", "y", "z"), V(x["q"], "x", "y", "z", "w"), V(x["s"], "x", "y", "z")));
        }
        return rig;
    }

    private static uint[] ReadUInts(SerializedFile sf, FieldValue? array)
    {
        if (array?.Value is PrimitiveArray a)
            return Enumerable.Range(0, a.Count).Select(i => BitConverter.ToUInt32(sf.Data, (int)a.Offset + i * 4)).ToArray();
        return array?.Children.Select(c => Convert.ToUInt32(c.Value)).ToArray() ?? Array.Empty<uint>();
    }
}
