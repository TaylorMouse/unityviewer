using System.Buffers.Binary;
using System.Reflection;

namespace UnityBrowser.Audio;

/// <summary>
/// Rebuilds a standard Ogg Vorbis stream from FSB5 Vorbis data.
/// FMOD strips the Vorbis headers: the identification and comment headers are rebuilt from the
/// sample info, and the setup header is looked up by CRC32 in a table of known FMOD setup packets
/// (from vgmstream / HearthSim python-fsb5). Audio packets are stored as [u16 size][packet].
/// </summary>
public static class FsbVorbis
{
    private const int ShortBlock = 256, LongBlock = 2048; // FMOD's fixed Vorbis block sizes

    private static readonly Lazy<Dictionary<uint, byte[]>> Setups = new(LoadSetups);

    public static byte[] ToOgg(FsbFile fsb, FsbSample s)
    {
        if (!Setups.Value.TryGetValue(s.VorbisSetupCrc, out var setup))
            throw new NotSupportedException($"Unknown FMOD Vorbis setup header (CRC 0x{s.VorbisSetupCrc:X8}).");

        bool[] modeIsLong = ReadModeBlockFlags(setup);
        int modeBits = ILog(modeIsLong.Length - 1);

        var ogg = new OggWriter(serial: 0x55424F47); // "UBOG"
        ogg.AddPacket(IdentificationHeader(s.Channels, s.SampleRate), granule: 0, flush: true, bos: true);
        ogg.AddPacket(CommentHeader(), granule: 0, flush: false);
        ogg.AddPacket(setup, granule: 0, flush: true);

        // Walk the packets, tracking granule positions: each packet yields prev/4 + cur/4 samples.
        var d = fsb.Data;
        int pos = s.DataOffset, end = s.DataOffset + s.DataSize;
        long granule = 0;
        int prevBlock = 0;
        var packets = new List<(int Offset, int Size)>();
        while (pos + 2 <= end)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos));
            if (size == 0 || size == 0xFFFF || pos + 2 + size > end) break;
            packets.Add((pos + 2, size));
            pos += 2 + size;
        }

        for (int i = 0; i < packets.Count; i++)
        {
            var (off, size) = packets[i];
            int mode = modeBits == 0 ? 0 : (d[off] >> 1) & ((1 << modeBits) - 1);
            int block = mode < modeIsLong.Length && modeIsLong[mode] ? LongBlock : ShortBlock;
            if (prevBlock != 0) granule += prevBlock / 4 + block / 4;
            prevBlock = block;

            bool last = i == packets.Count - 1;
            long g = last && s.SampleCount > 0 ? Math.Min(granule, s.SampleCount) : granule;
            ogg.AddPacket(d.AsSpan(off, size), g, flush: last, eos: last);
        }
        return ogg.ToArray();
    }

    private static byte[] IdentificationHeader(int channels, int rate)
    {
        var b = new byte[30];
        b[0] = 1;
        "vorbis"u8.CopyTo(b.AsSpan(1));
        b[11] = (byte)channels;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), rate);
        b[28] = (11 << 4) | 8; // blocksize_1 = 2048 (high nibble), blocksize_0 = 256 (low nibble)
        b[29] = 1;             // framing
        return b;
    }

    private static byte[] CommentHeader()
    {
        var b = new byte[0x19];
        b[0] = 3;
        "vorbis"u8.CopyTo(b.AsSpan(1));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(7), 9);
        "UnityBrws"u8.CopyTo(b.AsSpan(11));
        b[0x18] = 1;
        return b;
    }

    /// <summary>
    /// Reads the mode block flags by scanning the setup header backwards from its framing bit,
    /// the same technique as FFmpeg's vorbis_parser (modes are the last thing in the setup header).
    /// </summary>
    private static bool[] ReadModeBlockFlags(byte[] setup)
    {
        var bits = new BackwardBits(setup);
        while (bits.Remaining > 97 && bits.Read(1) == 0) { }  // skip padding up to the framing bit
        long afterFraming = bits.Position;

        int modeCount = 0, lastValid = 0;
        while (bits.Remaining >= 97)
        {
            if (bits.Read(8) > 63 || bits.Read(16) != 0 || bits.Read(16) != 0) break;
            bits.Read(1);
            modeCount++;
            if (modeCount > 64) break;
            long save = bits.Position;
            if (bits.Read(6) + 1 == modeCount) lastValid = modeCount;
            bits.Position = save;
        }
        if (lastValid == 0) throw new InvalidDataException("Could not locate the Vorbis mode table.");

        var flags = new bool[lastValid];
        bits.Position = afterFraming;
        for (int i = lastValid - 1; i >= 0; i--)
        {
            bits.Read(40);
            flags[i] = bits.Read(1) != 0;
        }
        return flags;
    }

    private static int ILog(int v)
    {
        int r = 0;
        while (v > 0) { r++; v >>= 1; }
        return r;
    }

    /// <summary>Reads a Vorbis (LSB-first) bitstream from the end towards the start.</summary>
    private sealed class BackwardBits
    {
        private readonly byte[] _data;
        public BackwardBits(byte[] data)
        {
            _data = data;
            Position = data.Length * 8L - 1;
        }

        /// <summary>Index of the next bit to read (counting down).</summary>
        public long Position { get; set; }
        public long Remaining => Position + 1;

        /// <summary>Reads n bits; the first bit read is the field's most significant bit.</summary>
        public uint Read(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                if (Position < 0) return v;
                int bit = (_data[Position >> 3] >> (int)(Position & 7)) & 1;
                v = (v << 1) | (uint)bit;
                Position--;
            }
            return v;
        }
    }

    private static Dictionary<uint, byte[]> LoadSetups()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("UnityBrowser.fsb_vorbis_setups.bin")
                      ?? throw new InvalidOperationException("Embedded FSB Vorbis setup table is missing.");
        var all = new byte[s.Length];
        s.ReadExactly(all);
        if (!all.AsSpan(0, 4).SequenceEqual("FVS1"u8)) throw new InvalidDataException("Bad setup table.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(4));
        var map = new Dictionary<uint, byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var e = all.AsSpan(8 + i * 12);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(e);
            int off = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
            int len = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
            map[crc] = all.AsSpan(off, len).ToArray();
        }
        return map;
    }
}

/// <summary>Minimal Ogg page writer.</summary>
internal sealed class OggWriter
{
    private static readonly uint[] CrcTable = BuildCrc();

    private readonly MemoryStream _out = new();
    private readonly int _serial;
    private int _sequence;
    private readonly List<byte> _segments = new();
    private readonly MemoryStream _body = new();
    private long _granule = -1;
    private bool _bos;

    public OggWriter(int serial) => _serial = serial;

    public void AddPacket(ReadOnlySpan<byte> packet, long granule, bool flush, bool bos = false, bool eos = false)
    {
        if (bos) _bos = true;
        int laces = packet.Length / 255 + 1;
        if (_segments.Count + laces > 255 || _body.Length > 8192) WritePage(eos: false);

        int remaining = packet.Length;
        while (remaining >= 255)
        {
            _segments.Add(255);
            remaining -= 255;
        }
        _segments.Add((byte)remaining);
        _body.Write(packet);
        _granule = granule;

        if (flush || eos) WritePage(eos);
    }

    private void WritePage(bool eos)
    {
        if (_segments.Count == 0) return;
        var header = new byte[27 + _segments.Count];
        "OggS"u8.CopyTo(header);
        header[5] = (byte)((_bos ? 2 : 0) | (eos ? 4 : 0));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), _granule);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), _serial);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), _sequence++);
        header[26] = (byte)_segments.Count;
        for (int i = 0; i < _segments.Count; i++) header[27 + i] = _segments[i];

        var body = _body.ToArray();
        uint crc = Crc(Crc(0, header), body);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), crc);
        _out.Write(header);
        _out.Write(body);

        _segments.Clear();
        _body.SetLength(0);
        _bos = false;
    }

    public byte[] ToArray()
    {
        WritePage(eos: true);
        return _out.ToArray();
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
        return crc;
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }
}
