namespace UnityBrowser.Unity;

public sealed class SkeletonNode
{
    public required string Name { get; init; }
    /// <summary>Unique, whitespace-free identifier (COLLADA sid).</summary>
    public required string Sid { get; init; }
    public int Parent { get; init; } = -1;
    /// <summary>Local transform relative to the parent, row-major, right-handed.</summary>
    public required double[] Local { get; init; }
}

/// <summary>A bone hierarchy plus the mapping from mesh bone slots to hierarchy nodes.</summary>
public sealed class Skeleton
{
    /// <summary>Nodes ordered so every parent comes before its children.</summary>
    public List<SkeletonNode> Nodes { get; } = new();
    /// <summary>For each mesh bone slot (bind pose index), the node it drives.</summary>
    public int[] SlotToNode { get; init; } = Array.Empty<int>();
    /// <summary>True when bone names and hierarchy are known (from the SkinnedMeshRenderer or an Avatar).</summary>
    public bool FromScene { get; init; }
    /// <summary>Where the names and hierarchy came from, for display.</summary>
    public string Source { get; init; } = "";
    /// <summary>Bone slots whose stored bind pose was unusable and was rebuilt from the hierarchy.</summary>
    public int RepairedBindPoses { get; set; }

    public double[] WorldMatrix(int node)
    {
        var m = Nodes[node].Local;
        for (int p = Nodes[node].Parent; p >= 0; p = Nodes[p].Parent) m = Mat4.Multiply(Nodes[p].Local, m);
        return m;
    }
}

/// <summary>
/// Rebuilds the skeleton of a skinned mesh. Bone names and hierarchy come from the SkinnedMeshRenderer
/// that uses the mesh (its m_Bones Transforms and their parents). When no renderer is found,
/// a flat skeleton is built from the bind poses so the skin still works.
/// </summary>
public static class SkeletonReader
{
    public static Skeleton? Read(SerializedFile sf, ObjectInfo meshObj, MeshData mesh)
    {
        if (!mesh.IsSkinned || mesh.BindPoses.Count == 0) return null;
        var named = FromRenderer(sf, meshObj, mesh) ?? FromAvatar(sf, mesh);
        return named != null ? ToBindPose(named, mesh) : FromBindPoses(mesh);
    }

    /// <summary>
    /// Re-poses the skeleton into its bind pose, so the rest pose matches the mesh exactly
    /// (the pose saved in the scene is often an animated or offset one). Bones get their bind world
    /// matrix (inverse bind pose, in mesh space); helper ancestors keep their scene offset to a child.
    /// </summary>
    private static Skeleton ToBindPose(Skeleton s, MeshData mesh)
    {
        var bindWorld = new double[]?[s.Nodes.Count];
        var broken = new List<int>();
        for (int slot = 0; slot < s.SlotToNode.Length; slot++)
        {
            if (!IsUsableBindPose(mesh.BindPoses[slot])) { broken.Add(slot); continue; }
            bindWorld[s.SlotToNode[slot]] ??= Mat4.Invert(mesh.BindPoses[slot]);
        }

        // Children come after parents, so walking backwards resolves helpers from their children.
        for (int i = s.Nodes.Count - 1; i >= 0; i--)
        {
            int parent = s.Nodes[i].Parent;
            if (parent >= 0 && bindWorld[i] != null && bindWorld[parent] == null)
                bindWorld[parent] = Mat4.Multiply(bindWorld[i], Mat4.Invert(s.Nodes[i].Local));
        }

        // Anything still unresolved (e.g. a bone with a broken bind pose) hangs off its parent
        // with its scene-local offset. Scale is dropped: broken bones are often scaled to zero to hide them.
        for (int i = 0; i < s.Nodes.Count; i++)
        {
            int parent = s.Nodes[i].Parent;
            if (bindWorld[i] == null && parent >= 0 && bindWorld[parent] != null)
                bindWorld[i] = Mat4.Multiply(bindWorld[parent]!, Mat4.RemoveScale(s.Nodes[i].Local));
        }

        // Replace broken bind poses so they agree with the rebuilt rest pose.
        foreach (int slot in broken)
        {
            var world = bindWorld[s.SlotToNode[slot]];
            if (world != null) mesh.BindPoses[slot] = Mat4.Invert(world);
        }

        var posed = new Skeleton { FromScene = true, Source = s.Source, SlotToNode = s.SlotToNode, RepairedBindPoses = broken.Count };
        for (int i = 0; i < s.Nodes.Count; i++)
        {
            var n = s.Nodes[i];
            var world = bindWorld[i] ?? s.WorldMatrix(i);
            var local = n.Parent >= 0 && bindWorld[n.Parent] != null
                ? Mat4.Multiply(Mat4.Invert(bindWorld[n.Parent]!), world)
                : world;
            posed.Nodes.Add(new SkeletonNode { Name = n.Name, Sid = n.Sid, Parent = n.Parent, Local = local });
        }
        return posed;
    }

    /// <summary>
    /// Some assets ship broken bind poses (all zeros, or garbage values); those can't be inverted
    /// and would collapse or scatter the vertices they drive.
    /// </summary>
    private static bool IsUsableBindPose(double[] m)
    {
        if (m.Any(v => double.IsNaN(v) || Math.Abs(v) > 1e5)) return false;
        if (Math.Abs(m[12]) > 1e-4 || Math.Abs(m[13]) > 1e-4 || Math.Abs(m[14]) > 1e-4 || Math.Abs(m[15] - 1) > 1e-4) return false;
        var check = Mat4.Multiply(m, Mat4.Invert(m));
        for (int i = 0; i < 16; i++)
            if (Math.Abs(check[i] - (i % 5 == 0 ? 1 : 0)) > 1e-4) return false;
        return true;
    }

    private static Skeleton? FromRenderer(SerializedFile sf, ObjectInfo meshObj, MeshData mesh)
    {
        var graph = SceneGraph.For(sf);
        var renderer = graph.SkinnedRenderers.FirstOrDefault(r => r.MeshPathId == meshObj.PathId && r.Bones.Length > 0);
        if (renderer == null || renderer.Bones.Length != mesh.BindPoses.Count) return null;
        if (!renderer.Bones.All(graph.Transforms.ContainsKey)) return null; // bone in another file

        // The bone transforms and all their ancestors.
        var include = new HashSet<long>();
        foreach (var b in renderer.Bones)
            for (long n = b; graph.Transforms.ContainsKey(n) && include.Add(n); n = graph.Transforms[n].Parent) { }

        // Order parents before children (walk down from the roots).
        var skeleton = new Skeleton { FromScene = true, Source = "the SkinnedMeshRenderer", SlotToNode = new int[renderer.Bones.Length] };
        var nodeIndex = new Dictionary<long, int>();
        var usedSids = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(long Id, int Parent)>(include.Where(id => !include.Contains(graph.Transforms[id].Parent)).Select(id => (id, -1)));
        while (queue.Count > 0)
        {
            var (id, parent) = queue.Dequeue();
            var t = graph.Transforms[id];
            string name = t.Name.Length > 0 ? t.Name : $"node_{id}";
            nodeIndex[id] = skeleton.Nodes.Count;
            skeleton.Nodes.Add(new SkeletonNode { Name = name, Sid = UniqueSid(name, usedSids), Parent = parent, Local = LocalMatrix(t) });
            foreach (var k in t.Children.Where(include.Contains)) queue.Enqueue((k, nodeIndex[id]));
        }

        for (int i = 0; i < renderer.Bones.Length; i++) skeleton.SlotToNode[i] = nodeIndex[renderer.Bones[i]];
        return skeleton;
    }

    /// <summary>
    /// Optimised rigs ("Optimize Game Objects"): the renderer lists no bones, but an Avatar in the file
    /// holds every bone's path, parent and rest pose. Mesh bone slots map to it by path hash.
    /// </summary>
    private static Skeleton? FromAvatar(SerializedFile sf, MeshData mesh)
    {
        var rig = AvatarRig.BestFor(sf, mesh);
        if (rig == null) return null;

        // Order parents before children.
        var order = new List<int>();
        var visited = new bool[rig.Nodes.Count];
        void Visit(int i)
        {
            if (visited[i]) return;
            visited[i] = true;
            int p = rig.Nodes[i].Parent;
            if (p >= 0 && p < rig.Nodes.Count) Visit(p);
            order.Add(i);
        }
        for (int i = 0; i < rig.Nodes.Count; i++) Visit(i);
        var newIndex = new int[rig.Nodes.Count];
        for (int k = 0; k < order.Count; k++) newIndex[order[k]] = k;

        var used = new HashSet<string>(StringComparer.Ordinal);
        var skeleton = new Skeleton
        {
            FromScene = true,
            Source = $"Avatar '{rig.Name}'",
            SlotToNode = mesh.BoneNameHashes.Select(h => newIndex[rig.NodeOf(h)]).ToArray(),
        };
        foreach (int i in order)
        {
            int p = rig.Nodes[i].Parent;
            skeleton.Nodes.Add(new SkeletonNode
            {
                Name = rig.NameOf(i),
                Sid = UniqueSid(rig.NameOf(i), used),
                Parent = p >= 0 && p < rig.Nodes.Count ? newIndex[p] : -1,
                Local = rig.LocalMatrix(i),
            });
        }
        return skeleton;
    }

    /// <summary>No renderer: one joint per bind pose, placed at the inverse of its bind matrix.</summary>
    private static Skeleton FromBindPoses(MeshData mesh)
    {
        var skeleton = new Skeleton { FromScene = false, Source = "bind poses", SlotToNode = Enumerable.Range(0, mesh.BindPoses.Count).ToArray() };
        for (int i = 0; i < mesh.BindPoses.Count; i++)
            skeleton.Nodes.Add(new SkeletonNode { Name = $"bone_{i}", Sid = $"bone_{i}", Local = Mat4.Invert(mesh.BindPoses[i]) });
        return skeleton;
    }

    /// <summary>Unity local TRS converted to a right-handed matrix (mirror X: position.x and quaternion y/z negate).</summary>
    private static double[] LocalMatrix(SceneTransform t) =>
        Mat4.Trs(-t.Position[0], t.Position[1], t.Position[2],
                 t.Rotation[0], -t.Rotation[1], -t.Rotation[2], t.Rotation[3],
                 t.Scale[0], t.Scale[1], t.Scale[2]);

    private static string UniqueSid(string name, HashSet<string> used)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        string sid = chars.Length == 0 ? "bone" : new string(chars);
        if (!char.IsLetter(sid[0]) && sid[0] != '_') sid = "_" + sid;
        string candidate = sid;
        for (int n = 2; !used.Add(candidate); n++) candidate = $"{sid}_{n}";
        return candidate;
    }
}
