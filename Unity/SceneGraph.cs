using System.Runtime.CompilerServices;
using System.Text;

namespace UnityBrowser.Unity;

public sealed class SceneTransform
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long Parent { get; init; }
    public List<long> Children { get; } = new();
    /// <summary>Unity local values (left-handed): position xyz, rotation xyzw, scale xyz.</summary>
    public float[] Position { get; init; } = new float[3];
    public float[] Rotation { get; init; } = { 0, 0, 0, 1 };
    public float[] Scale { get; init; } = { 1, 1, 1 };
}

public sealed record SkinnedRenderer(long PathId, long MeshPathId, long[] Bones);

/// <summary>The Transform hierarchy of a serialised file, built once and cached.</summary>
public sealed class SceneGraph
{
    private static readonly ConditionalWeakTable<SerializedFile, SceneGraph> Cache = new();

    public Dictionary<long, SceneTransform> Transforms { get; } = new();
    public List<SkinnedRenderer> SkinnedRenderers { get; } = new();

    public static SceneGraph For(SerializedFile sf)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(sf, out var graph))
            {
                graph = Build(sf);
                Cache.Add(sf, graph);
            }
            return graph;
        }
    }

    private static SceneGraph Build(SerializedFile sf)
    {
        var g = new SceneGraph();
        var byId = sf.Objects.ToDictionary(o => o.PathId);
        float[] V(FieldValue? f, params string[] c) => c.Select(n => Convert.ToSingle(f?[n]?.Value ?? 0f)).ToArray();

        foreach (var o in sf.Objects.Where(o => o.TypeName is "Transform" or "RectTransform"))
        {
            var t = AssetPreview.ReadObject(sf, o);
            long go = Convert.ToInt64(t["m_GameObject"]?["m_PathID"]?.Value);
            g.Transforms[o.PathId] = new SceneTransform
            {
                Id = o.PathId,
                Name = byId.TryGetValue(go, out var goInfo) ? ObjectNames.GetName(sf, goInfo) ?? "" : "",
                Parent = Convert.ToInt64(t["m_Father"]?["m_PathID"]?.Value),
                Position = V(t["m_LocalPosition"], "x", "y", "z"),
                Rotation = V(t["m_LocalRotation"], "x", "y", "z", "w"),
                Scale = V(t["m_LocalScale"], "x", "y", "z"),
            };
        }
        foreach (var t in g.Transforms.Values)
            if (g.Transforms.TryGetValue(t.Parent, out var parent)) parent.Children.Add(t.Id);

        foreach (var o in sf.Objects.Where(o => o.TypeName == "SkinnedMeshRenderer"))
        {
            var r = AssetPreview.ReadObject(sf, o);
            if (Convert.ToInt32(r["m_Mesh"]?["m_FileID"]?.Value) != 0) continue;
            var bones = r["m_Bones"]?.Children.Select(b => Convert.ToInt64(b["m_PathID"]?.Value)).ToArray() ?? Array.Empty<long>();
            g.SkinnedRenderers.Add(new SkinnedRenderer(o.PathId, Convert.ToInt64(r["m_Mesh"]?["m_PathID"]?.Value), bones));
        }
        return g;
    }

    public bool IsUnder(long node, long root)
    {
        for (long n = node; Transforms.ContainsKey(n); n = Transforms[n].Parent)
            if (n == root) return true;
        return false;
    }

    /// <summary>Unity's animation path hash: CRC32 of the path relative to the animated root ("a/b/c").</summary>
    public static uint PathHash(string path) => Crc32.Compute(Encoding.UTF8.GetBytes(path));

    private readonly Dictionary<long, Dictionary<uint, long>> _hashesUnder = new();

    /// <summary>Path hash of every transform below <paramref name="root"/>, relative to it (cached).</summary>
    public Dictionary<uint, long> HashesUnder(long root)
    {
        lock (_hashesUnder)
        {
            if (!_hashesUnder.TryGetValue(root, out var cached))
                _hashesUnder[root] = cached = ComputeHashesUnder(root);
            return cached;
        }
    }

    private Dictionary<uint, long> ComputeHashesUnder(long root)
    {
        var map = new Dictionary<uint, long>();
        void Walk(long id, string path)
        {
            foreach (var c in Transforms[id].Children)
            {
                string p = path.Length == 0 ? Transforms[c].Name : $"{path}/{Transforms[c].Name}";
                map.TryAdd(PathHash(p), c);
                Walk(c, p);
            }
        }
        if (Transforms.ContainsKey(root)) Walk(root, "");
        return map;
    }
}

public static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
