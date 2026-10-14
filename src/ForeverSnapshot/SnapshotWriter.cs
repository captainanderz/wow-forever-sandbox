// Writing side: new data.NNN archives (16 cross-reference headers, then the copied entries with checksum B for their new place)
// and one version-1 index per bucket. Layout matches the agent's own storages (observed on the live install): cross-reference
// entry b sits at offset 30*b, is header-only with flags 1, has key [x, archive, 7-byte tail] whose bucket is b-1 and is
// listed in index bucket b; real data starts at 480; indices are sorted by key and padded with zeros.
using System.Buffers.Binary;
using System.Security.Cryptography;
using static ForeverSnapshot.CascFormat;

namespace ForeverSnapshot;

internal sealed class SnapshotWriter
{
    // Offsets have 30 bits; stay a little under 1 GiB per archive like the agent does.
    public const int ArchiveLimit = 0x3FFF0000;
    public readonly record struct Placed(Key9 Key, SourceStorage.Location Source, int Archive, int Offset);
    public readonly record struct IndexEntry(Key9 Key, int Archive, int Offset, int Size);

    private readonly string dataDirectory;
    private readonly byte[] crossTail;   // 7 random bytes per snapshot, like the agent's per-storage tail

    public SnapshotWriter(string snapshotDataDirectory, byte[] tail)
    {
        dataDirectory = Path.Combine(snapshotDataDirectory, "data");
        crossTail = tail;
        Directory.CreateDirectory(dataDirectory);
    }

    // Deterministic layout: entries in source order (sequential reads), packed into archives from offset 480.
    public static List<Placed> Layout(IEnumerable<(Key9 key, SourceStorage.Location source)> entries)
    {
        var placed = new List<Placed>();
        int archive = 0, offset = CrossReferenceArea;
        foreach (var (key, source) in entries.OrderBy(e => e.source.Archive).ThenBy(e => e.source.Offset))
        {
            if (offset + (long)source.Size > ArchiveLimit && offset > CrossReferenceArea) { archive++; offset = CrossReferenceArea; }
            placed.Add(new Placed(key, source, archive, offset));
            offset += source.Size;
        }
        return placed;
    }

    public string ArchivePath(int archive) => Path.Combine(dataDirectory, $"data.{archive:D3}");

    public Key9 CrossKey(int archive, int bucket)
    {
        Span<byte> key = stackalloc byte[9];
        key[1] = (byte)archive; crossTail.CopyTo(key[2..]);
        for (var first = 0; first < 256; first++) { key[0] = (byte)first; if (Bucket(key) == (bucket + 15) % 16) return Key9.From(key); }
        throw new InvalidOperationException("No cross-reference key.");
    }

    public IEnumerable<IndexEntry> CrossEntries(int archive) => Enumerable.Range(0, 16).Select(b => new IndexEntry(CrossKey(archive, b), archive, b * HeaderSize, HeaderSize));

    // Writes one archive from its placed entries; every source entry is checked (key, size, checksums at its old place, BLTE key).
    // A zero-filled entry is space the agent reserved without downloading the file (the live install has the same hole and would
    // fetch it on demand); it is copied as it is and reported in holes. Any other data/key mismatch stops the snapshot.
    public long WriteArchive(int archive, IReadOnlyList<Placed> entries, SourceStorage source, Action<long> progress, List<Key9> holes)
    {
        var temporary = ArchivePath(archive) + ".tmp";
        long written = 0;
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            Span<byte> k = stackalloc byte[9];
            for (var b = 0; b < 16; b++)
            {
                header.Clear();
                CrossKey(archive, b).Write(k);
                for (var i = 0; i < 9; i++) header[15 - i] = k[i];
                BinaryPrimitives.WriteInt32LittleEndian(header[0x10..], HeaderSize);
                header[0x14] = 1;
                WriteChecksums(header, archive, b * HeaderSize);
                output.Write(header);
            }
            written = CrossReferenceArea;
            var buffer = new byte[1 << 20];
            foreach (var entry in entries)
            {
                if (entry.Archive != archive || entry.Offset != written) throw new InvalidOperationException("Layout and archive position disagree.");
                if (buffer.Length < entry.Source.Size) buffer = new byte[Math.Max(entry.Source.Size, buffer.Length * 2)];
                var raw = buffer.AsSpan(0, entry.Source.Size);
                source.ReadRaw(entry.Source, raw);
                if (!HeaderKeyMatches(raw, entry.Key) || BinaryPrimitives.ReadInt32LittleEndian(raw[0x10..]) != entry.Source.Size)
                    throw new InvalidDataException($"Entry {entry.Key} at data.{entry.Source.Archive:D3}+{entry.Source.Offset}: header does not match the index.");
                if (!ChecksumsValid(raw, entry.Source.Archive, entry.Source.Offset))
                    throw new InvalidDataException($"Entry {entry.Key} at data.{entry.Source.Archive:D3}+{entry.Source.Offset}: header checksum mismatch in the source.");
                if (!BlteMatchesKey(raw[HeaderSize..], entry.Key))
                {
                    if (raw[HeaderSize..].IndexOfAnyExcept((byte)0) >= 0)
                        throw new InvalidDataException($"Entry {entry.Key} at data.{entry.Source.Archive:D3}+{entry.Source.Offset}: data does not match its encoding key.");
                    holes.Add(entry.Key);
                }
                WriteChecksums(raw, archive, entry.Offset);   // bytes 0-0x15 unchanged, so A stays; B follows the new position
                output.Write(raw);
                written += raw.Length;
                progress(raw.Length);
            }
            output.Flush(true);
        }
        File.Move(temporary, ArchivePath(archive), true);
        return written;
    }

    // A new archive of sandbox-made entries (StorageAppend): the 16 cross-reference headers, then each BLTE stream behind a header
    // with its full encoding key byte-reversed, its size and flags 0 (as the agent's data entries). Returns the archive's index entries.
    public List<IndexEntry> WriteNewArchive(int archive, IReadOnlyList<(byte[] ekey, byte[] blte)> entries)
    {
        var result = CrossEntries(archive).ToList();
        var path = ArchivePath(archive);
        using (var output = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var header = new byte[HeaderSize]; var k = new byte[9];
            for (var b = 0; b < 16; b++)
            {
                Array.Clear(header);
                CrossKey(archive, b).Write(k);
                for (var i = 0; i < 9; i++) header[15 - i] = k[i];
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x10), HeaderSize);
                header[0x14] = 1;
                WriteChecksums(header, archive, b * HeaderSize);
                output.Write(header);
            }
            var offset = CrossReferenceArea;
            foreach (var (ekey, blte) in entries)
            {
                Array.Clear(header);
                for (var i = 0; i < 16; i++) header[15 - i] = ekey[i];
                var size = HeaderSize + blte.Length;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x10), size);
                WriteChecksums(header, archive, offset);
                output.Write(header); output.Write(blte);
                result.Add(new IndexEntry(Key9.From(ekey), archive, offset, size));
                offset += size;
            }
            output.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
        return result;
    }

    // Index v7 per bucket: header hash over the 16 header bytes, entries hash chained per 18-byte entry, padded with zeros.
    public void WriteIndices(IEnumerable<IndexEntry> all, uint version = 1)
    {
        var buckets = all.GroupBy(BucketOf).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Key.High).ThenBy(e => e.Key.Low).ToList());
        for (var bucket = 0; bucket < 16; bucket++)
        {
            var entries = buckets.TryGetValue(bucket, out var list) ? list : new List<IndexEntry>();
            var entriesSize = entries.Count * 18;
            var length = (0x28 + entriesSize + 0x10000 + 0xFFFF) & ~0xFFFF;
            var raw = new byte[length];
            BinaryPrimitives.WriteInt32LittleEndian(raw, 0x10);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(8), 7);
            raw[10] = (byte)bucket; raw[11] = 0; raw[12] = 4; raw[13] = 5; raw[14] = 9; raw[15] = 30;
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(16), 0x4000000000);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), HashLittle2(raw.AsSpan(8, 0x10)).c);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x20), entriesSize);
            uint pc = 0, pb = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var o = 0x28 + i * 18; var e = entries[i];
                e.Key.Write(raw.AsSpan(o));
                var packed = (ulong)e.Archive << 30 | (uint)e.Offset;
                for (var b = 0; b < 5; b++) raw[o + 9 + b] = (byte)(packed >> (8 * (4 - b)));
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(o + 14), e.Size);
                (pc, pb) = HashLittle2(raw.AsSpan(o, 18), pc, pb);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x24), pc);
            var path = Path.Combine(dataDirectory, $"{bucket:x2}{version:x8}.idx");
            File.WriteAllBytes(path + ".tmp", raw);
            File.Move(path + ".tmp", path, true);
        }
    }

    // Cross-reference entries are listed in their own bucket b (their key's bucket is b-1); everything else by key.
    private static int BucketOf(IndexEntry e) => e.Offset < CrossReferenceArea && e.Size == HeaderSize ? e.Offset / HeaderSize : e.Key.Bucket;

    public static byte[] NewTail() => RandomNumberGenerator.GetBytes(7);
}
