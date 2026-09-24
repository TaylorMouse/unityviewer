using System.Buffers.Binary;

namespace UnityBrowser.Unity;

/// <summary>A Hermite key: value plus incoming/outgoing slopes (units per second).</summary>
public sealed class CurveKey
{
    public float Time { get; init; }
    public float Value { get; set; }
    public float InSlope { get; set; }
    public float OutSlope { get; set; }
    /// <summary>The segment leaving this key holds its value until the next key.</summary>
    public bool Stepped { get; set; }
}

public sealed class AnimationCurve
{
    public List<CurveKey> Keys { get; } = new();

    /// <summary>Cubic Hermite evaluation (what Unity does between keys).</summary>
    public float Evaluate(float t)
    {
        if (Keys.Count == 0) return 0;
        if (t <= Keys[0].Time) return Keys[0].Value;
        if (t >= Keys[^1].Time) return Keys[^1].Value;
        int i = Keys.FindLastIndex(k => k.Time <= t);
        var a = Keys[i];
        var b = Keys[i + 1];
        if (a.Stepped) return a.Value;
        float dt = b.Time - a.Time;
        if (dt <= 0) return b.Value;
        float s = (t - a.Time) / dt, s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * a.Value + (s3 - 2 * s2 + s) * dt * a.OutSlope
             + (-2 * s3 + 3 * s2) * b.Value + (s3 - s2) * dt * b.InSlope;
    }
}

public sealed class ClipBinding
{
    public uint Path { get; init; }
    public uint Attribute { get; init; }
    public int TypeId { get; init; }
    public int CustomType { get; init; }
    public int FirstCurve { get; init; }
    public int CurveCount { get; init; }

    public bool IsTransform => TypeId == 4 && CustomType == 0;
    public const uint Position = 1, Rotation = 2, Scale = 3, Euler = 4;
}

public sealed class ClipData
{
    public string Name { get; init; } = "";
    public float SampleRate { get; init; }
    public float StartTime { get; init; }
    public float StopTime { get; init; }
    public List<ClipBinding> Bindings { get; } = new();
    public AnimationCurve[] Curves { get; init; } = Array.Empty<AnimationCurve>();
}

/// <summary>
/// Decodes Mecanim AnimationClips (m_MuscleClip). Curves are laid out in binding order and split into
/// streamed (Hermite keyframes), dense (per-frame samples) and constant parts, in that order.
/// </summary>
public static class AnimationClipReader
{
    public static bool CanRead(ObjectInfo o) => o.TypeName == "AnimationClip";

    public static ClipData Read(SerializedFile sf, ObjectInfo o)
    {
        var root = AssetPreview.ReadObject(sf, o);
        var muscle = root["m_MuscleClip"] ?? throw new NotSupportedException("Legacy (non-Mecanim) AnimationClips are not supported yet.");
        var clip = muscle["m_Clip"]?["data"] ?? muscle["m_Clip"] ?? throw new InvalidDataException("AnimationClip has no clip data.");

        var streamed = clip["m_StreamedClip"];
        var dense = clip["m_DenseClip"];
        var constant = clip["m_ConstantClip"];
        int streamedCount = Convert.ToInt32(streamed?["curveCount"]?.Value ?? 0);
        int denseCount = Convert.ToInt32(dense?["m_CurveCount"]?.Value ?? 0);
        var constantValues = ReadFloats(sf, constant?["data"]);
        float start = Convert.ToSingle(muscle["m_StartTime"]?.Value ?? 0f);
        float stop = Convert.ToSingle(muscle["m_StopTime"]?.Value ?? 0f);

        int total = streamedCount + denseCount + constantValues.Length;
        var curves = Enumerable.Range(0, total).Select(_ => new AnimationCurve()).ToArray();

        ReadStreamed(sf, streamed?["data"], curves);
        ReadDense(sf, dense, streamedCount, curves);
        for (int i = 0; i < constantValues.Length; i++)
        {
            var c = curves[streamedCount + denseCount + i];
            c.Keys.Add(new CurveKey { Time = start, Value = constantValues[i] });
            if (stop > start) c.Keys.Add(new CurveKey { Time = stop, Value = constantValues[i] });
        }

        var data = new ClipData
        {
            Name = root["m_Name"]?.Value as string ?? "",
            SampleRate = Convert.ToSingle(root["m_SampleRate"]?.Value ?? 30f),
            StartTime = start,
            StopTime = stop,
            Curves = curves,
        };

        int next = 0;
        foreach (var b in root["m_ClipBindingConstant"]?["genericBindings"]?.Children ?? new List<FieldValue>())
        {
            int typeId = Convert.ToInt32(b["typeID"]?.Value);
            uint attribute = Convert.ToUInt32(b["attribute"]?.Value);
            int count = typeId == 4 ? attribute switch { 2 => 4, 1 or 3 or 4 => 3, _ => 1 } : 1;
            data.Bindings.Add(new ClipBinding
            {
                Path = Convert.ToUInt32(b["path"]?.Value),
                Attribute = attribute,
                TypeId = typeId,
                CustomType = Convert.ToInt32(b["customType"]?.Value),
                FirstCurve = next,
                CurveCount = count,
            });
            next += count;
        }
        return data;
    }

    private static float[] ReadFloats(SerializedFile sf, FieldValue? array)
    {
        if (array?.Value is not PrimitiveArray a || a.Count == 0) return Array.Empty<float>();
        var result = new float[a.Count];
        for (int i = 0; i < a.Count; i++) result[i] = BinaryPrimitives.ReadSingleLittleEndian(sf.Data.AsSpan((int)a.Offset + i * 4));
        return result;
    }

    /// <summary>
    /// Streamed clip: a sequence of frames { float time; uint keyCount; keys { int curve; float c0, c1, c2, c3 } },
    /// where c3 is the key value, c2 the outgoing slope, and c0..c3 the cubic to the curve's next key.
    /// The first and last frames are sentinels.
    /// </summary>
    private static void ReadStreamed(SerializedFile sf, FieldValue? dataField, AnimationCurve[] curves)
    {
        if (dataField?.Value is not PrimitiveArray words || words.Count == 0) return;
        var d = sf.Data.AsSpan((int)words.Offset, words.ByteLength);
        var frames = new List<(float Time, List<(int Curve, float C0, float C1, float C2, float C3)> Keys)>();
        int p = 0;
        while (p + 8 <= d.Length)
        {
            float time = BinaryPrimitives.ReadSingleLittleEndian(d[p..]);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
            p += 8;
            if (count < 0 || p + count * 20L > d.Length) break;
            var keys = new List<(int, float, float, float, float)>(count);
            for (int k = 0; k < count; k++, p += 20)
                keys.Add((BinaryPrimitives.ReadInt32LittleEndian(d[p..]),
                          BinaryPrimitives.ReadSingleLittleEndian(d[(p + 4)..]),
                          BinaryPrimitives.ReadSingleLittleEndian(d[(p + 8)..]),
                          BinaryPrimitives.ReadSingleLittleEndian(d[(p + 12)..]),
                          BinaryPrimitives.ReadSingleLittleEndian(d[(p + 16)..])));
            frames.Add((time, keys));
        }

        var previous = new (float Time, float C0, float C1, float C2, float C3)?[curves.Length];
        for (int f = 1; f < frames.Count - 1; f++)
        {
            var (time, keys) = frames[f];
            foreach (var (curve, c0, c1, c2, c3) in keys)
            {
                if ((uint)curve >= curves.Length) continue;
                var key = new CurveKey { Time = time, Value = c3, OutSlope = c2 };
                if (previous[curve] is { } prev)
                {
                    float dx = time - prev.Time;
                    var last = curves[curve].Keys[^1];
                    if (prev.C0 == 0 && prev.C1 == 0 && prev.C2 == 0 && c3 != prev.C3)
                    {
                        last.Stepped = true; // constant segment that jumps: a stepped key
                        key.InSlope = 0;
                    }
                    else
                    {
                        key.InSlope = 3 * prev.C0 * dx * dx + 2 * prev.C1 * dx + prev.C2;
                    }
                }
                else
                {
                    key.InSlope = c2;
                }
                curves[curve].Keys.Add(key);
                previous[curve] = (time, c0, c1, c2, c3);
            }
        }
    }

    /// <summary>Dense clip: frameCount x curveCount samples at a fixed rate; kept as linear per-frame keys.</summary>
    private static void ReadDense(SerializedFile sf, FieldValue? dense, int firstCurve, AnimationCurve[] curves)
    {
        if (dense == null) return;
        int frames = Convert.ToInt32(dense["m_FrameCount"]?.Value ?? 0);
        int count = Convert.ToInt32(dense["m_CurveCount"]?.Value ?? 0);
        float rate = Convert.ToSingle(dense["m_SampleRate"]?.Value ?? 30f);
        float begin = Convert.ToSingle(dense["m_BeginTime"]?.Value ?? 0f);
        var samples = ReadFloats(sf, dense["m_SampleArray"]);
        if (frames <= 0 || count <= 0 || samples.Length < frames * count || rate <= 0) return;

        for (int c = 0; c < count; c++)
        {
            var curve = curves[firstCurve + c];
            for (int f = 0; f < frames; f++)
            {
                float v = samples[f * count + c];
                float prev = f > 0 ? samples[(f - 1) * count + c] : v;
                float next = f < frames - 1 ? samples[(f + 1) * count + c] : v;
                curve.Keys.Add(new CurveKey
                {
                    Time = begin + f / rate,
                    Value = v,
                    InSlope = (v - prev) * rate,
                    OutSlope = (next - v) * rate,
                });
            }
        }
    }
}
