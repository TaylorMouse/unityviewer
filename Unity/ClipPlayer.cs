namespace UnityBrowser.Unity;

/// <summary>
/// Plays an AnimationClip on the skinned meshes of its rig: evaluates the curves at a time, builds the
/// bone world matrices and deforms the meshes (linear blend skinning on the CPU). Output is right-handed,
/// like <see cref="MeshData"/>.
/// </summary>
public sealed class ClipPlayer
{
    /// <summary>A binding together with the clip whose curves it indexes (layers can come from different clips).</summary>
    private sealed record Ref(ClipData Clip, ClipBinding Binding);

    private sealed class Channels
    {
        public Ref? Position, Rotation, Scale, Euler;
    }

    public sealed class Skin
    {
        public required MeshData Mesh { get; init; }
        public required long[] Bones { get; init; }
        public float[] Positions { get; init; } = Array.Empty<float>();
        public float[]? Normals { get; init; }
    }

    private readonly ClipRig _rig;
    private readonly Dictionary<long, Channels> _animated = new();

    public ClipData Clip => Layered.Primary;
    public LayeredClip Layered { get; }
    public List<Skin> Skins { get; } = new();
    public float StartTime => Layered.StartTime;
    public float Duration => Math.Max(0, Layered.StopTime - Layered.StartTime);
    public int AnimatedTransforms => _animated.Count;

    private ClipPlayer(LayeredClip layered, ClipRig rig)
    {
        Layered = layered;
        _rig = rig;
    }

    public static ClipPlayer Create(SerializedFile sf, ObjectInfo clipObj)
    {
        var layered = ClipLayering.Resolve(sf, clipObj);
        var bindings = layered.AllTransformBindings.ToList();
        if (bindings.Count == 0) throw new NotSupportedException("This clip does not animate any transforms.");
        var rig = ClipRig.Find(sf, bindings)
                  ?? throw new NotSupportedException("No skinned character uses this clip (it animates props; playback is for character animations for now).");

        var player = new ClipPlayer(layered, rig);
        foreach (var (clip, _) in layered.Layers)
            foreach (var b in clip.Bindings.Where(b => b.IsTransform && b.Attribute is >= 1 and <= 4))
                player.Assign(rig, clip, b);

        foreach (var (meshObj, bones) in rig.Skins)
        {
            var mesh = MeshReader.Read(sf, meshObj);
            if (!mesh.IsSkinned || mesh.BindPoses.Count != bones.Length || mesh.BoneIndices == null || mesh.BoneWeights == null) continue;
            SkeletonReader.Read(sf, meshObj, mesh); // repairs broken bind poses in place
            player.Skins.Add(new Skin
            {
                Mesh = mesh,
                Bones = bones,
                Positions = new float[mesh.VertexCount * 3],
                Normals = mesh.Normals != null ? new float[mesh.VertexCount * 3] : null,
            });
        }
        if (player.Skins.Count == 0) throw new InvalidDataException("The rig's skinned meshes could not be read.");
        return player;
    }

    /// <summary>Later layers override earlier ones per property (a rotation replaces an Euler rotation and vice versa).</summary>
    private void Assign(ClipRig rig, ClipData clip, ClipBinding b)
    {
        long id = b.Path == 0 ? rig.Root : rig.Resolved.GetValueOrDefault(b.Path);
        if (!rig.Transforms.ContainsKey(id)) return;
        if (!_animated.TryGetValue(id, out var ch)) _animated[id] = ch = new Channels();
        var r = new Ref(clip, b);
        switch (b.Attribute)
        {
            case ClipBinding.Position: ch.Position = r; break;
            case ClipBinding.Scale: ch.Scale = r; break;
            case ClipBinding.Rotation: ch.Rotation = r; ch.Euler = null; break;
            case ClipBinding.Euler: ch.Euler = r; ch.Rotation = null; break;
        }
    }

    /// <summary>Poses the rig at clip time <paramref name="time"/> and writes the deformed meshes into <see cref="Skins"/>.</summary>
    public void Evaluate(float time)
    {
        var world = new Dictionary<long, double[]>();
        double[] World(long id)
        {
            if (world.TryGetValue(id, out var m)) return m;
            var t = _rig.Transforms[id];
            var local = Local(t, time);
            m = _rig.Transforms.ContainsKey(t.Parent) ? Mat4.Multiply(World(t.Parent), local) : local;
            world[id] = m;
            return m;
        }

        foreach (var skin in Skins)
        {
            var mesh = skin.Mesh;
            var matrices = new double[skin.Bones.Length][];
            for (int s = 0; s < skin.Bones.Length; s++)
                matrices[s] = Mat4.Multiply(World(skin.Bones[s]), mesh.BindPoses[s]);

            var src = mesh.Positions;
            var nsrc = mesh.Normals;
            var dst = skin.Positions;
            var ndst = skin.Normals;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                double x = src[v * 3], y = src[v * 3 + 1], z = src[v * 3 + 2];
                double px = 0, py = 0, pz = 0, nx = 0, ny = 0, nz = 0, total = 0;
                for (int k = 0; k < 4; k++)
                {
                    float w = mesh.BoneWeights![v * 4 + k];
                    int slot = mesh.BoneIndices![v * 4 + k];
                    if (w <= 0 || (uint)slot >= matrices.Length) continue;
                    var m = matrices[slot];
                    px += w * (m[0] * x + m[1] * y + m[2] * z + m[3]);
                    py += w * (m[4] * x + m[5] * y + m[6] * z + m[7]);
                    pz += w * (m[8] * x + m[9] * y + m[10] * z + m[11]);
                    if (nsrc != null)
                    {
                        double a = nsrc[v * 3], b = nsrc[v * 3 + 1], c = nsrc[v * 3 + 2];
                        nx += w * (m[0] * a + m[1] * b + m[2] * c);
                        ny += w * (m[4] * a + m[5] * b + m[6] * c);
                        nz += w * (m[8] * a + m[9] * b + m[10] * c);
                    }
                    total += w;
                }
                if (total <= 0) { px = x; py = y; pz = z; }
                dst[v * 3] = (float)px; dst[v * 3 + 1] = (float)py; dst[v * 3 + 2] = (float)pz;
                if (ndst != null)
                {
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len < 1e-12) { nx = nsrc![v * 3]; ny = nsrc[v * 3 + 1]; nz = nsrc[v * 3 + 2]; len = 1; }
                    ndst[v * 3] = (float)(nx / len); ndst[v * 3 + 1] = (float)(ny / len); ndst[v * 3 + 2] = (float)(nz / len);
                }
            }
        }
    }

    /// <summary>Local transform at a time: animated channels from the clip, the rest from the rig's rest pose.</summary>
    private double[] Local(SceneTransform t, float time)
    {
        _animated.TryGetValue(t.Id, out var ch);
        float C(Ref? r, int i, float rest) => r == null ? rest : r.Clip.Curves[r.Binding.FirstCurve + i].Evaluate(time);

        double px = C(ch?.Position, 0, t.Position[0]), py = C(ch?.Position, 1, t.Position[1]), pz = C(ch?.Position, 2, t.Position[2]);
        double sx = C(ch?.Scale, 0, t.Scale[0]), sy = C(ch?.Scale, 1, t.Scale[1]), sz = C(ch?.Scale, 2, t.Scale[2]);

        double qx, qy, qz, qw;
        if (ch?.Euler is { } e)
        {
            // Unity Euler angles (degrees) apply Z, then X, then Y: q = qY * qX * qZ.
            (qx, qy, qz, qw) = EulerToQuaternion(C(e, 0, 0), C(e, 1, 0), C(e, 2, 0));
        }
        else
        {
            qx = C(ch?.Rotation, 0, t.Rotation[0]); qy = C(ch?.Rotation, 1, t.Rotation[1]);
            qz = C(ch?.Rotation, 2, t.Rotation[2]); qw = C(ch?.Rotation, 3, t.Rotation[3]);
        }
        double len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (len < 1e-9) { qx = qy = qz = 0; qw = len = 1; }

        // Mirror X for right-handed output: position.x and quaternion y/z negate.
        return Mat4.Trs(-px, py, pz, qx / len, -qy / len, -qz / len, qw / len, sx, sy, sz);
    }

    private static (double X, double Y, double Z, double W) EulerToQuaternion(double xDeg, double yDeg, double zDeg)
    {
        double hx = xDeg * Math.PI / 360, hy = yDeg * Math.PI / 360, hz = zDeg * Math.PI / 360;
        (double X, double Y, double Z, double W) qx = (Math.Sin(hx), 0, 0, Math.Cos(hx));
        (double X, double Y, double Z, double W) qy = (0, Math.Sin(hy), 0, Math.Cos(hy));
        (double X, double Y, double Z, double W) qz = (0, 0, Math.Sin(hz), Math.Cos(hz));
        return Mul(Mul(qy, qx), qz);

        static (double, double, double, double) Mul((double X, double Y, double Z, double W) a, (double X, double Y, double Z, double W) b) => (
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }
}
