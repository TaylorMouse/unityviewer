using System.Buffers.Binary;
using System.Text;

namespace UnityBrowser.Unity;

/// <summary>Bounds-checked reader over a byte array with switchable endianness.</summary>
public sealed class EndianReader
{
    private readonly byte[] _data;

    public EndianReader(byte[] data, bool bigEndian, long position = 0, long alignBase = 0)
    {
        _data = data;
        BigEndian = bigEndian;
        Position = position;
        AlignBase = alignBase;
    }

    public byte[] Data => _data;
    public long Position { get; set; }
    public bool BigEndian { get; set; }

    /// <summary>Alignment is computed relative to this offset (start of the containing file).</summary>
    public long AlignBase { get; set; }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position < 0 || Position + count > _data.Length)
            throw new EndOfStreamException($"Read of {count} bytes at 0x{Position:X} is out of range.");
        var span = new ReadOnlySpan<byte>(_data, (int)Position, count);
        Position += count;
        return span;
    }

    public byte U8() => Take(1)[0];
    public sbyte I8() => (sbyte)Take(1)[0];
    public bool Bool() => Take(1)[0] != 0;
    public short I16() => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(Take(2)) : BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort U16() => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int I32() => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint U32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long I64() => BigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong U64() => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BigEndian ? BinaryPrimitives.ReadSingleBigEndian(Take(4)) : BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BigEndian ? BinaryPrimitives.ReadDoubleBigEndian(Take(8)) : BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public byte[] Bytes(int count) => Take(count).ToArray();

    public void Skip(long count)
    {
        if (count < 0 || Position + count > _data.Length)
            throw new EndOfStreamException($"Skip of {count} bytes at 0x{Position:X} is out of range.");
        Position += count;
    }

    public void Align(int alignment)
    {
        long rel = Position - AlignBase;
        long pad = (alignment - rel % alignment) % alignment;
        Position += pad;
    }

    /// <summary>Null-terminated UTF-8 string.</summary>
    public string CString(int maxLength = 32768)
    {
        long start = Position;
        long end = start;
        long limit = Math.Min(_data.Length, start + maxLength);
        while (end < limit && _data[end] != 0) end++;
        if (end >= limit) throw new InvalidDataException($"Unterminated string at 0x{start:X}.");
        Position = end + 1;
        return Encoding.UTF8.GetString(_data, (int)start, (int)(end - start));
    }

    /// <summary>Unity serialised string: int32 length, bytes, align to 4.</summary>
    public string AlignedString(int maxLength = 1 << 24)
    {
        int len = I32();
        if (len < 0 || len > maxLength) throw new InvalidDataException($"Bad string length {len} at 0x{Position - 4:X}.");
        var s = Encoding.UTF8.GetString(Take(len));
        Align(4);
        return s;
    }
}
