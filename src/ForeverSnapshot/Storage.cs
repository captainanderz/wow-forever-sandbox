// Reading side: .build.info, build / CDN configs, local indices (every entry), raw archive entries, encoding, and the download
// and install manifests with Battle.net-style tag selection.
using System.Buffers.Binary;
using System.Text;
using AuthServer.Network;
using static ForeverSnapshot.CascFormat;

namespace ForeverSnapshot;

internal sealed record BuildInfo(string[] Header, string[] Row, string Directory)
{
    public string this[string column] => Row[Array.FindIndex(Header, h => h.Split('!')[0] == column)];
    public string[] SelectedTags => this["Tags"].Split(':').SelectMany(group => group.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(t => t.TrimEnd('?')).Distinct().ToArray();

    public static BuildInfo Read(string installDirectory, string product, string version)
    {
        var lines = File.ReadAllLines(Path.Combine(installDirectory, ".build.info"));
        var header = lines[0].Split('|');
        int Column(string name) => Array.FindIndex(header, h => h.Split('!')[0] == name);
        var row = lines.Skip(1).Select(l => l.Split('|')).FirstOrDefault(r => r.Length == header.Length && r[Column("Product")] == product)
            ?? throw new InvalidDataException($"Product {product} is not in .build.info.");
        if (row[Column("Version")] != version) throw new InvalidDataException($"The install has {product} {row[Column("Version")]}, not {version}. The {version} files are no longer there to snapshot.");
        return new BuildInfo(header, row, installDirectory);
    }
}

internal sealed class SourceStorage : IDisposable
{
    public readonly record struct Location(int Archive, int Offset, int Size);

    public string DataDirectory { get; }
    public Dictionary<Key9, Location> Index { get; } = new();
    public int IndexFiles { get; private set; }
    private readonly Dictionary<int, Microsoft.Win32.SafeHandles.SafeFileHandle> archives = new();

    public SourceStorage(string installDirectory)
    {
        DataDirectory = Path.Combine(installDirectory, "Data");
        var latest = new Dictionary<int, (uint version, string path)>();
        foreach (var path in Directory.GetFiles(Path.Combine(DataDirectory, "data"), "*.idx"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 10) continue;
            var bucket = Convert.ToInt32(name[..2], 16); var version = Convert.ToUInt32(name[2..], 16);
            if (!latest.TryGetValue(bucket, out var known) || version > known.version) latest[bucket] = (version, path);
        }
        foreach (var (_, (_, path)) in latest.OrderBy(p => p.Key))
        {
            var raw = File.ReadAllBytes(path);
            var (entriesOffset, entriesSize, entrySize) = CheckIndex(raw, path);
            for (var o = entriesOffset + 8; o + entrySize <= entriesOffset + 8 + entriesSize; o += entrySize)
            {
                ulong packed = 0; for (var b = 0; b < 5; b++) packed = packed << 8 | raw[o + 9 + b];
                Index.TryAdd(Key9.From(raw.AsSpan(o)), new Location((int)(packed >> 30), (int)(packed & 0x3FFFFFFF), BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(o + 14))));
            }
            IndexFiles++;
        }
    }

    // Index v7 with the usual 9/5/4 spec and 30 offset bits; both hashes must match (the writer uses the same algorithms).
    public static (int entriesOffset, int entriesSize, int entrySize) CheckIndex(byte[] raw, string name)
    {
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(raw);
        if (headerSize != 0x10 || HashLittle2(raw.AsSpan(8, headerSize)).c != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)))
            throw new InvalidDataException($"{Path.GetFileName(name)}: index header hash mismatch.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8)) != 7 || raw[12] != 4 || raw[13] != 5 || raw[14] != 9 || raw[15] != 30)
            throw new InvalidDataException($"{Path.GetFileName(name)}: unsupported index layout.");
        var entriesOffset = (8 + headerSize + 15) & ~15;
        var entriesSize = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(entriesOffset));
        uint pc = 0, pb = 0;
        for (var o = entriesOffset + 8; o < entriesOffset + 8 + entriesSize; o += 18) (pc, pb) = HashLittle2(raw.AsSpan(o, 18), pc, pb);
        if (pc != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(entriesOffset + 4))) throw new InvalidDataException($"{Path.GetFileName(name)}: index entries hash mismatch.");
        return (entriesOffset, entriesSize, 18);
    }

    public string ConfigPath(string kind, string key) => Path.Combine(DataDirectory, kind, key[..2], key[2..4], key);

    public static Dictionary<string, string[]> ReadConfig(string path) =>
        File.ReadAllLines(path).Where(l => l.Contains(" = ")).Select(l => l.Split(" = ", 2)).ToDictionary(p => p[0], p => p[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public bool Has(ReadOnlySpan<byte> ekey) => Index.ContainsKey(Key9.From(ekey));

    // Raw entry (header + BLTE): exact unbuffered reads (no read-ahead waste on small headers), shared so Battle.net is not blocked.
    public void ReadRaw(Location location, Span<byte> target)
    {
        if (!archives.TryGetValue(location.Archive, out var handle))
            archives[location.Archive] = handle = File.OpenHandle(Path.Combine(DataDirectory, "data", $"data.{location.Archive:D3}"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var span = target[..location.Size];
        for (var done = 0; done < span.Length; )
        {
            var read = RandomAccess.Read(handle, span[done..], location.Offset + done);
            if (read <= 0) throw new EndOfStreamException($"data.{location.Archive:D3} ends before offset {location.Offset + location.Size}.");
            done += read;
        }
    }

    public byte[] ReadFile(ReadOnlySpan<byte> ekey)
    {
        var location = Index.TryGetValue(Key9.From(ekey), out var l) ? l : throw new FileNotFoundException("Encoding key " + Convert.ToHexString(ekey) + " is not stored locally.");
        var raw = new byte[location.Size];
        ReadRaw(location, raw);
        return ForeverCasc.Blte(raw[HeaderSize..], null);
    }

    public void Dispose() { foreach (var handle in archives.Values) handle.Dispose(); archives.Clear(); }
}

// Encoding file: CKey -> EKeys (pages sorted by first CKey).
internal sealed class EncodingTable
{
    private readonly byte[] data;
    private readonly int pages, pageSize, ekeySize, pagesStart, indexStart;

    public EncodingTable(byte[] encoding)
    {
        data = encoding;
        if (encoding[0] != (byte)'E' || encoding[1] != (byte)'N') throw new InvalidDataException("Not an encoding file.");
        ekeySize = encoding[4];
        pageSize = BinaryPrimitives.ReadUInt16BigEndian(encoding.AsSpan(5)) * 1024;
        pages = BinaryPrimitives.ReadInt32BigEndian(encoding.AsSpan(9));
        indexStart = 22 + BinaryPrimitives.ReadInt32BigEndian(encoding.AsSpan(18));
        pagesStart = indexStart + pages * 32;
    }

    public List<byte[]> EKeys(ReadOnlySpan<byte> ckey)
    {
        int lo = 0, hi = pages - 1, page = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (data.AsSpan(indexStart + mid * 32, 16).SequenceCompareTo(ckey) <= 0) { page = mid; lo = mid + 1; } else hi = mid - 1;
        }
        var result = new List<byte[]>();
        if (page < 0) return result;
        for (int p = pagesStart + page * pageSize, end = p + pageSize; p + 22 <= end && data[p] != 0; p += 22 + data[p] * ekeySize)
            if (data.AsSpan(p + 6, 16).SequenceEqual(ckey))
            {
                for (var k = 0; k < data[p]; k++) result.Add(data.AsSpan(p + 22 + k * ekeySize, ekeySize).ToArray());
                break;
            }
        return result;
    }
}

// Download ("DL") and install ("IN") manifests share the tag model: an entry is selected when, for every tag type that has a
// selected tag, it carries one of them, and it carries none of the tags of a type with no selection (HighRes, Alternate, ...).
internal static class Manifests
{
    public sealed record Tag(string Name, int Type, byte[] Mask);
    public sealed record DownloadEntry(byte[] EKey, long Size, int Index);
    public sealed record InstallEntry(string Name, byte[] CKey, int Size, int Index);

    public static (List<DownloadEntry> entries, List<Tag> tags) ReadDownload(byte[] dl)
    {
        if (dl[0] != (byte)'D' || dl[1] != (byte)'L') throw new InvalidDataException("Not a download manifest.");
        int version = dl[2], ekeySize = dl[3], hasChecksum = dl[4];
        var count = BinaryPrimitives.ReadInt32BigEndian(dl.AsSpan(5)); var tagCount = BinaryPrimitives.ReadUInt16BigEndian(dl.AsSpan(9));
        int pos = 11, flagSize = 0;
        if (version >= 2) flagSize = dl[pos++];
        if (version >= 3) pos += 4;
        var entries = new List<DownloadEntry>(count);
        for (var i = 0; i < count; i++)
        {
            long size = 0; for (var b = 0; b < 5; b++) size = size << 8 | dl[pos + ekeySize + b];
            entries.Add(new DownloadEntry(dl.AsSpan(pos, ekeySize).ToArray(), size, i));
            pos += ekeySize + 6 + (hasChecksum != 0 ? 4 : 0) + flagSize;
        }
        return (entries, ReadTags(dl, ref pos, tagCount, count));
    }

    public static (List<InstallEntry> entries, List<Tag> tags) ReadInstall(byte[] install)
    {
        if (install[0] != (byte)'I' || install[1] != (byte)'N') throw new InvalidDataException("Not an install manifest.");
        int hashSize = install[3];
        var tagCount = BinaryPrimitives.ReadUInt16BigEndian(install.AsSpan(4)); var count = BinaryPrimitives.ReadInt32BigEndian(install.AsSpan(6));
        var pos = 10;
        var tags = ReadTags(install, ref pos, tagCount, count);
        var entries = new List<InstallEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var end = Array.IndexOf(install, (byte)0, pos); var name = Encoding.UTF8.GetString(install, pos, end - pos); pos = end + 1;
            entries.Add(new InstallEntry(name, install.AsSpan(pos, hashSize).ToArray(), BinaryPrimitives.ReadInt32BigEndian(install.AsSpan(pos + hashSize)), i));
            pos += hashSize + 4;
        }
        return (entries, tags);
    }

    private static List<Tag> ReadTags(byte[] data, ref int pos, int tagCount, int entryCount)
    {
        var tags = new List<Tag>(tagCount);
        for (var t = 0; t < tagCount; t++)
        {
            var end = Array.IndexOf(data, (byte)0, pos); var name = Encoding.UTF8.GetString(data, pos, end - pos); pos = end + 1;
            var type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2;
            var bytes = (entryCount + 7) / 8;
            tags.Add(new Tag(name, type, data.AsSpan(pos, bytes).ToArray())); pos += bytes;
        }
        return tags;
    }

    public static Func<int, bool> Selector(List<Tag> tags, IReadOnlyCollection<string> selected)
    {
        var byType = tags.GroupBy(t => t.Type).Select(g => (all: g.ToArray(), chosen: g.Where(t => selected.Contains(t.Name)).ToArray())).ToArray();
        static bool Has(Tag tag, int i) => (tag.Mask[i >> 3] >> (7 - (i & 7)) & 1) != 0;
        return i => byType.All(type => type.chosen.Length > 0 ? type.chosen.Any(t => Has(t, i)) : !type.all.Any(t => Has(t, i)));
    }
}
