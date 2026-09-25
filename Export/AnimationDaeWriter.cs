using System.Text;
using UnityBrowser.Unity;

namespace UnityBrowser.Export;

/// <summary>
/// Exports an AnimationClip as COLLADA: the character rig it animates (joint hierarchy), every skinned
/// mesh bound to that rig, and the animation itself. Keys are kept as in the source:
/// position and scale curves keep Unity's key times, values and tangents (as BEZIER keys);
/// quaternion rotations are converted to Euler angles at the original key times, with tangents taken
/// from the Unity curve at each key; Unity Euler curves are kept as they are.
/// </summary>
public static class AnimationDaeWriter
{
    private const double Rad2Deg = 180 / Math.PI;

    /// <summary>A scene node in the export, with its (right-handed) rest values and animation channels.</summary>
    private sealed class Node
    {
        public required SceneTransform Transform { get; init; }
        public required string Sid { get; init; }
        public string Id => "rig-" + Sid;
        public List<Node> Children { get; } = new();
        public bool EulerLayout { get; set; }
        public Ref? Position, Rotation, Scale, Euler;
    }

    /// <summary>A binding with the clip whose curves it indexes (a layered export mixes clips).</summary>
    private sealed record Ref(ClipData Clip, ClipBinding Binding);

    private sealed record Channel(string Target, string Param, List<(double Time, double Value, double In, double Out, bool Step)> Keys);

    public static string Write(string path, SerializedFile sf, ObjectInfo clipObj)
    {
        // A skin clip that is an override layer is combined with its base clip, as the game plays them.
        var layered = ClipLayering.Resolve(sf, clipObj);
        var clip = layered.Primary;
        var bindings = layered.AllTransformBindings.ToList();
        if (bindings.Count == 0) throw new NotSupportedException("This clip does not animate any transforms.");

        var rig = ClipRig.Find(sf, bindings)
                  ?? throw new NotSupportedException("No skinned character uses this clip (it animates props, which are not exported yet).");
        var transforms = rig.Transforms;
        long root = rig.Root;
        var resolved = rig.Resolved;

        // Nodes: the rig root's ancestors (so world transforms match the scene), all skin bones and
        // animated transforms, plus everything in between.
        var include = new HashSet<long>();
        void AddWithAncestors(long id)
        {
            for (long n = id; transforms.ContainsKey(n) && include.Add(n); n = transforms[n].Parent) { }
        }
        AddWithAncestors(root);
        foreach (var skin in rig.Skins) foreach (var b in skin.Bones) AddWithAncestors(b);
        foreach (var id in resolved.Values) AddWithAncestors(id);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new Dictionary<long, Node>();
        Node Build(long id)
        {
            var t = transforms[id];
            var node = new Node { Transform = t, Sid = UniqueSid(t.Name, used) };
            nodes[id] = node;
            foreach (var c in t.Children.Where(include.Contains)) node.Children.Add(Build(c));
            return node;
        }
        var tops = include.Where(id => !include.Contains(transforms[id].Parent)).Select(Build).ToList();

        // Attach the curves to their nodes; later layers override earlier ones per property.
        foreach (var (layerClip, _) in layered.Layers)
            foreach (var b in layerClip.Bindings.Where(b => b.IsTransform && b.Attribute is >= 1 and <= 4))
            {
                long id = b.Path == 0 ? root : resolved.GetValueOrDefault(b.Path);
                if (!nodes.TryGetValue(id, out var node)) continue;
                var r = new Ref(layerClip, b);
                switch (b.Attribute)
                {
                    case ClipBinding.Position: node.Position = r; break;
                    case ClipBinding.Scale: node.Scale = r; break;
                    case ClipBinding.Rotation: node.Rotation = r; node.Euler = null; node.EulerLayout = false; break;
                    case ClipBinding.Euler: node.Euler = r; node.Rotation = null; node.EulerLayout = true; break;
                }
            }

        // Skinned meshes (bind poses repaired the same way as the mesh export).
        var meshes = new List<(string Id, MeshData Mesh, string[] SlotSids)>();
        var meshIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (meshObj, bones) in rig.Skins)
        {
            var mesh = MeshReader.Read(sf, meshObj);
            if (!mesh.IsSkinned || mesh.BindPoses.Count != bones.Length || !bones.All(nodes.ContainsKey)) continue;
            SkeletonReader.Read(sf, meshObj, mesh);
            string id = UniqueSid(DaeWriter.MakeId(mesh.Name), meshIds);
            meshes.Add((id, mesh, bones.Select(b => nodes[b].Sid).ToArray()));
        }
        if (meshes.Count == 0) throw new InvalidDataException("The rig's skinned meshes could not be read.");

        var channels = new List<Channel>();
        foreach (var node in nodes.Values) channels.AddRange(BuildChannels(node));

        var sb = new StringBuilder();
        DaeWriter.AppendHeader(sb);
        DaeWriter.AppendEffects(sb);
        sb.AppendLine("  <library_materials>");
        foreach (var m in meshes) DaeWriter.AppendMaterials(sb, m.Id, m.Mesh);
        sb.AppendLine("  </library_materials>");
        sb.AppendLine("  <library_geometries>");
        foreach (var m in meshes) DaeWriter.AppendGeometry(sb, m.Id, m.Mesh);
        sb.AppendLine("  </library_geometries>");
        sb.AppendLine("  <library_controllers>");
        foreach (var m in meshes) DaeWriter.AppendController(sb, m.Id, m.Mesh, m.SlotSids);
        sb.AppendLine("  </library_controllers>");
        AppendAnimations(sb, channels);

        sb.AppendLine("  <library_visual_scenes>");
        sb.AppendLine("    <visual_scene id=\"scene\" name=\"scene\">");
        foreach (var top in tops) AppendNode(sb, top, 3);
        foreach (var m in meshes) DaeWriter.AppendMeshInstance(sb, m.Id, m.Mesh, true, tops.Select(t => t.Id), 3);
        sb.AppendLine("    </visual_scene>");
        sb.AppendLine("  </library_visual_scenes>");
        DaeWriter.AppendFooter(sb);
        File.WriteAllText(path, sb.ToString());

        return $"{string.Join(" + ", layered.Layers.Select(l => l.Clip.Name))}: {channels.Count} channels on {nodes.Count} nodes, {meshes.Count} skinned mesh(es)";
    }

    // ---- Channels ----

    private static IEnumerable<Channel> BuildChannels(Node node)
    {
        if (node.Position is { } p)
        {
            yield return Direct($"{node.Id}/location.X", "X", p.Clip.Curves[p.Binding.FirstCurve], -1);
            yield return Direct($"{node.Id}/location.Y", "Y", p.Clip.Curves[p.Binding.FirstCurve + 1], 1);
            yield return Direct($"{node.Id}/location.Z", "Z", p.Clip.Curves[p.Binding.FirstCurve + 2], 1);
        }
        if (node.Scale is { } s)
        {
            yield return Direct($"{node.Id}/scale.X", "X", s.Clip.Curves[s.Binding.FirstCurve], 1);
            yield return Direct($"{node.Id}/scale.Y", "Y", s.Clip.Curves[s.Binding.FirstCurve + 1], 1);
            yield return Direct($"{node.Id}/scale.Z", "Z", s.Clip.Curves[s.Binding.FirstCurve + 2], 1);
        }
        if (node.Euler is { } e)
        {
            // Unity Euler order is Z, X, Y (R = Ry Rx Rz); mirroring X negates the Y and Z angles.
            yield return Direct($"{node.Id}/rotationX.ANGLE", "ANGLE", e.Clip.Curves[e.Binding.FirstCurve], 1);
            yield return Direct($"{node.Id}/rotationY.ANGLE", "ANGLE", e.Clip.Curves[e.Binding.FirstCurve + 1], -1);
            yield return Direct($"{node.Id}/rotationZ.ANGLE", "ANGLE", e.Clip.Curves[e.Binding.FirstCurve + 2], -1);
        }
        else if (node.Rotation is { } r)
        {
            foreach (var c in QuaternionChannels(node, r)) yield return c;
        }
    }

    /// <summary>A curve copied key for key (values and slopes multiplied by <paramref name="sign"/>).</summary>
    private static Channel Direct(string target, string param, AnimationCurve curve, double sign) =>
        new(target, param, curve.Keys.Select(k => ((double)k.Time, sign * k.Value, sign * k.InSlope, sign * k.OutSlope, k.Stepped)).ToList());

    /// <summary>
    /// Quaternion curves to Z/Y/X Euler channels, keyed at the original key times. Tangents are the
    /// one-sided derivatives of the Unity curve at each key, so the shape follows the source closely.
    /// </summary>
    private static IEnumerable<Channel> QuaternionChannels(Node node, Ref r)
    {
        var q = Enumerable.Range(0, 4).Select(i => r.Clip.Curves[r.Binding.FirstCurve + i]).ToArray();
        var times = q.SelectMany(c => c.Keys.Select(k => k.Time)).Distinct().OrderBy(x => x).ToList();
        bool Stepped(float time) => q.Any(c => c.Keys.Any(k => k.Time == time && k.Stepped));

        double[] EulerAt(double time, double[]? near)
        {
            // Unity normalises the interpolated quaternion; mirroring X negates its y and z.
            float tt = (float)time;
            double qx = q[0].Evaluate(tt), qy = -q[1].Evaluate(tt), qz = -q[2].Evaluate(tt), qw = q[3].Evaluate(tt);
            double len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len < 1e-9) { qx = qy = qz = 0; qw = len = 1; }
            var angles = EulerZyx(Mat4.Trs(0, 0, 0, qx / len, qy / len, qz / len, qw / len, 1, 1, 1));
            if (near != null)
                for (int a = 0; a < 3; a++)
                {
                    while (angles[a] - near[a] > 180) angles[a] -= 360;
                    while (angles[a] - near[a] < -180) angles[a] += 360;
                }
            return angles;
        }

        Key MakeKey(double time, double? prevTime, double? nextTime, double[]? near, bool step)
        {
            var v = EulerAt(time, near);
            var inSlope = new double[3];
            var outSlope = new double[3];
            if (nextTime is { } next)
            {
                double h = Math.Min(1e-3, (next - time) * 0.25);
                var after = EulerAt(time + h, v);
                for (int a = 0; a < 3; a++) outSlope[a] = (after[a] - v[a]) / h;
            }
            if (prevTime is { } prev)
            {
                double h = Math.Min(1e-3, (time - prev) * 0.25);
                var before = EulerAt(time - h, v);
                for (int a = 0; a < 3; a++) inSlope[a] = (v[a] - before[a]) / h;
            }
            if (prevTime == null) inSlope = outSlope;
            if (nextTime == null) outSlope = inSlope;
            return new Key(time, v, inSlope, outSlope, step);
        }

        // The original keys, converted.
        var original = new List<Key>();
        double[]? previous = null;
        for (int i = 0; i < times.Count; i++)
        {
            var key = MakeKey(times[i], i > 0 ? times[i - 1] : null, i + 1 < times.Count ? times[i + 1] : null,
                previous, Stepped((float)times[i]));
            original.Add(key);
            previous = key.V;
        }

        // Euler curves can't always follow a quaternion between keys (fast or flipping rotations).
        // Only in those segments, add keys until the curve stays within 0.25 degrees of Unity's.
        var keys = new List<Key>();
        if (original.Count > 0) keys.Add(original[0]);
        void Refine(Key a, Key b, int depth)
        {
            double dt = b.T - a.T;
            if (!a.Step && depth < 5 && dt > 1e-4)
            {
                double worst = 0;
                foreach (double f in new[] { 0.25, 0.5, 0.75 })
                {
                    double time = a.T + f * dt;
                    var approx = Hermite(a, b, time);
                    var truth = EulerAt(time, approx);
                    for (int ax = 0; ax < 3; ax++) worst = Math.Max(worst, Math.Abs(truth[ax] - approx[ax]));
                }
                if (worst > 0.25)
                {
                    double mt = a.T + dt / 2;
                    var mid = MakeKey(mt, a.T, b.T, Hermite(a, b, mt), false);
                    Refine(a, mid, depth + 1);
                    Refine(mid, b, depth + 1);
                    return;
                }
            }
            keys.Add(b);
        }
        for (int i = 0; i + 1 < original.Count; i++) Refine(original[i], original[i + 1], 0);

        string[] axes = { "Z", "Y", "X" };
        for (int a = 0; a < 3; a++)
            yield return new Channel($"{node.Id}/rotation{axes[a]}.ANGLE", "ANGLE",
                keys.Select(k => (k.T, k.V[a], k.In[a], k.Out[a], k.Step)).ToList());
    }

    private sealed record Key(double T, double[] V, double[] In, double[] Out, bool Step);

    /// <summary>What a BEZIER key pair with 1/3 handles evaluates to (identical to cubic Hermite in time).</summary>
    private static double[] Hermite(Key a, Key b, double time)
    {
        double dt = b.T - a.T, u = (time - a.T) / dt, u2 = u * u, u3 = u2 * u;
        var r = new double[3];
        for (int ax = 0; ax < 3; ax++)
            r[ax] = (2 * u3 - 3 * u2 + 1) * a.V[ax] + (u3 - 2 * u2 + u) * dt * a.Out[ax]
                  + (-2 * u3 + 3 * u2) * b.V[ax] + (u3 - u2) * dt * b.In[ax];
        return r;
    }

    // ---- Rotation helpers ----

    /// <summary>Angles in degrees [z, y, x] for R = Rz * Ry * Rx.</summary>
    private static double[] EulerZyx(double[] m)
    {
        double sy = Math.Clamp(-m[8], -1, 1);
        double y = Math.Asin(sy), x, z;
        if (Math.Abs(sy) < 0.99999)
        {
            x = Math.Atan2(m[9], m[10]);
            z = Math.Atan2(m[4], m[0]);
        }
        else
        {
            x = Math.Atan2(-m[6], m[5]);
            z = 0;
        }
        return new[] { z * Rad2Deg, y * Rad2Deg, x * Rad2Deg };
    }

    /// <summary>Angles in degrees [y, x, z] for R = Ry * Rx * Rz (Unity's Euler order).</summary>
    private static double[] EulerYxz(double[] m)
    {
        double sx = Math.Clamp(-m[6], -1, 1);
        double x = Math.Asin(sx), y, z;
        if (Math.Abs(sx) < 0.99999)
        {
            y = Math.Atan2(m[2], m[10]);
            z = Math.Atan2(m[4], m[5]);
        }
        else
        {
            y = Math.Atan2(-m[8], m[0]);
            z = 0;
        }
        return new[] { y * Rad2Deg, x * Rad2Deg, z * Rad2Deg };
    }

    // ---- Writing ----

    private static void AppendNode(StringBuilder sb, Node node, int depth)
    {
        var t = node.Transform;
        string pad = new(' ', depth * 2);
        string F(double v) => v.ToString("0.#########", DaeWriter.Ci);
        double len = Math.Sqrt(t.Rotation.Sum(v => (double)v * v));
        if (len < 1e-9) len = 1;
        var rot = Mat4.Trs(0, 0, 0, t.Rotation[0] / len, -t.Rotation[1] / len, -t.Rotation[2] / len, t.Rotation[3] / len, 1, 1, 1);

        sb.AppendLine($"{pad}<node id=\"{node.Id}\" name=\"{DaeWriter.Esc(t.Name)}\" sid=\"{node.Sid}\" type=\"JOINT\">");
        sb.AppendLine($"{pad}  <translate sid=\"location\">{F(-t.Position[0])} {F(t.Position[1])} {F(t.Position[2])}</translate>");
        if (node.EulerLayout)
        {
            var e = EulerYxz(rot);
            sb.AppendLine($"{pad}  <rotate sid=\"rotationY\">0 1 0 {F(e[0])}</rotate>");
            sb.AppendLine($"{pad}  <rotate sid=\"rotationX\">1 0 0 {F(e[1])}</rotate>");
            sb.AppendLine($"{pad}  <rotate sid=\"rotationZ\">0 0 1 {F(e[2])}</rotate>");
        }
        else
        {
            var e = EulerZyx(rot);
            sb.AppendLine($"{pad}  <rotate sid=\"rotationZ\">0 0 1 {F(e[0])}</rotate>");
            sb.AppendLine($"{pad}  <rotate sid=\"rotationY\">0 1 0 {F(e[1])}</rotate>");
            sb.AppendLine($"{pad}  <rotate sid=\"rotationX\">1 0 0 {F(e[2])}</rotate>");
        }
        sb.AppendLine($"{pad}  <scale sid=\"scale\">{F(t.Scale[0])} {F(t.Scale[1])} {F(t.Scale[2])}</scale>");
        foreach (var c in node.Children) AppendNode(sb, c, depth + 1);
        sb.AppendLine($"{pad}</node>");
    }

    private static void AppendAnimations(StringBuilder sb, List<Channel> channels)
    {
        string F(double v) => v.ToString("0.#########", DaeWriter.Ci);
        sb.AppendLine("  <library_animations>");
        int n = 0;
        foreach (var c in channels.Where(c => c.Keys.Count > 0))
        {
            string id = $"anim{n++}";
            int count = c.Keys.Count;
            var input = new StringBuilder();
            var output = new StringBuilder();
            var inTan = new StringBuilder();
            var outTan = new StringBuilder();
            var interp = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var k = c.Keys[i];
                double dtPrev = i > 0 ? k.Time - c.Keys[i - 1].Time : (i + 1 < count ? c.Keys[i + 1].Time - k.Time : 1 / 30.0);
                double dtNext = i + 1 < count ? c.Keys[i + 1].Time - k.Time : dtPrev;
                double inSlope = double.IsFinite(k.In) ? k.In : 0, outSlope = double.IsFinite(k.Out) ? k.Out : 0;
                input.Append(F(k.Time)).Append(' ');
                output.Append(F(k.Value)).Append(' ');
                inTan.Append(F(k.Time - dtPrev / 3)).Append(' ').Append(F(k.Value - inSlope * dtPrev / 3)).Append(' ');
                outTan.Append(F(k.Time + dtNext / 3)).Append(' ').Append(F(k.Value + outSlope * dtNext / 3)).Append(' ');
                interp.Append(k.Step ? "STEP " : "BEZIER ");
            }

            sb.AppendLine($"    <animation id=\"{id}\">");
            Source(sb, $"{id}-input", input, count, 1, "TIME", "float");
            Source(sb, $"{id}-output", output, count, 1, c.Param, "float");
            Source(sb, $"{id}-intan", inTan, count, 2, null, "float");
            Source(sb, $"{id}-outtan", outTan, count, 2, null, "float");
            sb.AppendLine($"      <source id=\"{id}-interp\"><Name_array id=\"{id}-interp-array\" count=\"{count}\">{interp.ToString().TrimEnd()}</Name_array>");
            sb.AppendLine($"        <technique_common><accessor source=\"#{id}-interp-array\" count=\"{count}\" stride=\"1\"><param name=\"INTERPOLATION\" type=\"name\"/></accessor></technique_common></source>");
            sb.AppendLine($"      <sampler id=\"{id}-sampler\">");
            sb.AppendLine($"        <input semantic=\"INPUT\" source=\"#{id}-input\"/>");
            sb.AppendLine($"        <input semantic=\"OUTPUT\" source=\"#{id}-output\"/>");
            sb.AppendLine($"        <input semantic=\"IN_TANGENT\" source=\"#{id}-intan\"/>");
            sb.AppendLine($"        <input semantic=\"OUT_TANGENT\" source=\"#{id}-outtan\"/>");
            sb.AppendLine($"        <input semantic=\"INTERPOLATION\" source=\"#{id}-interp\"/>");
            sb.AppendLine("      </sampler>");
            sb.AppendLine($"      <channel source=\"#{id}-sampler\" target=\"{c.Target}\"/>");
            sb.AppendLine("    </animation>");
        }
        sb.AppendLine("  </library_animations>");
    }

    private static void Source(StringBuilder sb, string id, StringBuilder values, int count, int stride, string? param, string type)
    {
        sb.AppendLine($"      <source id=\"{id}\"><float_array id=\"{id}-array\" count=\"{count * stride}\">{values.ToString().TrimEnd()}</float_array>");
        sb.Append($"        <technique_common><accessor source=\"#{id}-array\" count=\"{count}\" stride=\"{stride}\">");
        if (stride == 2) sb.Append($"<param name=\"X\" type=\"{type}\"/><param name=\"Y\" type=\"{type}\"/>");
        else sb.Append($"<param name=\"{param}\" type=\"{type}\"/>");
        sb.AppendLine("</accessor></technique_common></source>");
    }

    private static string UniqueSid(string name, HashSet<string> used)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        string sid = chars.Length == 0 ? "node" : new string(chars);
        if (!char.IsLetter(sid[0]) && sid[0] != '_') sid = "_" + sid;
        string candidate = sid;
        for (int n = 2; !used.Add(candidate); n++) candidate = $"{sid}_{n}";
        return candidate;
    }
}
