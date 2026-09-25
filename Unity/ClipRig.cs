namespace UnityBrowser.Unity;

/// <summary>
/// The transforms an AnimationClip plays on, from the scene hierarchy or (for optimised rigs) from an
/// Avatar, plus the skinned meshes bound to them. Shared by the DAE exporter and the animation player.
/// </summary>
public sealed class ClipRig
{
    public required Dictionary<long, SceneTransform> Transforms { get; init; }
    public long Root { get; init; }
    public required Dictionary<uint, long> Resolved { get; init; }
    public List<(ObjectInfo MeshObj, long[] Bones)> Skins { get; } = new();

    /// <summary>The rig the clip was authored against, or null when no skinned character uses it.</summary>
    public static ClipRig? Find(SerializedFile sf, List<ClipBinding> bindings) =>
        FindScene(sf, bindings) ?? FindAvatar(sf, bindings);

    /// <summary>
    /// Scene rig: the transform the clip was authored against is the one under which the most path hashes
    /// resolve, among the ancestors of skinned meshes' bones.
    /// </summary>
    private static ClipRig? FindScene(SerializedFile sf, List<ClipBinding> bindings)
    {
        var graph = SceneGraph.For(sf);
        var hashes = bindings.Select(b => b.Path).Where(h => h != 0).Distinct().ToList();
        var candidates = new HashSet<long>();
        foreach (var r in graph.SkinnedRenderers)
            foreach (var b in r.Bones.Take(1))
                for (long n = b; graph.Transforms.ContainsKey(n); n = graph.Transforms[n].Parent) candidates.Add(n);

        long best = 0;
        int bestScore = 0;
        Dictionary<uint, long> bestMap = new();
        foreach (var candidate in candidates)
        {
            var map = graph.HashesUnder(candidate);
            int score = hashes.Count(map.ContainsKey);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
                bestMap = map;
            }
        }
        if (bestScore == 0) return null;

        var rig = new ClipRig { Transforms = graph.Transforms, Root = best, Resolved = bestMap };
        foreach (var r in graph.SkinnedRenderers.Where(r => r.Bones.Length > 0 && r.Bones.All(b => graph.IsUnder(b, best))))
            if (sf.Objects.FirstOrDefault(o => o.PathId == r.MeshPathId) is { } meshObj)
                rig.Skins.Add((meshObj, r.Bones));
        return rig.Skins.Count > 0 ? rig : null;
    }

    /// <summary>
    /// Avatar rig, for optimised rigs whose renderers list no bones. The Avatar that matches the file's
    /// skinned meshes (rest pose closest to their bind pose) is used; clip paths resolve through its m_TOS.
    /// </summary>
    private static ClipRig? FindAvatar(SerializedFile sf, List<ClipBinding> bindings)
    {
        var graph = SceneGraph.For(sf);
        var avatars = AvatarRig.ReadAll(sf);
        if (avatars.Count == 0) return null;

        // Skinned meshes without scene bones (one entry per mesh).
        var meshes = graph.SkinnedRenderers.Where(r => r.Bones.Length == 0).Select(r => r.MeshPathId).Distinct()
            .Select(id => sf.Objects.FirstOrDefault(o => o.PathId == id)).Where(o => o != null)
            .Select(o => (Obj: o!, Mesh: MeshReader.Read(sf, o!))).Where(m => m.Mesh.IsSkinned).ToList();
        if (meshes.Count == 0) return null;

        var hashes = bindings.Select(b => b.Path).Where(h => h != 0).Distinct().ToList();
        var best = avatars
            .Select(a => (Rig: a, Meshes: meshes.Where(m => a.Covers(m.Mesh)).ToList(), Hits: hashes.Count(h => a.NodeOf(h) >= 0)))
            .Where(x => x.Meshes.Count > 0 && x.Hits > 0)
            .OrderByDescending(x => x.Meshes.Count)
            .ThenBy(x => x.Meshes.Min(m => x.Rig.BindSpread(m.Mesh)) < 1e-3 ? 0 : 1)   // the model's own Avatar first
            .ThenByDescending(x => x.Hits)
            .FirstOrDefault();
        if (best.Rig == null) return null;

        // Synthetic transforms (ids 1..n) carrying the Avatar's local rest pose.
        var avatar = best.Rig;
        var transforms = new Dictionary<long, SceneTransform>();
        for (int i = 0; i < avatar.Nodes.Count; i++)
        {
            var n = avatar.Nodes[i];
            transforms[i + 1] = new SceneTransform
            {
                Id = i + 1,
                Name = avatar.NameOf(i),
                Parent = n.Parent >= 0 && n.Parent < avatar.Nodes.Count ? n.Parent + 1 : 0,
                Position = n.Position,
                Rotation = n.Rotation,
                Scale = n.Scale,
            };
        }
        foreach (var t in transforms.Values)
            if (transforms.TryGetValue(t.Parent, out var parent)) parent.Children.Add(t.Id);

        var resolved = new Dictionary<uint, long>();
        for (int i = 0; i < avatar.Nodes.Count; i++) resolved.TryAdd(avatar.Nodes[i].Hash, i + 1);

        long root = transforms.Values.FirstOrDefault(t => !transforms.ContainsKey(t.Parent))?.Id ?? 1;
        var rig = new ClipRig { Transforms = transforms, Root = root, Resolved = resolved };
        foreach (var (obj, mesh) in best.Meshes)
            rig.Skins.Add((obj, mesh.BoneNameHashes.Select(h => (long)avatar.NodeOf(h) + 1).ToArray()));
        return rig;
    }
}
