// Read-only access to the local client's game files (CASC storage), for terrain heights and client tables.
// Formats (wowdev.wiki, validated against the local 1.60.1.70338 install): .build.info, build config, local index v7
// (9-byte encoding keys, 5-byte offsets, 30 offset bits), data.NNN archives (30-byte header + BLTE), BLTE chunks
// N / Z / E (Salsa20 with TACT keys), encoding (CKey -> EKey pages), root TSFM v2 (FileDataID -> CKey), WDC5 tables.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AuthServer.Network;

internal sealed class ForeverCasc : IDisposable
{
    private readonly string dataDirectory;
    private readonly Func<ulong, byte[]> tactKey;
    private readonly object gate = new();
    private readonly Dictionary<int, FileStream> archives = new();
    // Local index: 16 buckets, each with sorted 9-byte keys and (archive, offset, size).
    private readonly byte[][] indexKeys = new byte[16][];
    private readonly (int archive, int offset, int size)[][] indexEntries = new (int, int, int)[16][];
    private byte[] encoding;
    private int encodingPages, encodingPageSize, encodingEKeySize, encodingPagesStart, encodingPageIndexStart;
    private int[] rootIds;
    private byte[] rootKeys;

    private ForeverCasc(string data, Func<ulong, byte[]> keys) { dataDirectory = data; tactKey = keys; }

    // installDirectory holds .build.info and Data/; the build is found by its version (e.g. 1.60.1.70338).
    public static ForeverCasc Open(string installDirectory, string version, Func<ulong, byte[]> tactKey)
    {
        var lines = File.ReadAllLines(Path.Combine(installDirectory, ".build.info"));
        var columns = lines[0].Split('|').Select(c => c.Split('!')[0]).ToArray();
        var row = lines.Skip(1).Select(l => l.Split('|')).FirstOrDefault(r => r.Length == columns.Length && r[Array.IndexOf(columns, "Version")] == version)
            ?? throw new InvalidDataException("Client build " + version + " is not in .build.info.");
        var casc = new ForeverCasc(Path.Combine(installDirectory, "Data"), tactKey);
        var config = Config(casc.ConfigPath("config", row[Array.IndexOf(columns, "Build Key")]));
        casc.LoadIndices();
        casc.LoadEncoding(Hex(config["encoding"][1]));
        casc.LoadRoot(Hex(config["root"][0]));
        return casc;
    }

    public bool Has(int fileDataId) => Array.BinarySearch(rootIds, fileDataId) >= 0;

    public byte[] OpenFile(int fileDataId) => TryOpenFile(fileDataId) ?? throw new FileNotFoundException($"Client file {fileDataId} is not stored locally.");

    public byte[] TryOpenFile(int fileDataId)
    {
        var i = Array.BinarySearch(rootIds, fileDataId);
        if (i < 0) return null;
        var ekeys = EKeys(rootKeys.AsSpan(i * 16, 16));
        foreach (var ekey in ekeys) if (Locate(ekey) is { } location) return Blte(ReadArchive(location), tactKey);
        return null;
    }

    public void Dispose() { lock (gate) { foreach (var stream in archives.Values) stream.Dispose(); archives.Clear(); } }

    private string ConfigPath(string kind, string key) => System.IO.Path.Combine(dataDirectory, kind, key[..2], key[2..4], key);
    private static Dictionary<string, string[]> Config(string path) =>
        File.ReadAllLines(path).Where(l => l.Contains(" = ")).Select(l => l.Split(" = ", 2)).ToDictionary(p => p[0], p => p[1].Split(' '));
    private static byte[] Hex(string text) => Convert.FromHexString(text);

    private static int Bucket(ReadOnlySpan<byte> key) { byte x = 0; for (var i = 0; i < 9; i++) x ^= key[i]; return (x & 0xF) ^ (x >> 4); }

    private void LoadIndices()
    {
        var latest = new Dictionary<int, (uint version, string path)>();
        foreach (var path in Directory.GetFiles(System.IO.Path.Combine(dataDirectory, "data"), "*.idx"))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.Length != 10) continue;
            var bucket = Convert.ToInt32(name[..2], 16); var version = Convert.ToUInt32(name[2..], 16);
            if (!latest.TryGetValue(bucket, out var known) || version > known.version) latest[bucket] = (version, path);
        }
        foreach (var (bucket, (_, path)) in latest)
        {
            var raw = File.ReadAllBytes(path);
            var headerSize = BinaryPrimitives.ReadInt32LittleEndian(raw);
            int sizeBytes = raw[12], offsetBytes = raw[13], keyBytes = raw[14], offsetBits = raw[15];
            var entriesOffset = (8 + headerSize + 15) & ~15;
            var entrySize = keyBytes + offsetBytes + sizeBytes;
            var count = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(entriesOffset)) / entrySize;
            var keys = new byte[count * 9]; var entries = new (int, int, int)[count];
            for (int i = 0, o = entriesOffset + 8; i < count; i++, o += entrySize)
            {
                raw.AsSpan(o, 9).CopyTo(keys.AsSpan(i * 9));
                ulong packed = 0; for (var b = 0; b < offsetBytes; b++) packed = packed << 8 | raw[o + keyBytes + b];
                entries[i] = ((int)(packed >> offsetBits), (int)(packed & ((1UL << offsetBits) - 1)), BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(o + keyBytes + offsetBytes)));
            }
            // Sorted by key for binary search (first occurrence wins, like the client).
            var order = Enumerable.Range(0, count).ToArray();
            Array.Sort(order, (a, b) => { var c = keys.AsSpan(a * 9, 9).SequenceCompareTo(keys.AsSpan(b * 9, 9)); return c != 0 ? c : a.CompareTo(b); });
            indexKeys[bucket] = new byte[count * 9]; indexEntries[bucket] = new (int, int, int)[count];
            for (var i = 0; i < count; i++) { keys.AsSpan(order[i] * 9, 9).CopyTo(indexKeys[bucket].AsSpan(i * 9)); indexEntries[bucket][i] = entries[order[i]]; }
        }
    }

    private (int archive, int offset, int size)? Locate(ReadOnlySpan<byte> ekey)
    {
        var bucket = Bucket(ekey); var keys = indexKeys[bucket];
        if (keys == null) return null;
        int lo = 0, hi = keys.Length / 9 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1; var c = keys.AsSpan(mid * 9, 9).SequenceCompareTo(ekey[..9]);
            if (c == 0) { while (mid > 0 && keys.AsSpan((mid - 1) * 9, 9).SequenceEqual(ekey[..9])) mid--; return indexEntries[bucket][mid]; }
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }

    private byte[] ReadArchive((int archive, int offset, int size) location)
    {
        var buffer = new byte[location.size];
        lock (gate)
        {
            if (!archives.TryGetValue(location.archive, out var stream))
                archives[location.archive] = stream = new FileStream(System.IO.Path.Combine(dataDirectory, "data", $"data.{location.archive:D3}"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = location.offset; stream.ReadExactly(buffer);
        }
        return buffer[30..];   // archive entry header: reversed key, size, flags, checksums
    }

    private void LoadEncoding(byte[] ekey)
    {
        encoding = Blte(ReadArchive(Locate(ekey) ?? throw new InvalidDataException("Encoding file is not stored locally.")), tactKey);
        encodingEKeySize = encoding[4];
        encodingPageSize = BinaryPrimitives.ReadUInt16BigEndian(encoding.AsSpan(5)) * 1024;
        encodingPages = BinaryPrimitives.ReadInt32BigEndian(encoding.AsSpan(9));
        var specSize = BinaryPrimitives.ReadInt32BigEndian(encoding.AsSpan(18));
        encodingPageIndexStart = 22 + specSize;
        encodingPagesStart = encodingPageIndexStart + encodingPages * 32;
    }

    private List<byte[]> EKeys(ReadOnlySpan<byte> ckey)
    {
        int lo = 0, hi = encodingPages - 1, page = -1;
        while (lo <= hi)   // last page whose first CKey <= ckey
        {
            var mid = (lo + hi) >> 1;
            if (encoding.AsSpan(encodingPageIndexStart + mid * 32, 16).SequenceCompareTo(ckey) <= 0) { page = mid; lo = mid + 1; } else hi = mid - 1;
        }
        var result = new List<byte[]>();
        if (page < 0) return result;
        var p = encodingPagesStart + page * encodingPageSize; var end = p + encodingPageSize;
        while (p + 22 <= end && encoding[p] != 0)
        {
            var count = encoding[p];
            if (encoding.AsSpan(p + 6, 16).SequenceEqual(ckey))
            {
                for (var k = 0; k < count; k++) result.Add(encoding.AsSpan(p + 22 + k * encodingEKeySize, encodingEKeySize).ToArray());
                break;
            }
            p += 22 + count * encodingEKeySize;
        }
        return result;
    }

    // Root TSFM v2: blocks of (count, locale, content flags in three parts), FileDataID deltas, CKeys, optional name hashes.
    // enUS / all-locale entries, low-violence variants skipped; first match per FileDataID.
    private void LoadRoot(byte[] ckey)
    {
        var root = Blte(ReadArchive(EKeys(ckey).Select(e => Locate(e)).FirstOrDefault(l => l != null) ?? throw new InvalidDataException("Root file is not stored locally.")), tactKey);
        if (Encoding.ASCII.GetString(root, 0, 4) != "TSFM") throw new InvalidDataException("Unsupported root format.");
        var ids = new List<int>(); var keyOffsets = new List<int>();
        var pos = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(4));
        while (pos + 17 <= root.Length)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(pos)); var locale = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(pos + 4));
            var content = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(pos + 8)) | BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(pos + 12)) | (uint)root[pos + 16] << 17;
            pos += 17;
            var deltas = pos; pos += 4 * count; var ckeys = pos; pos += 16 * count;
            if ((content & 0x10000000) == 0) pos += 8 * count;
            if ((locale & 0x2) == 0 || (content & 0x80) != 0) continue;
            var id = -1;
            for (var i = 0; i < count; i++) { id += 1 + BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(deltas + 4 * i)); ids.Add(id); keyOffsets.Add(ckeys + 16 * i); }
        }
        var order = Enumerable.Range(0, ids.Count).ToArray();
        Array.Sort(order, (a, b) => ids[a] != ids[b] ? ids[a].CompareTo(ids[b]) : a.CompareTo(b));
        var unique = new List<int>(order.Length);
        foreach (var i in order) if (unique.Count == 0 || ids[unique[^1]] != ids[i]) unique.Add(i);
        rootIds = new int[unique.Count]; rootKeys = new byte[unique.Count * 16];
        for (var n = 0; n < unique.Count; n++) { rootIds[n] = ids[unique[n]]; root.AsSpan(keyOffsets[unique[n]], 16).CopyTo(rootKeys.AsSpan(n * 16)); }
    }

    // BLTE: single chunk (header size 0) or a chunk table; chunk modes N (raw), Z (zlib), E (encrypted; unknown key -> zeros).
    public static byte[] Blte(byte[] data, Func<ulong, byte[]> tactKey)
    {
        if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != "BLTE") throw new InvalidDataException("Not a BLTE stream.");
        var headerSize = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
        if (headerSize == 0) return Chunk(data.AsSpan(8), 0, -1, tactKey);
        var count = data[9] << 16 | data[10] << 8 | data[11];
        using var output = new MemoryStream();
        var pos = headerSize;
        for (var i = 0; i < count; i++)
        {
            var compressed = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12 + 24 * i)); var size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16 + 24 * i));
            output.Write(Chunk(data.AsSpan(pos, compressed), i, size, tactKey)); pos += compressed;
        }
        return output.ToArray();
    }

    private static byte[] Chunk(ReadOnlySpan<byte> chunk, int index, int size, Func<ulong, byte[]> tactKey)
    {
        switch ((char)chunk[0])
        {
            case 'N': return chunk[1..].ToArray();
            case 'Z':
                using (var zlib = new ZLibStream(new MemoryStream(chunk[1..].ToArray()), CompressionMode.Decompress))
                using (var output = new MemoryStream()) { zlib.CopyTo(output); return output.ToArray(); }
            case 'E':
                int nameLength = chunk[1]; var name = BinaryPrimitives.ReadUInt64LittleEndian(chunk.Slice(2, 8));
                var p = 2 + nameLength; int ivLength = chunk[p]; var iv = chunk.Slice(p + 1, ivLength); p += 1 + ivLength;
                var key = tactKey?.Invoke(name);
                if (key == null || chunk[p] != 'S') return new byte[Math.Max(size, 0)];   // like the client: unknown key reads as zeros
                Span<byte> nonce = stackalloc byte[8]; nonce.Clear(); iv.CopyTo(nonce);
                for (var i = 0; i < 4; i++) nonce[i] ^= (byte)(index >> (8 * i));
                return Chunk(Salsa20(key, nonce, chunk[(p + 1)..]), index, size, tactKey);
            default: throw new InvalidDataException($"Unsupported BLTE chunk mode {(char)chunk[0]}.");
        }
    }

    // Salsa20/20 with a 16-byte key ("expand 16-byte k"), 64-bit nonce, block counter from 0.
    public static byte[] Salsa20(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> data)
    {
        Span<uint> state = stackalloc uint[16]; Span<uint> x = stackalloc uint[16]; Span<byte> stream = stackalloc byte[64];
        uint K(int i) => BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        state[0] = 0x61707865; state[5] = 0x3120646e; state[10] = 0x79622d36; state[15] = 0x6b206574;
        state[1] = K(0); state[2] = K(1); state[3] = K(2); state[4] = K(3); state[11] = K(0); state[12] = K(1); state[13] = K(2); state[14] = K(3);
        state[6] = BinaryPrimitives.ReadUInt32LittleEndian(nonce); state[7] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);
        var output = new byte[data.Length];
        for (long block = 0; block * 64 < data.Length; block++)
        {
            state[8] = (uint)block; state[9] = (uint)(block >> 32);
            state.CopyTo(x);
            for (var round = 0; round < 10; round++)
            {
                QuarterRound(x, 0, 4, 8, 12); QuarterRound(x, 5, 9, 13, 1); QuarterRound(x, 10, 14, 2, 6); QuarterRound(x, 15, 3, 7, 11);
                QuarterRound(x, 0, 1, 2, 3); QuarterRound(x, 5, 6, 7, 4); QuarterRound(x, 10, 11, 8, 9); QuarterRound(x, 15, 12, 13, 14);
            }
            for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(stream[(i * 4)..], x[i] + state[i]);
            var start = (int)(block * 64);
            for (var i = 0; i < 64 && start + i < data.Length; i++) output[start + i] = (byte)(data[start + i] ^ stream[i]);
        }
        return output;
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[b] ^= uint.RotateLeft(x[a] + x[d], 7); x[c] ^= uint.RotateLeft(x[b] + x[a], 9);
        x[d] ^= uint.RotateLeft(x[c] + x[b], 13); x[a] ^= uint.RotateLeft(x[d] + x[c], 18);
    }
}

// WDC5 client table reader (non-sparse tables): field storage none / bitpacked / common / pallet / pallet array / signed,
// ID list, copy table; sections whose TACT key is unknown read as zeros and are skipped, like the client.
internal sealed class ForeverDb2
{
    private readonly byte[] data;
    private readonly int recordSize, idIndex;
    private readonly (int offsetBits, int sizeBits, int additional, int kind, int a, int b, int c)[] storage;
    private readonly int[] palletOffsets;
    private readonly Dictionary<int, int>[] common;
    private readonly int palletStart;
    private readonly Dictionary<int, int> rows = new();   // id -> record offset
    public uint TableHash { get; }
    public uint LayoutHash { get; }

    public ForeverDb2(byte[] file)
    {
        data = file;
        if (Encoding.ASCII.GetString(file, 0, 4) != "WDC5") throw new InvalidDataException("Not a WDC5 table.");
        var p = 136;
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
        recordSize = I(p + 8); TableHash = (uint)I(p + 16); LayoutHash = (uint)I(p + 20);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p + 36)); idIndex = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p + 38));
        int totalFields = I(p + 40), storageSize = I(p + 52), commonSize = I(p + 56), palletSize = I(p + 60), sections = I(p + 64);
        if ((flags & 1) != 0) throw new NotSupportedException("Sparse WDC5 tables are not supported.");
        p += 68;
        var sectionHeaders = new List<(ulong tact, int offset, int records, int strings, int idList, int copies)>();
        for (var s = 0; s < sections; s++, p += 40)
            sectionHeaders.Add((BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(p)), I(p + 8), I(p + 12), I(p + 16), I(p + 24), I(p + 36)));
        p += 4 * totalFields;
        storage = new (int, int, int, int, int, int, int)[storageSize / 24];
        for (var f = 0; f < storage.Length; f++, p += 24)
            storage[f] = (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p)), BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p + 2)), I(p + 4), I(p + 8), I(p + 12), I(p + 16), I(p + 20));
        palletStart = p; p += palletSize;
        palletOffsets = new int[storage.Length]; common = new Dictionary<int, int>[storage.Length];
        int pallet = 0, commonOffset = p;
        for (var f = 0; f < storage.Length; f++)
        {
            palletOffsets[f] = pallet;
            if (storage[f].kind is 3 or 4) pallet += storage[f].additional;
            common[f] = new Dictionary<int, int>();
            if (storage[f].kind == 2)
            {
                for (var i = 0; i < storage[f].additional / 8; i++) common[f][I(commonOffset + 8 * i)] = I(commonOffset + 8 * i + 4);
                commonOffset += storage[f].additional;
            }
        }
        foreach (var section in sectionHeaders)
        {
            var records = section.offset; var ids = records + section.records * recordSize + section.strings;
            var copies = ids + section.idList;
            if (section.tact != 0 && file.AsSpan(records, Math.Min(section.records * recordSize, 64)).IndexOfAnyExcept((byte)0) < 0) continue;
            for (var i = 0; i < section.records; i++)
            {
                var offset = records + i * recordSize;
                var id = section.idList > 0 ? I(ids + 4 * i) : Field(offset, idIndex, 0);
                rows.TryAdd(id, offset);
            }
            for (var i = 0; i < section.copies; i++)
                if (rows.TryGetValue(I(copies + 8 * i + 4), out var source)) rows.TryAdd(I(copies + 8 * i), source);
        }
    }

    public IEnumerable<int> Ids => rows.Keys;
    public bool Has(int id) => rows.ContainsKey(id);
    public int Get(int id, int field, int element = 0) => rows.TryGetValue(id, out var offset) ? Field(offset, field, element, id) : 0;

    private int Field(int offset, int field, int element, int id = 0)
    {
        var (offsetBits, sizeBits, _, kind, a, b, c) = storage[field];
        switch (kind)
        {
            case 0: return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + offsetBits / 8 + element * 4)) & (sizeBits >= 32 ? -1 : (1 << sizeBits) - 1);
            case 1: return (int)Bits(offset, offsetBits, b);
            case 5: var v = (long)Bits(offset, offsetBits, b); return (int)(v >> (b - 1) != 0 ? v - (1L << b) : v);
            case 2: return common[field].TryGetValue(id, out var value) ? value : a;
            case 3 or 4:
                var index = (int)Bits(offset, offsetBits, b); var cardinality = kind == 4 ? c : 1;
                return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(palletStart + palletOffsets[field] + (index * cardinality + element) * 4));
            default: throw new NotSupportedException($"WDC5 storage type {kind}.");
        }
    }

    private ulong Bits(int offset, int bitOffset, int size)
    {
        var start = offset + bitOffset / 8; ulong value = 0;
        for (var i = 0; i < 8 && start + i < data.Length; i++) value |= (ulong)data[start + i] << (8 * i);
        return (value >> (bitOffset % 8)) & (size >= 64 ? ulong.MaxValue : (1UL << size) - 1);
    }
}
