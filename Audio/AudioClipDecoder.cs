using System.Buffers.Binary;
using UnityBrowser.Unity;

namespace UnityBrowser.Audio;

/// <summary>Decoded audio: interleaved signed 16-bit PCM.</summary>
public sealed class DecodedAudio
{
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required short[] Samples { get; init; }
    public List<PropertyInfo> Info { get; } = new();

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / Channels / SampleRate);
}

public static class AudioClipDecoder
{
    public static bool CanDecode(ObjectInfo o) => o.TypeName == "AudioClip";

    public static DecodedAudio Decode(SerializedFile sf, ObjectInfo o)
    {
        var root = AssetPreview.ReadObject(sf, o);
        var res = root["m_Resource"] ?? throw new NotSupportedException("This AudioClip layout (pre Unity 5) is not supported.");
        string source = res["m_Source"]?.Value as string ?? "";
        long offset = Convert.ToInt64(res["m_Offset"]?.Value ?? 0ul);
        long size = Convert.ToInt64(res["m_Size"]?.Value ?? 0ul);
        if (size <= 0 || source.Length == 0) throw new InvalidDataException("AudioClip has no audio data.");
        if (sf.Owner == null) throw new InvalidOperationException("No owning file to read the audio from.");

        var fsb = FsbFile.Parse(sf.Owner.ReadResource(source, offset, size));
        int index = Convert.ToInt32(root["m_SubsoundIndex"]?.Value ?? 0);
        if (index < 0 || index >= fsb.Samples.Count) index = 0;
        var sample = fsb.Samples[index];

        short[] pcm = fsb.Codec switch
        {
            0x0F => DecodeVorbis(fsb, sample),
            0x01 => DecodePcm8(fsb, sample),
            0x02 => DecodePcm16(fsb, sample),
            0x05 => DecodePcmFloat(fsb, sample),
            _ => throw new NotSupportedException($"FMOD {FsbFile.CodecName(fsb.Codec)} audio is not supported yet."),
        };

        var audio = new DecodedAudio { Channels = sample.Channels, SampleRate = sample.SampleRate, Samples = pcm };
        audio.Info.Add(new("Duration", FormatTime(audio.Duration)));
        audio.Info.Add(new("Channels", sample.Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{sample.Channels}" }));
        audio.Info.Add(new("Sample rate", $"{sample.SampleRate:N0} Hz"));
        audio.Info.Add(new("Codec", $"FMOD FSB5 {FsbFile.CodecName(fsb.Codec)}"));
        audio.Info.Add(new("Audio data", $"{size / (1024.0 * 1024):0.0} MB ({Path.GetFileName(source)} @ 0x{offset:X})"));
        if (fsb.Samples.Count > 1) audio.Info.Add(new("Subsound", $"{index + 1} of {fsb.Samples.Count}"));
        return audio;
    }

    public static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static short[] DecodeVorbis(FsbFile fsb, FsbSample s)
    {
        var ogg = FsbVorbis.ToOgg(fsb, s);
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
        long expected = s.SampleCount > 0 ? s.SampleCount * s.Channels : 0;
        var output = new List<short>(expected > 0 ? (int)expected : 1 << 20);
        var buffer = new float[s.Channels * 8192];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                output.Add((short)Math.Clamp(buffer[i] * 32767f, -32768f, 32767f));
        }
        return output.ToArray();
    }

    private static short[] DecodePcm8(FsbFile fsb, FsbSample s)
    {
        var src = fsb.Data.AsSpan(s.DataOffset, s.DataSize);
        var pcm = new short[src.Length];
        for (int i = 0; i < src.Length; i++) pcm[i] = (short)((src[i] - 128) << 8);
        return pcm;
    }

    private static short[] DecodePcm16(FsbFile fsb, FsbSample s)
    {
        var src = fsb.Data.AsSpan(s.DataOffset, s.DataSize & ~1);
        var pcm = new short[src.Length / 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(src[(i * 2)..]);
        return pcm;
    }

    private static short[] DecodePcmFloat(FsbFile fsb, FsbSample s)
    {
        var src = fsb.Data.AsSpan(s.DataOffset, s.DataSize & ~3);
        var pcm = new short[src.Length / 4];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(src[(i * 4)..]) * 32767f, -32768f, 32767f);
        return pcm;
    }
}

public static class WavWriter
{
    public static void Write(string path, DecodedAudio audio)
    {
        int dataBytes = audio.Samples.Length * 2;
        using var fs = File.Create(path);
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), (short)audio.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), audio.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), audio.SampleRate * audio.Channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), (short)(audio.Channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), dataBytes);
        fs.Write(h);
        fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.Samples.AsSpan()));
    }
}
