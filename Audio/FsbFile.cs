using System.Buffers.Binary;

namespace UnityBrowser.Audio;

/// <summary>One sound inside an FMOD FSB5 bank.</summary>
public sealed class FsbSample
{
    public int Channels { get; set; }
    public int SampleRate { get; set; }
    public long SampleCount { get; init; }
    /// <summary>Offset of the sample's data inside the FSB buffer.</summary>
    public int DataOffset { get; init; }
    public int DataSize { get; set; }
    public uint VorbisSetupCrc { get; set; }
}

/// <summary>
/// FMOD FSB5 sound bank, as used by Unity for AudioClip data.
/// Layout follows vgmstream's fsb5.c.
/// </summary>
public sealed class FsbFile
{
    public int Version { get; private init; }
    public int Codec { get; private init; }
    public List<FsbSample> Samples { get; } = new();
    public byte[] Data { get; private init; } = Array.Empty<byte>();

    public static string CodecName(int codec) => codec switch
    {
        0x01 => "PCM8", 0x02 => "PCM16", 0x03 => "PCM24", 0x04 => "PCM32", 0x05 => "PCM float",
        0x06 => "GC ADPCM", 0x07 => "IMA ADPCM", 0x08 => "VAG", 0x09 => "HEVAG", 0x0A => "XMA",
        0x0B => "MPEG", 0x0C => "CELT", 0x0D => "AT9", 0x0E => "XWMA", 0x0F => "Vorbis",
        0x10 => "FADPCM", 0x11 => "Opus",
        _ => $"Codec 0x{codec:X}",
    };

    public static FsbFile Parse(byte[] d)
    {
        if (d.Length < 0x3C || !d.AsSpan(0, 4).SequenceEqual("FSB5"u8))
            throw new InvalidDataException("Audio data is not an FMOD FSB5 bank.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x04));
        int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x08));
        int sampleHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x0C));
        int nameTableSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x10));
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x14));
        int codec = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x18));
        int baseHeaderSize = version == 0 ? 0x40 : 0x3C;
        int dataStart = baseHeaderSize + sampleHeaderSize + nameTableSize;

        var fsb = new FsbFile { Version = version, Codec = codec, Data = d };
        int offset = baseHeaderSize;
        int headerEnd = baseHeaderSize + sampleHeaderSize;

        for (int i = 0; i < count; i++)
        {
            ulong mode = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(offset));
            offset += 8;

            var sample = new FsbSample
            {
                SampleCount = (long)((mode >> 34) & 0x3FFFFFFF),
                DataOffset = dataStart + (int)(((mode >> 7) & 0x07FFFFFF) << 5),
                Channels = ((mode >> 5) & 3) switch { 0 => 1, 1 => 2, 2 => 6, _ => 8 },
                SampleRate = ((mode >> 1) & 0xF) switch
                {
                    0 => 4000, 1 => 8000, 2 => 11000, 3 => 11025, 4 => 16000, 5 => 22050,
                    6 => 24000, 7 => 32000, 8 => 44100, 9 => 48000, 10 => 96000, _ => 0,
                },
            };

            if ((mode & 1) != 0)
            {
                while (offset < headerEnd)
                {
                    uint flag = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset));
                    int type = (int)((flag >> 25) & 0x7F);
                    int size = (int)((flag >> 1) & 0xFFFFFF);
                    var payload = d.AsSpan(offset + 4);
                    switch (type)
                    {
                        case 0x01: sample.Channels = payload[0]; break;
                        case 0x02: sample.SampleRate = BinaryPrimitives.ReadInt32LittleEndian(payload); break;
                        case 0x0B: sample.VorbisSetupCrc = BinaryPrimitives.ReadUInt32LittleEndian(payload); break;
                    }
                    offset += 4 + size;
                    if ((flag & 1) == 0) break;
                }
            }
            fsb.Samples.Add(sample);
        }

        // Each sample runs to the start of the next one (or the end of the data section).
        for (int i = 0; i < fsb.Samples.Count; i++)
        {
            int end = i + 1 < fsb.Samples.Count ? fsb.Samples[i + 1].DataOffset : dataStart + dataSize;
            fsb.Samples[i].DataSize = Math.Max(0, Math.Min(end, d.Length) - fsb.Samples[i].DataOffset);
        }
        return fsb;
    }
}
