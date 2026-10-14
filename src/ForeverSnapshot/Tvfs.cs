// TVFS manifests (the build config's vfs-root and vfs-N). The client finds files through them, not through the build config's
// root line: vfs-root holds ".root" (the TSFM root) and one mounted sub-manifest per locale + content flags ("0000000200000000" =
// enUS), whose entries are named "<FileDataID 8 hex><CKey 32 hex>". Layout per CascLib CascRootFile_TVFS.cpp, checked on this
// build: big-endian header (version 1, header 46, 9-byte keys, flags 7 = content keys + encoding specifier table + patch records),
// a path prefix tree (optional 0x00 separator, length-prefixed fragment, optional 0x00, 0xFF + node value: a folder with the high
// bit and the byte size of its children including the value, else a VFS table offset), VFS entries (span count, spans of content
// offset, length, CFT offset sized by the CFT table), CFT entries (EKey, encoded size, ESpec offset sized by the ESpec table,
// content size, CKey, patch record count).
using System.Buffers.Binary;
using System.Text;

namespace ForeverSnapshot;

internal sealed class Tvfs
{
    public sealed record Span(uint Offset, uint Length, int Cft);
    public sealed record Cft(byte[] EKey, uint EncodedSize, int Spec, uint ContentSize, byte[] CKey, int Patches);
    public sealed record Entry(string Path, int VfsOffset, int SpanCount, List<Span> Spans);
    public sealed record NewFile(string Name, byte[] EKey, uint EncodedSize, int Spec, uint ContentSize, byte[] CKey);

    public byte[] Raw { get; private init; }
    public byte HeaderSize, EKeySize;
    public uint Flags, PathOffset, PathSize, VfsOffset, VfsSize, CftOffset, CftSize, EstOffset, EstSize;
    public ushort MaxDepth;
    public int CftOffsSize, EstOffsSize;
    public readonly List<Entry> Entries = new();

    public static Tvfs Parse(byte[] raw)
    {
        if (raw.Length < 0x2E || Encoding.ASCII.GetString(raw, 0, 4) != "TVFS" || raw[4] != 1) throw new InvalidDataException("Not a TVFS manifest.");
        var t = new Tvfs { Raw = raw, HeaderSize = raw[5], EKeySize = raw[6] };
        uint U(int o) => BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(o));
        t.Flags = U(8); t.PathOffset = U(12); t.PathSize = U(16); t.VfsOffset = U(20); t.VfsSize = U(24); t.CftOffset = U(28); t.CftSize = U(32);
        t.MaxDepth = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(36));
        if (t.Flags != 7 || t.HeaderSize != 0x2E || t.EKeySize != 9) throw new InvalidDataException("Unsupported TVFS layout (expected flags 7, header 46, 9-byte keys).");
        t.EstOffset = U(38); t.EstSize = U(42);
        t.CftOffsSize = OffsetSize(t.CftSize); t.EstOffsSize = OffsetSize(t.EstSize);
        t.Walk((int)t.PathOffset, (int)(t.PathOffset + t.PathSize), "");
        return t;
    }

    public static int OffsetSize(uint tableSize) => tableSize > 0xFFFFFF ? 4 : tableSize > 0xFFFF ? 3 : tableSize > 0xFF ? 2 : 1;

    // CapturePathEntry / ParsePathFileTable: each fragment extends the current path; a node value ends the chain (a file, or a
    // folder whose children follow) and the path goes back to the folder's base.
    private void Walk(int pos, int end, string prefix)
    {
        var path = prefix;
        while (pos < end)
        {
            if (Raw[pos] == 0) { path += "/"; pos++; }
            if (pos < end && Raw[pos] != 0xFF) { int length = Raw[pos++]; path += Encoding.ASCII.GetString(Raw, pos, length); pos += length; }
            if (pos < end && Raw[pos] == 0) { path += "/"; pos++; }
            if (pos < end && Raw[pos] == 0xFF)
            {
                var value = BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(pos + 1)); pos += 5;
                if ((value & 0x80000000) != 0)
                {
                    var size = (int)(value & 0x7FFFFFFF);
                    Walk(pos, pos + size - 4, path);
                    pos += size - 4;
                }
                else Entries.Add(File(path, (int)value));
                path = prefix;
            }
            else if (pos < end) path += "/";
        }
    }

    private Entry File(string path, int vfsOffset)
    {
        var p = (int)VfsOffset + vfsOffset;
        int count = Raw[p++]; var spans = new List<Span>();
        if (count is >= 1 and <= 224)
            for (var i = 0; i < count; i++, p += 8 + CftOffsSize)
            {
                var cft = 0; for (var b = 0; b < CftOffsSize; b++) cft = cft << 8 | Raw[p + 8 + b];
                spans.Add(new Span(BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(p)), BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(p + 4)), cft));
            }
        return new Entry(path, vfsOffset, count, spans);
    }

    public Cft CftAt(int offset)
    {
        var p = (int)CftOffset + offset;
        var ekey = Raw.AsSpan(p, EKeySize).ToArray(); p += EKeySize;
        var size = BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(p)); p += 4;
        var spec = 0; for (var b = 0; b < EstOffsSize; b++) spec = spec << 8 | Raw[p + b]; p += EstOffsSize;
        var contentSize = BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(p)); p += 4;
        var ckey = Raw.AsSpan(p, 16).ToArray(); p += 16;
        return new Cft(ekey, size, spec, contentSize, ckey, Raw[p]);
    }

    // A copy with one single-span file re-pointed in place (same table sizes): its VFS span length and its CFT entry's EKey,
    // encoded size, content size and CKey. The ESpec offset and the (empty) patch records stay.
    public byte[] WithFile(string path, byte[] ekey, uint encodedSize, uint contentSize, byte[] ckey)
    {
        var entry = Entries.Single(e => e.Path == path);
        if (entry.SpanCount != 1 || CftAt(entry.Spans[0].Cft).Patches != 0) throw new InvalidDataException($"TVFS entry {path} is not a plain single-span file.");
        var raw = Raw.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan((int)VfsOffset + entry.VfsOffset + 1 + 4), contentSize);
        var p = (int)CftOffset + entry.Spans[0].Cft;
        ekey.AsSpan(0, EKeySize).CopyTo(raw.AsSpan(p));
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(p + EKeySize), encodedSize);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(p + EKeySize + 4 + EstOffsSize), contentSize);
        ckey.CopyTo(raw.AsSpan(p + EKeySize + 4 + EstOffsSize + 4));
        return raw;
    }

    // A flat manifest like the client's small ones: header, path table (a root folder node, then each file name with its VFS offset,
    // in name order), the ESpec table copied from this manifest, CFT entries, VFS entries (one span each). Keeps this manifest's
    // single-span files and adds `files`.
    public byte[] WithFiles(IEnumerable<NewFile> files)
    {
        var all = new List<NewFile>();
        foreach (var e in Entries)
        {
            var c = e.SpanCount == 1 ? CftAt(e.Spans[0].Cft) : throw new InvalidDataException($"TVFS entry {e.Path} has {e.SpanCount} spans.");
            if (c.Patches != 0 || e.Spans[0].Offset != 0 || e.Spans[0].Length != c.ContentSize || e.Path.Contains('/')) throw new InvalidDataException($"TVFS entry {e.Path} is not a plain file.");
            all.Add(new NewFile(e.Path, c.EKey, c.EncodedSize, c.Spec, c.ContentSize, c.CKey));
        }
        all.AddRange(files);
        all = all.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        var est = Raw.AsSpan((int)EstOffset, (int)EstSize).ToArray();
        int estOffs = OffsetSize((uint)est.Length), cftEntry = EKeySize + 4 + estOffs + 4 + 16 + 1, cftSize = cftEntry * all.Count, cftOffs = OffsetSize((uint)cftSize);
        var path = new MemoryStream(); var children = new MemoryStream();
        for (var i = 0; i < all.Count; i++)
        {
            var name = Encoding.ASCII.GetBytes(all[i].Name);
            if (name.Length is 0 or > 255) throw new InvalidDataException("TVFS name length out of range.");
            children.WriteByte((byte)name.Length); children.Write(name); children.WriteByte(0xFF); WriteU32(children, (uint)(i * (1 + 8 + cftOffs)));
        }
        path.WriteByte(0xFF); WriteU32(path, 0x80000000 | (uint)(4 + children.Length)); children.WriteTo(path);
        var cft = new MemoryStream(); var vfs = new MemoryStream();
        for (var i = 0; i < all.Count; i++)
        {
            var f = all[i];
            cft.Write(f.EKey.AsSpan(0, EKeySize)); WriteU32(cft, f.EncodedSize); WriteN(cft, (uint)f.Spec, estOffs); WriteU32(cft, f.ContentSize); cft.Write(f.CKey); cft.WriteByte(0);
            vfs.WriteByte(1); WriteU32(vfs, 0); WriteU32(vfs, f.ContentSize); WriteN(vfs, (uint)(i * cftEntry), cftOffs);
        }
        var output = new MemoryStream();
        var header = Raw.AsSpan(0, HeaderSize).ToArray();
        uint pathAt = HeaderSize, estAt = pathAt + (uint)path.Length, cftAt = estAt + (uint)est.Length, vfsAt = cftAt + (uint)cft.Length;
        void Set(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at), value);
        Set(12, pathAt); Set(16, (uint)path.Length); Set(20, vfsAt); Set(24, (uint)vfs.Length); Set(28, cftAt); Set(32, (uint)cft.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(36), 1); Set(38, estAt); Set(42, (uint)est.Length);
        output.Write(header); path.WriteTo(output); output.Write(est); cft.WriteTo(output); vfs.WriteTo(output);
        return output.ToArray();
    }

    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    private static void WriteN(Stream s, uint v, int bytes) { for (var i = bytes - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i))); }
}
