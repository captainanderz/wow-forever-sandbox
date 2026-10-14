// Adding sandbox-made files (the .walls models) to a snapshot without touching what is there. The client finds files through the
// TVFS manifests (Tvfs.cs), so one new data archive holds: the files, the TSFM root with one more block (vfs-root's ".root"), the
// enUS mount's manifest with the files added, and vfs-root re-pointed to both. The next index version lists every entry plus the
// new archive's; a new build config differs only in its vfs-root / vfs-1 and mount lines (the root and encoding lines stay: the
// client does not use them for lookups, and evicted a rebuilt 149 MB encoding table it did not use). .build.info points at the new
// config; the previous one is kept as .build.info.base and Data\sandbox-files.json records what was written, so Remove restores it.
// The game rebuilds shmem from the indices, so shmem is deleted after either change.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthServer.Network;
using static ForeverSnapshot.CascFormat;

namespace ForeverSnapshot;

internal static class StorageAppend
{
    public sealed record NewFile(int FileDataId, byte[] Content);
    private sealed record State(string BaseBuildKey, string BuildKey, int Archive, uint IndexVersion, int[] FileDataIds);
    private const string StateFile = "sandbox-files.json", BaseBuildInfo = ".build.info.base";
    private const uint NoNameHash = 0x10000000;

    public static bool Installed(string client) => File.Exists(Path.Combine(client, "Data", StateFile));

    // Read-only survey of the TVFS manifests: the root's layout and where a file's entry lives.
    public static string Survey(string install, string product, string version, int fileDataId)
    {
        var info = BuildInfo.Read(install, product, version);
        using var storage = new SourceStorage(install);
        var config = SourceStorage.ReadConfig(storage.ConfigPath("config", info["Build Key"]));
        var vfs = config.Where(p => p.Key.StartsWith("vfs-") && !p.Key.EndsWith("-size")).ToDictionary(p => p.Key, p => p.Value);
        var subKeys = vfs.Values.Select(v => Convert.ToHexString(Convert.FromHexString(v[1]).AsSpan(0, 9))).ToHashSet();
        var report = new StringBuilder();
        var root = Tvfs.Parse(storage.ReadFile(Convert.FromHexString(vfs["vfs-root"][1])));
        report.AppendLine($"vfs-root: flags {root.Flags}, header {root.HeaderSize}, ekey {root.EKeySize}, depth {root.MaxDepth}, path {root.PathSize}, vfs {root.VfsSize}, cft {root.CftSize} ({root.CftOffsSize}), est {root.EstSize}; {root.Entries.Count} entries");
        foreach (var e in root.Entries.Take(12))
        {
            var c = root.CftAt(e.Spans[0].Cft);
            report.AppendLine($"  '{e.Path}' spans {e.SpanCount} len {e.Spans[0].Length} ekey {Convert.ToHexString(c.EKey)} sub-manifest {subKeys.Contains(Convert.ToHexString(c.EKey))} spec {c.Spec}");
        }
        report.AppendLine($"  sub-manifest entries in root: {root.Entries.Count(e => subKeys.Contains(Convert.ToHexString(root.CftAt(e.Spans[0].Cft).EKey)))}");
        var needle = $"{fileDataId:X8}";
        var found = 0;
        var mounts = root.Entries.ToDictionary(e => Convert.ToHexString(root.CftAt(e.Spans[0].Cft).EKey), e => e.Path);
        var rootEntry = root.Entries.First(e => e.Path == ".root"); var rootCft = root.CftAt(rootEntry.Spans[0].Cft);
        report.AppendLine($".root cft: ekey {Convert.ToHexString(rootCft.EKey)} size {rootCft.EncodedSize} spec {rootCft.Spec} ckey {Convert.ToHexString(rootCft.CKey)} (build config root {config["root"][0]})");
        foreach (var (name, keys) in vfs.Where(p => p.Key != "vfs-root").OrderBy(p => int.Parse(p.Key[4..])))
        {
            Tvfs t;
            try { t = Tvfs.Parse(storage.ReadFile(Convert.FromHexString(keys[1]))); } catch (Exception error) when (error is FileNotFoundException or InvalidDataException) { continue; }
            var mount = mounts.GetValueOrDefault(Convert.ToHexString(Convert.FromHexString(keys[1]).AsSpan(0, 9)), "?");
            foreach (var e in t.Entries.Where(e => e.Path.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            {
                if (found++ < 6)
                {
                    var c = t.CftAt(e.Spans[0].Cft);
                    report.AppendLine($"{name} mounted as '{mount}': flags {t.Flags}, depth {t.MaxDepth}, {t.Entries.Count} entries, path {t.PathSize}, vfs {t.VfsSize}, cft {t.CftSize}, est {t.EstSize}: '{e.Path}' spans {e.SpanCount} len {e.Spans[0].Length} ekey {Convert.ToHexString(c.EKey)} size {c.EncodedSize} spec {c.Spec} ckey {(c.CKey == null ? "-" : Convert.ToHexString(c.CKey))}");
                    report.AppendLine($"   sample entries: {string.Join(" | ", t.Entries.Take(3).Select(x => x.Path))}");
                }
            }
        }
        report.AppendLine($"{found} entries for FileDataID {fileDataId} across {vfs.Count} manifests.");
        var small = Tvfs.Parse(storage.ReadFile(Convert.FromHexString(vfs["vfs-2"][1])));
        report.AppendLine($"vfs-2 ({small.Raw.Length} bytes, mounted '{mounts.GetValueOrDefault(Convert.ToHexString(Convert.FromHexString(vfs["vfs-2"][1]).AsSpan(0, 9)))}'): flags {small.Flags}, header {small.HeaderSize}, depth {small.MaxDepth}, path {small.PathOffset}+{small.PathSize}, vfs {small.VfsOffset}+{small.VfsSize}, cft {small.CftOffset}+{small.CftSize}, est {small.EstOffset}+{small.EstSize}");
        report.AppendLine($"  bytes {Convert.ToHexString(small.Raw)}");
        foreach (var e in small.Entries) report.AppendLine($"  '{e.Path}' spans {e.SpanCount} {string.Join(",", e.Spans.Select(s => $"[{s.Offset}+{s.Length} cft {s.Cft}]"))}");
        report.AppendLine($"  root est: {Convert.ToHexString(root.Raw.AsSpan((int)root.EstOffset, (int)root.EstSize))} '{Encoding.ASCII.GetString(root.Raw, (int)root.EstOffset, (int)root.EstSize).Replace('\0', '|')}'");
        report.Append($"  root cft entry size {root.CftSize / (double)root.Entries.Count:0.##}; .root raw cft {Convert.ToHexString(root.Raw.AsSpan((int)root.CftOffset + rootEntry.Spans[0].Cft, 36))}");
        return report.ToString();
    }

    // Read-only self-check on any install of the build: the encoding table re-serialises byte for byte, and the root parses.
    public static string Check(string install, string product, string version)
    {
        var info = BuildInfo.Read(install, product, version);
        using var storage = new SourceStorage(install);
        var config = SourceStorage.ReadConfig(storage.ConfigPath("config", info["Build Key"]));
        var raw = storage.ReadFile(Convert.FromHexString(config["encoding"][1]));
        var encoding = Encoding_.Parse(raw);
        var again = encoding.Serialize();
        var same = again.AsSpan().SequenceEqual(raw);
        if (!same)
        {
            var at = again.AsSpan().CommonPrefixLength(raw);
            int cePages = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(9)), esPages = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(13)), spec = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(18));
            int ce = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(5)) * 1024, es = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(7)) * 1024;
            int ceIndex = 22 + spec, cePagesAt = ceIndex + cePages * 32, esIndex = cePagesAt + cePages * ce, esPagesAt = esIndex + esPages * 32, trailer = esPagesAt + esPages * es;
            var lastPage = esPagesAt + (esPages - 1) * es;
            var inPage = again.AsSpan(lastPage, es).CommonPrefixLength(raw.AsSpan(lastPage, es));
            var differing = Enumerable.Range(0, es).Count(i => again[lastPage + i] != raw[lastPage + i]);
            return $"last es page differs from +{inPage} ({differing} bytes differ); old there: {Convert.ToHexString(raw.AsSpan(lastPage + Math.Max(0, inPage - 25), Math.Min(100, es - Math.Max(0, inPage - 25))))}; new: {Convert.ToHexString(again.AsSpan(lastPage + Math.Max(0, inPage - 25), Math.Min(100, es - Math.Max(0, inPage - 25))))}; "
                + $"DIFFERENT at {at:N0} (lengths {again.Length:N0} vs {raw.Length:N0}); sections: spec 22, ce index {ceIndex:N0}, ce pages {cePagesAt:N0} ({cePages} x {ce}), es index {esIndex:N0}, es pages {esPagesAt:N0} ({esPages} x {es}), trailer {trailer:N0}; "
                + $"new header {Convert.ToHexString(again.AsSpan(0, 22))} old {Convert.ToHexString(raw.AsSpan(0, 22))}; around: new {Convert.ToHexString(again.AsSpan(Math.Max(0, at - 8), 48))} old {Convert.ToHexString(raw.AsSpan(Math.Max(0, at - 8), 48))}";
        }
        var rootCKey = Convert.FromHexString(config["root"][0]); var rootEKey = encoding.EKeyOf(rootCKey);
        var rootData = storage.ReadFile(rootEKey);
        if (rootData.Length < 32) return $"encoding re-serialises {(same ? "identically" : "DIFFERENTLY")}; root ckey {Convert.ToHexString(rootCKey)} -> ekey {Convert.ToHexString(rootEKey)} read {rootData.Length} bytes";
        var root = Root.Parse(rootData);
        // TVFS writers on the build's own manifests: the enUS mount's manifest re-serialises byte for byte, and re-pointing vfs-root's
        // .root to its own values changes nothing.
        var vfsRoot = Tvfs.Parse(storage.ReadFile(Convert.FromHexString(config["vfs-root"][1])));
        var rootEntry = vfsRoot.CftAt(vfsRoot.Entries.Single(e => e.Path == ".root").Spans[0].Cft);
        var samePointer = vfsRoot.WithFile(".root", rootEntry.EKey, rootEntry.EncodedSize, rootEntry.ContentSize, rootEntry.CKey).AsSpan().SequenceEqual(vfsRoot.Raw);
        var mount = Tvfs.Parse(storage.ReadFile(vfsRoot.CftAt(vfsRoot.Entries.Single(e => e.Path == ForeverWallModels.Mount).Spans[0].Cft).EKey));
        var sameManifest = mount.WithFiles(Array.Empty<Tvfs.NewFile>()).AsSpan().SequenceEqual(mount.Raw);
        return $"encoding {raw.Length:N0} bytes, {encoding.Counts} - re-serialises {(same ? "identically" : "DIFFERENTLY")}; root {root.Count:N0} file IDs, has {ForeverWallModels.CopySource}: {root.Has(ForeverWallModels.CopySource)}\n{root.Describe()}\n"
            + $"TVFS: mount {ForeverWallModels.Mount} manifest ({mount.Raw.Length} bytes, {mount.Entries.Count} files) re-serialises {(sameManifest ? "identically" : "DIFFERENTLY")}; vfs-root re-point {(samePointer ? "is exact" : "CHANGES BYTES")}; "
            + $"client lookup {root.TvfsPath(ForeverWallModels.CopySource)} -> {ReadBack(install, product, version, new[] { ForeverWallModels.CopySource })[ForeverWallModels.CopySource].Length:N0} bytes";
    }

    // Adds `files` (with a TSFM root block like the one holding `sameBlockAs`) under the vfs-root mount `mount`. Returns the new
    // archive's number.
    public static int Append(string client, string product, string version, IReadOnlyList<NewFile> files, int[] sameBlockAs, string mount)
    {
        if (Installed(client)) Remove(client);
        var info = BuildInfo.Read(client, product, version);
        var baseKey = info["Build Key"];
        var dataDirectory = Path.Combine(client, "Data");
        using var storage = new SourceStorage(client);
        var configPath = storage.ConfigPath("config", baseKey);
        var configText = File.ReadAllText(configPath);
        var vfs = SourceStorage.ReadConfig(configPath).Where(p => p.Key.StartsWith("vfs-") && !p.Key.EndsWith("-size")).ToDictionary(p => p.Key, p => p.Value);
        var rootKeys = vfs["vfs-root"];
        var vfsRoot = Tvfs.Parse(storage.ReadFile(Convert.FromHexString(rootKeys[1])));
        var tsfmCft = vfsRoot.CftAt(vfsRoot.Entries.Single(e => e.Path == ".root").Spans[0].Cft);
        var tsfm = Root.Parse(storage.ReadFile(tsfmCft.EKey));
        foreach (var file in files)
            if (tsfm.Has(file.FileDataId)) throw new InvalidDataException($"FileDataID {file.FileDataId} already exists in this build.");
        var mountCft = vfsRoot.CftAt(vfsRoot.Entries.Single(e => e.Path == mount).Spans[0].Cft);
        var mountName = vfs.Single(p => p.Key != "vfs-root" && Convert.FromHexString(p.Value[1]).AsSpan(0, 9).SequenceEqual(mountCft.EKey)).Key;
        var manifest = Tvfs.Parse(storage.ReadFile(Convert.FromHexString(vfs[mountName][1])));
        var fileSpec = manifest.Entries.Select(e => manifest.CftAt(e.Spans[0].Cft).Spec).FirstOrDefault();

        // Blobs: the files, the TSFM root with one more block, the mount's manifest with the files, and vfs-root re-pointed to both.
        var stored = new List<(byte[] ekey, byte[] blte)>();
        (byte[] ckey, byte[] ekey, uint encoded, uint size) Store(byte[] content)
        {
            var (blte, ekey) = Blte(content); stored.Add((ekey, blte));
            return (MD5.HashData(content), ekey, (uint)blte.Length, (uint)content.Length);
        }
        var entries = files.Select(f =>
        {
            var s = Store(f.Content);
            return new Tvfs.NewFile($"{f.FileDataId:X8}{Convert.ToHexString(s.ckey)}", s.ekey, s.encoded, fileSpec, s.size, s.ckey);
        }).ToList();
        var newTsfm = tsfm.WithBlock(files.Select(f => (f.FileDataId, MD5.HashData(f.Content))).ToList(), sameBlockAs, mount);
        var t = Store(newTsfm);
        var m = Store(manifest.WithFiles(entries));
        var patched = Tvfs.Parse(vfsRoot.WithFile(".root", t.ekey, t.encoded, t.size, t.ckey)).WithFile(mount, m.ekey, m.encoded, m.size, m.ckey);
        var r = Store(patched);

        // New archive after the existing ones; indices = every current entry plus the new archive's.
        var writer = new SnapshotWriter(dataDirectory, SnapshotWriter.NewTail());
        var archive = Directory.GetFiles(Path.Combine(dataDirectory, "data"), "data.*").Select(p => Path.GetExtension(p).TrimStart('.'))
            .Select(e => int.TryParse(e, out var n) ? n : -1).Max() + 1;
        var added = writer.WriteNewArchive(archive, stored);
        var indexVersion = NextIndexVersion(dataDirectory);
        writer.WriteIndices(storage.Index.Select(e => new SnapshotWriter.IndexEntry(e.Key, e.Value.Archive, e.Value.Offset, e.Value.Size)).Concat(added), indexVersion);

        // Build config: the same build with the new vfs-root (and every vfs-N that is the same file) and the new mount manifest.
        var hex = (byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
        var replaced = new Dictionary<string, string>();
        foreach (var (name, keys) in vfs)
        {
            if (keys[1] == rootKeys[1]) { replaced[name] = $"{hex(r.ckey)} {hex(r.ekey)}"; replaced[name + "-size"] = $"{r.size} {r.encoded}"; }
            if (name == mountName) { replaced[name] = $"{hex(m.ckey)} {hex(m.ekey)}"; replaced[name + "-size"] = $"{m.size} {m.encoded}"; }
        }
        var lines = configText.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var key = line.Split(" = ", 2)[0];
            return line.Contains(" = ") && replaced.TryGetValue(key, out var value) ? $"{key} = {value}" : line;
        });
        var newConfig = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        var newKey = hex(MD5.HashData(newConfig));
        var newConfigPath = storage.ConfigPath("config", newKey);
        Directory.CreateDirectory(Path.GetDirectoryName(newConfigPath));
        File.WriteAllBytes(newConfigPath, newConfig);

        storage.Dispose();
        File.Copy(Path.Combine(client, ".build.info"), Path.Combine(client, BaseBuildInfo), true);
        SetBuildKey(client, product, newKey);
        File.Delete(Path.Combine(dataDirectory, "data", "shmem"));
        File.WriteAllText(Path.Combine(dataDirectory, StateFile), JsonSerializer.Serialize(new State(baseKey, newKey, archive, indexVersion, files.Select(f => f.FileDataId).ToArray())));
        return archive;
    }

    // Files as the client finds them (Tact.log "Unable to lookup file with path '<mount>:<FDID><CKey>'"): build config vfs-root ->
    // ".root" (TSFM) -> the file's block gives the mount and CKey -> that mount's manifest -> the entry's EKey -> content, which
    // must hash to the CKey. Returns the contents by FileDataID.
    public static Dictionary<int, byte[]> ReadBack(string client, string product, string version, IEnumerable<int> fileDataIds)
    {
        var info = BuildInfo.Read(client, product, version);
        using var storage = new SourceStorage(client);
        var config = SourceStorage.ReadConfig(storage.ConfigPath("config", info["Build Key"]));
        var vfsRoot = Tvfs.Parse(storage.ReadFile(Convert.FromHexString(config["vfs-root"][1])));
        var tsfm = Root.Parse(storage.ReadFile(vfsRoot.CftAt(vfsRoot.Entries.Single(e => e.Path == ".root").Spans[0].Cft).EKey));
        var manifests = new Dictionary<string, Tvfs>();
        var result = new Dictionary<int, byte[]>();
        foreach (var id in fileDataIds)
        {
            var path = tsfm.TvfsPath(id) ?? throw new InvalidDataException($"FileDataID {id} is not in the root.");
            var (mount, name) = (path[..16], path[17..]);
            if (!manifests.TryGetValue(mount, out var manifest))
                manifests[mount] = manifest = Tvfs.Parse(storage.ReadFile(vfsRoot.CftAt((vfsRoot.Entries.SingleOrDefault(e => e.Path == mount)
                    ?? throw new InvalidDataException($"{path}: no mount {mount} in vfs-root.")).Spans[0].Cft).EKey));
            var entry = manifest.Entries.SingleOrDefault(e => e.Path == name) ?? throw new InvalidDataException($"{path}: not in the mount's manifest.");
            var c = manifest.CftAt(entry.Spans[0].Cft);
            var content = storage.ReadFile(c.EKey);
            if (!MD5.HashData(content).AsSpan().SequenceEqual(c.CKey) || Convert.ToHexString(c.CKey) != name[8..] || content.Length != c.ContentSize)
                throw new InvalidDataException($"{path} does not match its content.");
            result[id] = content;
        }
        return result;
    }

    // Puts the storage back as it was before Append: the base .build.info, and indices without the added archive (rewritten as the
    // next version, since the game itself writes newer index versions that still list it); the archive and config are deleted.
    public static void Remove(string client)
    {
        var dataDirectory = Path.Combine(client, "Data");
        var statePath = Path.Combine(dataDirectory, StateFile);
        if (!File.Exists(statePath)) return;
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath));
        File.Copy(Path.Combine(client, BaseBuildInfo), Path.Combine(client, ".build.info"), true);
        using (var storage = new SourceStorage(client))
        {
            var keep = storage.Index.Where(e => e.Value.Archive != state.Archive).Select(e => new SnapshotWriter.IndexEntry(e.Key, e.Value.Archive, e.Value.Offset, e.Value.Size)).ToList();
            new SnapshotWriter(dataDirectory, SnapshotWriter.NewTail()).WriteIndices(keep, NextIndexVersion(dataDirectory));
        }
        File.Delete(Path.Combine(dataDirectory, "data", $"data.{state.Archive:D3}"));
        File.Delete(Path.Combine(dataDirectory, "config", state.BuildKey[..2], state.BuildKey[2..4], state.BuildKey));
        File.Delete(Path.Combine(dataDirectory, "data", "shmem"));
        File.Delete(Path.Combine(client, BaseBuildInfo));
        File.Delete(statePath);
    }

    private static uint NextIndexVersion(string dataDirectory) =>
        Directory.GetFiles(Path.Combine(dataDirectory, "data"), "*.idx").Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(n => n.Length == 10).Select(n => Convert.ToUInt32(n[2..], 16)).Max() + 1;

    // BLTE as the client's own blobs (b:{256K*=z}): a chunk table (flags 0x0F, 24-bit count, per chunk compressed size, size and MD5)
    // and 256 KiB zlib chunks ('Z'); the encoding key is the MD5 of the chunk table header.
    private static (byte[] blte, byte[] ekey) Blte(byte[] content)
    {
        const int chunkSize = 256 * 1024;
        var chunks = new List<(byte[] data, int size)>();
        for (var o = 0; o < content.Length || chunks.Count == 0; o += chunkSize)
        {
            var part = content.AsSpan(o, Math.Min(chunkSize, content.Length - o));
            using var compressed = new MemoryStream();
            compressed.WriteByte((byte)'Z');
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(part);
            chunks.Add((compressed.ToArray(), part.Length));
        }
        var headerSize = 12 + 24 * chunks.Count;
        var header = new byte[headerSize];
        "BLTE"u8.CopyTo(header); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), headerSize);
        header[8] = 0x0F; header[9] = (byte)(chunks.Count >> 16); header[10] = (byte)(chunks.Count >> 8); header[11] = (byte)chunks.Count;
        for (var i = 0; i < chunks.Count; i++)
        {
            var at = 12 + 24 * i;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(at), chunks[i].data.Length);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(at + 4), chunks[i].size);
            MD5.HashData(chunks[i].data).CopyTo(header, at + 8);
        }
        return (header.Concat(chunks.SelectMany(c => c.data)).ToArray(), MD5.HashData(header));
    }
    private static void SetBuildKey(string client, string product, string key)
    {
        var path = Path.Combine(client, ".build.info");
        var lines = File.ReadAllLines(path);
        var header = lines[0].Split('|');
        int Column(string name) => Array.FindIndex(header, h => h.Split('!')[0] == name);
        for (var i = 1; i < lines.Length; i++)
        {
            var row = lines[i].Split('|');
            if (row.Length == header.Length && row[Column("Product")] == product) { row[Column("Build Key")] = key; lines[i] = string.Join('|', row); }
        }
        File.WriteAllText(path + ".tmp", string.Join("\n", lines) + "\n");
        File.Move(path + ".tmp", path, true);
    }

    // Root TSFM v2: header (magic, header size, version, total files, named files), blocks of (count, locale, content flags in
    // three parts), FileDataID deltas, CKeys, and name hashes unless the block has NoNameHash.
    private sealed class Root
    {
        private byte[] data;
        private readonly List<(int start, int count, uint locale, uint flags1, uint flags2, byte flags3, int[] ids)> blocks = new();
        private readonly HashSet<int> ids = new();

        public static Root Parse(byte[] raw)
        {
            if (Encoding.ASCII.GetString(raw, 0, 4) != "TSFM" || BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8)) != 2)
                throw new InvalidDataException("Unsupported root format.");
            var root = new Root { data = raw };
            for (var pos = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4)); pos + 17 <= raw.Length; )
            {
                var start = pos;
                var count = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(pos)); var locale = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(pos + 4));
                uint f1 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(pos + 8)), f2 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(pos + 12)); var f3 = raw[pos + 16];
                pos += 17;
                var blockIds = new int[count]; var id = -1;
                for (var i = 0; i < count; i++) { id += 1 + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(pos + 4 * i)); blockIds[i] = id; root.ids.Add(id); }
                pos += 20 * count + ((f2 & NoNameHash) == 0 ? 8 * count : 0);   // no-name-hash is a second-part flag
                root.blocks.Add((start, count, locale, f1, f2, f3, blockIds));
            }
            return root;
        }

        public bool Has(int id) => ids.Contains(id);
        public int Count => ids.Count;

        // The client's TVFS path for a file: "<locale><first content-flags part>:<FileDataID><CKey>" (first block that has it).
        public string TvfsPath(int id)
        {
            foreach (var b in blocks)
            {
                var i = Array.IndexOf(b.ids, id);
                if (i < 0) continue;
                var ckey = data.AsSpan(b.start + 17 + 4 * b.count + 16 * i, 16);
                return $"{b.locale:X8}{b.flags1:X8}:{id:X8}{Convert.ToHexString(ckey)}";
            }
            return null;
        }

        // Header fields against the blocks, and where the no-name-hash flag sits in blocks without name hashes.
        public string Describe()
        {
            var header = Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) / 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 * i))).ToArray();
            long total = blocks.Sum(b => (long)b.count), named = blocks.Where(b => (b.flags2 & NoNameHash) == 0).Sum(b => (long)b.count);
            var unnamed = blocks.Where(b => (b.flags2 & NoNameHash) != 0).ToList();
            var like = blocks.FirstOrDefault(b => b.ids.Contains(ForeverWallModels.CopySource));
            return $"root header {string.Join(" ", header.Select(h => $"0x{h:X}"))}; blocks {blocks.Count}, entries {total:N0}, with names {named:N0}; "
                + $"no-name blocks {unnamed.Count}: flag in f1 {unnamed.Count(b => (b.flags1 & NoNameHash) != 0)}, f2 {unnamed.Count(b => (b.flags2 & NoNameHash) != 0)}; "
                + $"sample unnamed flags {string.Join(", ", unnamed.Take(4).Select(b => $"loc 0x{b.locale:X} f1 0x{b.flags1:X} f2 0x{b.flags2:X} f3 0x{b.flags3:X}"))}; "
                + $"block of {ForeverWallModels.CopySource}: loc 0x{like.locale:X} f1 0x{like.flags1:X} f2 0x{like.flags2:X} f3 0x{like.flags3:X} count {like.count}; "
                + $"last blocks {string.Join(", ", blocks.TakeLast(3).Select(b => $"[{b.count} loc 0x{b.locale:X} f1 0x{b.flags1:X} f2 0x{b.flags2:X} f3 0x{b.flags3:X} first {b.ids.FirstOrDefault()}]"))}";
        }

        // The old root plus one block with the new files, without name hashes. The client looks a file up in TVFS as
        // "<locale 8 hex><content flags 8 hex>:<FileDataID><CKey>", so the block's locale and first content-flags part are those of
        // the vfs-root mount holding the files; the other parts are those of the block that holds `likeIds` (files of the same kind).
        // The total file count in the header grows by the new files.
        public byte[] WithBlock(IReadOnlyList<(int id, byte[] ckey)> files, int[] likeIds, string mount)
        {
            var like = blocks.FirstOrDefault(b => (b.locale & 0x2) != 0 && b.ids.Any(likeIds.Contains));
            if (like.ids == null) throw new InvalidDataException("No root block holds the reference files.");
            uint locale = Convert.ToUInt32(mount[..8], 16), content = Convert.ToUInt32(mount[8..16], 16);
            var sorted = files.OrderBy(f => f.id).ToList();
            var block = new byte[17 + 20 * sorted.Count];
            BinaryPrimitives.WriteInt32LittleEndian(block, sorted.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), locale);
            // The client reads the three content-flag parts separately: no-name-hash lives in the second part (all 335 such blocks of
            // this build have it there, e.g. 0x12000000), never in the first.
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), content);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), like.flags2 | NoNameHash);
            block[16] = like.flags3;
            var previous = -1;
            for (var i = 0; i < sorted.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(17 + 4 * i), sorted[i].id - previous - 1); previous = sorted[i].id;
                sorted[i].ckey.CopyTo(block, 17 + 4 * sorted.Count + 16 * i);
            }
            var result = data.Concat(block).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)) + sorted.Count);
            return result;
        }
    }

    // Encoding: header, ESpec strings, CKey -> EKeys pages and EKey -> ESpec pages (each with a first-key + MD5 page index), trailer.
    private sealed class Encoding_
    {
        private byte[] header, trailer;
        private List<string> specs;
        private int ceKeyPageSize, eSpecPageSize;
        private readonly List<(byte[] ckey, long size, byte[][] ekeys)> ceKeys = new();
        private readonly List<(byte[] ekey, uint spec, long size)> eKeys = new();

        public static Encoding_ Parse(byte[] raw)
        {
            if (raw[0] != (byte)'E' || raw[1] != (byte)'N' || raw[3] != 16 || raw[4] != 16) throw new InvalidDataException("Unsupported encoding table.");
            var e = new Encoding_ { header = raw[..22] };
            e.ceKeyPageSize = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(5)) * 1024; e.eSpecPageSize = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(7)) * 1024;
            int cePages = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(9)), esPages = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(13)), specSize = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(18));
            e.specs = Encoding.ASCII.GetString(raw, 22, specSize).Split('\0').ToList();
            if (e.specs[^1] == "") e.specs.RemoveAt(e.specs.Count - 1);
            var pos = 22 + specSize + cePages * 32;
            for (var page = 0; page < cePages; page++, pos += e.ceKeyPageSize)
                for (int p = pos, end = pos + e.ceKeyPageSize; p + 22 <= end && raw[p] != 0; )
                {
                    int count = raw[p]; long size = 0; for (var b = 0; b < 5; b++) size = size << 8 | raw[p + 1 + b];
                    var ekeys = Enumerable.Range(0, count).Select(k => raw.AsSpan(p + 22 + 16 * k, 16).ToArray()).ToArray();
                    e.ceKeys.Add((raw.AsSpan(p + 6, 16).ToArray(), size, ekeys));
                    p += 22 + 16 * count;
                }
            pos += esPages * 32;
            for (var page = 0; page < esPages; page++, pos += e.eSpecPageSize)
                for (int p = pos, end = pos + e.eSpecPageSize; p + 25 <= end && raw.AsSpan(p, 16).IndexOfAnyExcept((byte)0) >= 0; p += 25)
                {
                    long size = 0; for (var b = 0; b < 5; b++) size = size << 8 | raw[p + 20 + b];
                    e.eKeys.Add((raw.AsSpan(p, 16).ToArray(), BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(p + 16)), size));
                }
            e.trailer = raw[pos..];
            return e;
        }

        public byte[] EKeyOf(byte[] ckey) => ceKeys.FirstOrDefault(c => c.ckey.AsSpan().SequenceEqual(ckey)).ekeys?.FirstOrDefault();
        public string Counts => $"{ceKeys.Count:N0} content keys, {eKeys.Count:N0} encoding keys, {specs.Count} specs";

        public uint SpecIndex(string spec)
        {
            var index = specs.IndexOf(spec);
            if (index < 0) { specs.Add(spec); index = specs.Count - 1; }
            return (uint)index;
        }

        public void Add(byte[] ckey, byte[] ekey, long size, long encodedSize, uint spec)
        {
            if (!ceKeys.Any(c => c.ckey.AsSpan().SequenceEqual(ckey))) ceKeys.Add((ckey, size, new[] { ekey }));
            if (!eKeys.Any(k => k.ekey.AsSpan().SequenceEqual(ekey))) eKeys.Add((ekey, spec, encodedSize));
        }

        public byte[] Serialize()
        {
            static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);
            var ce = ceKeys.OrderBy(c => c.ckey, Comparer<byte[]>.Create(Compare)).ToList();
            var es = eKeys.OrderBy(k => k.ekey, Comparer<byte[]>.Create(Compare)).ToList();
            var cePages = Paginate(ce.Select(c =>
            {
                var entry = new byte[22 + 16 * c.ekeys.Length]; entry[0] = (byte)c.ekeys.Length;
                for (var b = 0; b < 5; b++) entry[1 + b] = (byte)(c.size >> (8 * (4 - b)));
                c.ckey.CopyTo(entry, 6); for (var k = 0; k < c.ekeys.Length; k++) c.ekeys[k].CopyTo(entry, 22 + 16 * k);
                return (c.ckey, entry);
            }), ceKeyPageSize);
            var esPages = Paginate(es.Select(k =>
            {
                var entry = new byte[25]; k.ekey.CopyTo(entry, 0); BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(16), k.spec);
                for (var b = 0; b < 5; b++) entry[20 + b] = (byte)(k.size >> (8 * (4 - b)));
                return (k.ekey, entry);
            }), eSpecPageSize);
            // The EKey -> ESpec list ends with a terminator entry (zero key, spec index 0xFFFFFFFF) in the last page.
            var lastPage = esPages[^1].page; var lastUsed = (es.Count - 1) % (eSpecPageSize / 25) * 25 + 25;
            if (lastUsed + 25 > eSpecPageSize) throw new InvalidDataException("No room for the encoding table's terminator entry.");
            BinaryPrimitives.WriteUInt32BigEndian(lastPage.AsSpan(lastUsed + 16), 0xFFFFFFFF);
            var specBlock = Encoding.ASCII.GetBytes(string.Concat(specs.Select(s => s + "\0")));
            using var output = new MemoryStream();
            var head = header.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(9), cePages.Count); BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(13), esPages.Count);
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(18), specBlock.Length);
            output.Write(head); output.Write(specBlock);
            foreach (var (first, page) in cePages) { output.Write(first); output.Write(MD5.HashData(page)); }
            foreach (var (_, page) in cePages) output.Write(page);
            foreach (var (first, page) in esPages) { output.Write(first); output.Write(MD5.HashData(page)); }
            foreach (var (_, page) in esPages) output.Write(page);
            output.Write(trailer);
            return output.ToArray();
        }

        // Greedy pages in key order, zero-padded; each page is listed by its first key.
        private static List<(byte[] first, byte[] page)> Paginate(IEnumerable<(byte[] key, byte[] entry)> entries, int pageSize)
        {
            var pages = new List<(byte[], byte[])>(); byte[] page = null; byte[] first = null; var used = 0;
            foreach (var (key, entry) in entries)
            {
                if (page == null || used + entry.Length > pageSize) { if (page != null) pages.Add((first, page)); page = new byte[pageSize]; first = key; used = 0; }
                entry.CopyTo(page, used); used += entry.Length;
            }
            if (page != null) pages.Add((first, page));
            return pages;
        }
    }
}
