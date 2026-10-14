// Local CASC storage details needed to write a storage (wowdev.wiki CASC, verified against the live install before use):
// Jenkins lookup3 hashlittle2, archive entry header checksums A and B, index buckets, and the BLTE encoding-key rule.
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ForeverSnapshot;

internal static class CascFormat
{
    public const int HeaderSize = 0x1E;
    public const int CrossReferenceArea = 16 * HeaderSize;   // every archive starts with one header-only entry per index bucket

    // 9-byte encoding key prefix, as used by the local indices.
    public readonly record struct Key9(ulong High, byte Low)
    {
        public static Key9 From(ReadOnlySpan<byte> key) => new(BinaryPrimitives.ReadUInt64BigEndian(key), key[8]);
        public void Write(Span<byte> target) { BinaryPrimitives.WriteUInt64BigEndian(target, High); target[8] = Low; }
        public int Bucket { get { var x = (byte)(High ^ High >> 8 ^ High >> 16 ^ High >> 24 ^ High >> 32 ^ High >> 40 ^ High >> 48 ^ High >> 56 ^ Low); return (x & 0xF) ^ (x >> 4); } }
        public override string ToString() => $"{High:x16}{Low:x2}";
    }

    public static int Bucket(ReadOnlySpan<byte> key) { byte x = 0; for (var i = 0; i < 9; i++) x ^= key[i]; return (x & 0xF) ^ (x >> 4); }

    // Bob Jenkins' lookup3 hashlittle2: returns (pc, pb).
    public static (uint c, uint b) HashLittle2(ReadOnlySpan<byte> data, uint pc = 0, uint pb = 0)
    {
        var length = data.Length;
        uint a, b, c; a = b = c = 0xDEADBEEF + (uint)length + pc; c += pb;
        var i = 0;
        while (length > 12)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]); b += BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]); c += BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 8)..]);
            Mix(ref a, ref b, ref c); length -= 12; i += 12;
        }
        if (length == 0) return (c, b);
        Span<byte> tail = stackalloc byte[12]; tail.Clear(); data[i..].CopyTo(tail);
        a += BinaryPrimitives.ReadUInt32LittleEndian(tail); b += BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]); c += BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
        Final(ref a, ref b, ref c);
        return (c, b);
    }

    private static void Mix(ref uint a, ref uint b, ref uint c)
    {
        a -= c; a ^= uint.RotateLeft(c, 4); c += b;
        b -= a; b ^= uint.RotateLeft(a, 6); a += c;
        c -= b; c ^= uint.RotateLeft(b, 8); b += a;
        a -= c; a ^= uint.RotateLeft(c, 16); c += b;
        b -= a; b ^= uint.RotateLeft(a, 19); a += c;
        c -= b; c ^= uint.RotateLeft(b, 4); b += a;
    }

    private static void Final(ref uint a, ref uint b, ref uint c)
    {
        c ^= b; c -= uint.RotateLeft(b, 14);
        a ^= c; a -= uint.RotateLeft(c, 11);
        b ^= a; b -= uint.RotateLeft(a, 25);
        c ^= b; c -= uint.RotateLeft(b, 16);
        a ^= c; a -= uint.RotateLeft(c, 4);
        b ^= a; b -= uint.RotateLeft(a, 14);
        c ^= b; c -= uint.RotateLeft(b, 24);
    }

    // ChecksumA: hashlittle over the first 0x16 header bytes, seed 0x3D6BE971.
    public static uint ChecksumA(ReadOnlySpan<byte> header) => HashLittle2(header[..0x16], 0x3D6BE971).c;

    // ChecksumB: the first 0x1A header bytes folded into four lanes by position, XORed with the encoded archive position. The table
    // was recovered from live entries (all 16 values consistent over 400 samples; the first four match the published ones).
    private static readonly uint[] TableB =
    {
        0x049396B8, 0x72A82A9B, 0xEE626CCA, 0x9917754F, 0x15DE40B1, 0xF5A8A9B6, 0x421EAC7E, 0xA9D55C9A,
        0x317FD40C, 0x04FAF80D, 0x3D6BE971, 0x52933CFD, 0x27F64B7D, 0xC6F5C11B, 0xD5757E3A, 0x6C388745,
    };

    public static uint ChecksumB(ReadOnlySpan<byte> header, int archive, int offset)
    {
        var o = (uint)(offset & 0x3FFFFFFF) | (uint)(archive & 3) << 30;
        var encoded = TableB[(o + 0x1E) & 0xF] ^ (o + 0x1E);
        Span<byte> lanes = stackalloc byte[4]; lanes.Clear();
        for (var i = 0; i < 0x1A; i++) lanes[(int)(((uint)i + o) & 3)] ^= header[i];
        uint result = 0;
        for (var j = 0; j < 4; j++)
        {
            var lane = (int)(((uint)j + 0x1A + o) & 3);
            result |= (uint)(byte)(lanes[lane] ^ (byte)(encoded >> (8 * lane))) << (8 * j);
        }
        return result;
    }

    public static void WriteChecksums(Span<byte> header, int archive, int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x16..], ChecksumA(header));
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x1A..], ChecksumB(header, archive, offset));
    }

    public static bool ChecksumsValid(ReadOnlySpan<byte> header, int archive, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(header[0x16..]) == ChecksumA(header) && BinaryPrimitives.ReadUInt32LittleEndian(header[0x1A..]) == ChecksumB(header, archive, offset);

    // The header stores the encoding key byte-reversed; only the first 9 key bytes are significant.
    public static bool HeaderKeyMatches(ReadOnlySpan<byte> header, Key9 key)
    {
        Span<byte> k = stackalloc byte[9]; key.Write(k);
        for (var i = 0; i < 9; i++) if (header[15 - i] != k[i]) return false;
        return true;
    }

    // Encoding key = MD5 of the BLTE header (chunk table) or, for a single-chunk stream, of the whole stream.
    public static bool BlteMatchesKey(ReadOnlySpan<byte> blte, Key9 key)
    {
        if (blte.Length < 8 || blte[0] != (byte)'B' || blte[1] != (byte)'L' || blte[2] != (byte)'T' || blte[3] != (byte)'E') return false;
        var headerSize = BinaryPrimitives.ReadInt32BigEndian(blte[4..]);
        if (headerSize < 0 || headerSize > blte.Length) return false;
        Span<byte> md5 = stackalloc byte[16];
        MD5.HashData(headerSize == 0 ? blte : blte[..headerSize], md5);
        return Key9.From(md5) == key;
    }
}
