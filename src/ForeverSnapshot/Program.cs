// ForeverSnapshot: freezes one client build into a standalone install that Battle.net never touches.
//   create --from "C:\Blizzard Games\World of Warcraft" --to "<product>\client"   (resumable)
//   verify "<product>\client" [--quick]
// The source is only read. The snapshot holds the build's own content (download manifest for this install's tags, plus the
// build's manifests), the program folder, the build and CDN configs, and a one-row .build.info.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AuthServer.Network;
using static ForeverSnapshot.CascFormat;

namespace ForeverSnapshot;

internal static class Program
{
    private const string DefaultProduct = "wow_classic_beta", DefaultVersion = "1.60.1.70338";
    private static readonly string[] SkippedProgramFolders = { "Cache", "Logs", "Errors", "Screenshots", "WTF" };
    private static readonly string[] SandboxConfigs = { "ForeverSandbox.wtf" };

    private static int Main(string[] args)
    {
        try
        {
            var options = Options(args.Skip(1));
            switch (args.FirstOrDefault())
            {
                case "create": return Create(Required(options, "from"), Required(options, "to"), options.GetValueOrDefault("product", DefaultProduct), options.GetValueOrDefault("version", DefaultVersion));
                case "verify": return Verify(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Required(options, "client"), options.ContainsKey("quick"));
                case "walls": return Walls(args.Length > 1 ? args[1] : "", Required(options, "to"));
                default:
                    Console.WriteLine("ForeverSnapshot create --from <Battle.net WoW folder> --to <snapshot folder> [--product wow_classic_beta] [--version 1.60.1.70338]");
                    Console.WriteLine("ForeverSnapshot verify <snapshot folder> [--quick]");
                    Console.WriteLine("ForeverSnapshot walls install|remove|status --to <snapshot folder>   (the .walls models)");
                    return 2;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine("ERROR: " + (Environment.GetEnvironmentVariable("FOREVER_SNAPSHOT_DEBUG") == "1" ? error.ToString() : error.Message));
            return 1;
        }
    }

    // The .walls models (WallModels) and a copy of a client model (to tell storage problems from model problems) added to the
    // snapshot (StorageAppend); every added file is read back through the server's own storage reader. remove undoes it exactly.
    private static int Walls(string action, string client)
    {
        switch (action)
        {
            case "status":
                Console.WriteLine(StorageAppend.Installed(client) ? "Wall models are installed." : "Wall models are not installed.");
                return 0;
            case "check":
                Console.WriteLine(StorageAppend.Check(client, DefaultProduct, DefaultVersion));
                return 0;
            case "tvfs":
                Console.WriteLine(StorageAppend.Survey(client, DefaultProduct, DefaultVersion, ForeverWallModels.CopySource));
                return 0;
            case "export":   // the generated files only, into the --to folder (for inspection)
                Directory.CreateDirectory(client);
                File.WriteAllBytes(Path.Combine(client, $"{ForeverWallModels.SkinFile}.skin"), WallModels.Skin());
                for (var tint = 0; tint < ForeverWallModels.Tints.Length; tint++)
                    foreach (var red in new[] { false, true })
                        File.WriteAllBytes(Path.Combine(client, $"{ForeverWallModels.ModelFile(tint, red)}.m2"),
                            WallModels.Model(WallModels.Face(tint, red), red ? WallModels.RedLine : WallModels.YellowLine, ForeverWallModels.SkinFile));
                return 0;
            case "remove":
                StorageAppend.Remove(client);
                Console.WriteLine("Wall models removed; the snapshot is back to its own build.");
                return 0;
            case "install":
                StorageAppend.Remove(client);   // a previous install first, so everything below reads the snapshot's own build
                byte[] copy;
                using (var casc = ForeverCasc.Open(client, DefaultVersion, _ => null)) copy = casc.OpenFile(ForeverWallModels.CopySource);
                var files = new List<StorageAppend.NewFile> { new(ForeverWallModels.CopyFile, copy), new(ForeverWallModels.SkinFile, WallModels.Skin()) };
                for (var tint = 0; tint < ForeverWallModels.Tints.Length; tint++)
                    foreach (var red in new[] { false, true })
                        files.Add(new(ForeverWallModels.ModelFile(tint, red), WallModels.Model(WallModels.Face(tint, red), red ? WallModels.RedLine : WallModels.YellowLine, ForeverWallModels.SkinFile)));
                var clock = Stopwatch.StartNew();
                var archive = StorageAppend.Append(client, DefaultProduct, DefaultVersion, files, new[] { ForeverWallModels.CopySource }, ForeverWallModels.Mount);
                // The new archive's entries as `verify` checks every entry: header key, size, checksums at their place, data key.
                using (var check = new SourceStorage(client))
                {
                    var buffer = Array.Empty<byte>(); var entries = 0;
                    foreach (var (key, location) in check.Index.Where(e => e.Value.Archive == archive))
                    {
                        buffer = new byte[location.Size]; check.ReadRaw(location, buffer);
                        if (!HeaderKeyMatches(buffer, key) || !ChecksumsValid(buffer, location.Archive, location.Offset) || BitConverter.ToInt32(buffer, 0x10) != location.Size
                            || location.Size > HeaderSize && !BlteMatchesKey(buffer.AsSpan(HeaderSize), key))
                            throw new InvalidDataException($"New entry {key} at data.{archive:D3}+{location.Offset} does not verify.");
                        entries++;
                    }
                    if (entries != 16 + files.Count + 3) throw new InvalidDataException($"The new archive has {entries} indexed entries, expected {16 + files.Count + 3}.");
                }
                // As the client finds them (vfs-root -> mount manifest -> EKeys), and the server's own reader still opens the build.
                var back = StorageAppend.ReadBack(client, DefaultProduct, DefaultVersion, files.Select(f => f.FileDataId).Append(ForeverWallModels.CopySource));
                foreach (var file in files)
                    if (!back.TryGetValue(file.FileDataId, out var content) || !content.AsSpan().SequenceEqual(file.Content)) throw new InvalidDataException($"FileDataID {file.FileDataId} does not read back.");
                using (var casc = ForeverCasc.Open(client, DefaultVersion, _ => null))
                    if (!casc.OpenFile(ForeverWallModels.CopySource).AsSpan().SequenceEqual(copy)) throw new InvalidDataException("The client's own model no longer reads back.");
                Console.WriteLine($"Installed {files.Count} files ({files.Sum(f => f.Content.Length):N0} bytes) in {clock.Elapsed.TotalSeconds:0}s; all read back through vfs-root.");
                return 0;
            default:
                Console.WriteLine("ForeverSnapshot walls install|remove|status --to <snapshot folder>");
                return 2;
        }
    }

    private static Dictionary<string, string> Options(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
            if (list[i].StartsWith("--")) result[list[i][2..]] = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : "";
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is required.");

    private sealed class Progress
    {
        public string LayoutHash { get; set; }
        public string Tail { get; set; }
        public List<int> CompletedArchives { get; set; } = new();
        public List<string> Holes { get; set; } = new();
    }

    private static int Create(string from, string to, string product, string version)
    {
        from = Path.GetFullPath(from).TrimEnd('\\'); to = Path.GetFullPath(to).TrimEnd('\\');
        if (to.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase) || to.Equals(from, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The snapshot folder must be outside the Battle.net install.");
        if (Process.GetProcessesByName("WowB").Length > 0) throw new InvalidOperationException("Close the game first: it compacts its storage while running.");

        var info = BuildInfo.Read(from, product, version);
        var programFolder = ProgramFolder(from, product);
        Console.WriteLine($"Source: {product} {version} (build {info["Build Key"]}), program folder {Path.GetFileName(programFolder)}");
        Console.WriteLine($"Selected tags: {string.Join(' ', info.SelectedTags)}");

        using var source = new SourceStorage(from);
        Console.WriteLine($"Local indices: {source.IndexFiles} files, {source.Index.Count:N0} entries (header and entry hashes verified).");
        var build = SourceStorage.ReadConfig(source.ConfigPath("config", info["Build Key"]));
        var cdn = SourceStorage.ReadConfig(source.ConfigPath("config", info["CDN Key"]));
        var encoding = new EncodingTable(source.ReadFile(Convert.FromHexString(build["encoding"][1])));

        // What the build needs: the download manifest for this install's tags, plus the build's own manifests.
        var (download, downloadTags) = Manifests.ReadDownload(source.ReadFile(Convert.FromHexString(build["download"][1])));
        var (install, installTags) = Manifests.ReadInstall(source.ReadFile(Convert.FromHexString(build["install"][1])));
        var selectDownload = Manifests.Selector(downloadTags, info.SelectedTags);
        var selectInstall = Manifests.Selector(installTags, info.SelectedTags);
        var installFiles = install.Where(e => selectInstall(e.Index)).ToList();
        var looseKeys = installFiles.SelectMany(e => encoding.EKeys(e.CKey)).Select(k => Key9.From(k)).ToHashSet();

        var needed = new Dictionary<Key9, SourceStorage.Location>();
        int loose = 0, missing = 0; long missingBytes = 0;
        foreach (var entry in download.Where(e => selectDownload(e.Index)))
        {
            var key = Key9.From(entry.EKey);
            if (source.Index.TryGetValue(key, out var location)) needed.TryAdd(key, location);
            else if (looseKeys.Contains(key)) loose++;
            else { missing++; missingBytes += entry.Size; }
        }
        foreach (var (name, values) in build)
        {
            var keys = new List<byte[]>();
            if (name == "root") keys.AddRange(encoding.EKeys(Convert.FromHexString(values[0])));
            else if ((name is "encoding" or "install" or "download" or "size" or "patch-index" || name.StartsWith("vfs-")) && !name.EndsWith("-size") && values.Length >= 2) keys.Add(Convert.FromHexString(values[1]));
            foreach (var k in keys) if (source.Index.TryGetValue(Key9.From(k), out var location)) needed.TryAdd(Key9.From(k), location);
        }
        var totalBytes = needed.Values.Sum(l => (long)l.Size);
        Console.WriteLine($"Build content: {needed.Count:N0} entries, {totalBytes / 1e9:F2} GB; {loose} install files kept as loose files; {missing} not stored locally ({missingBytes / 1e9:F2} GB).");
        if (missing > 0) throw new InvalidDataException("Some of the build's content is not downloaded; the snapshot would not be complete.");

        // Every source header must check out at its current place before anything is written (proves the checksum code).
        Console.Write("Checking source headers... ");
        Span<byte> header = stackalloc byte[HeaderSize];
        var checkedHeaders = 0;
        foreach (var (key, location) in needed.OrderBy(p => p.Value.Archive).ThenBy(p => p.Value.Offset))
        {
            source.ReadRaw(location with { Size = HeaderSize }, header);
            if (!HeaderKeyMatches(header, key) || !ChecksumsValid(header, location.Archive, location.Offset))
                throw new InvalidDataException($"Source header for {key} at data.{location.Archive:D3}+{location.Offset} does not verify.");
            checkedHeaders++;
        }
        Console.WriteLine($"{checkedHeaders:N0} verified.");

        var placed = SnapshotWriter.Layout(needed.Select(p => (p.Key, p.Value)));
        var archiveCount = placed.Count == 0 ? 0 : placed[^1].Archive + 1;
        var layoutHash = LayoutHash(placed);
        Directory.CreateDirectory(to);
        var drive = new DriveInfo(Path.GetPathRoot(to)!);
        var progressPath = Path.Combine(to, "snapshot-progress.json");
        var progress = File.Exists(progressPath) ? JsonSerializer.Deserialize<Progress>(File.ReadAllText(progressPath)) : null;
        if (progress?.LayoutHash != layoutHash)
        {
            if (Directory.Exists(Path.Combine(to, "Data")) || File.Exists(Path.Combine(to, ".build.info")))
                throw new InvalidOperationException("The snapshot folder already holds a different or finished snapshot; choose an empty folder.");
            progress = new Progress { LayoutHash = layoutHash, Tail = Convert.ToHexString(SnapshotWriter.NewTail()) };
        }
        var remaining = placed.Where(p => !progress.CompletedArchives.Contains(p.Archive)).Sum(p => (long)p.Source.Size);
        if (drive.AvailableFreeSpace < remaining + 3L * 1024 * 1024 * 1024) throw new IOException($"Not enough free space on {drive.Name}: need about {(remaining >> 30) + 3} GB.");
        SaveProgress(progressPath, progress);

        var writer = new SnapshotWriter(Path.Combine(to, "Data"), Convert.FromHexString(progress.Tail));
        var clock = Stopwatch.StartNew(); long done = placed.Where(p => progress.CompletedArchives.Contains(p.Archive)).Sum(p => (long)p.Source.Size); var lastReport = TimeSpan.Zero;
        var startDone = done;
        foreach (var group in placed.GroupBy(p => p.Archive))
        {
            if (progress.CompletedArchives.Contains(group.Key) && File.Exists(writer.ArchivePath(group.Key))) continue;
            var holes = new List<Key9>();
            writer.WriteArchive(group.Key, group.ToList(), source, bytes =>
            {
                done += bytes;
                if (clock.Elapsed - lastReport < TimeSpan.FromSeconds(2)) return;
                lastReport = clock.Elapsed;
                var rate = (done - startDone) / Math.Max(1, clock.Elapsed.TotalSeconds);
                Console.Write($"\rCopying: archive {group.Key + 1}/{archiveCount}, {done / 1e9:F2}/{totalBytes / 1e9:F2} GB, {rate / 1e6:F0} MB/s, about {TimeSpan.FromSeconds((totalBytes - done) / Math.Max(1, rate)):hh\\:mm\\:ss} left   ");
            }, holes);
            progress.Holes.AddRange(holes.Select(h => h.ToString()));
            progress.CompletedArchives.Add(group.Key);
            SaveProgress(progressPath, progress);
        }
        Console.WriteLine($"\rCopied {totalBytes / 1e9:F2} GB into {archiveCount} archives in {clock.Elapsed:hh\\:mm\\:ss}.                                   ");
        if (progress.Holes.Count > 0) Console.WriteLine($"{progress.Holes.Count} entries were never downloaded by Battle.net (zero-filled in the install too); kept as they are: {string.Join(' ', progress.Holes)}");

        writer.WriteIndices(placed.Select(p => new SnapshotWriter.IndexEntry(p.Key, p.Archive, p.Offset, p.Source.Size))
            .Concat(Enumerable.Range(0, archiveCount).SelectMany(writer.CrossEntries)));
        Console.WriteLine("Wrote 16 indices.");

        // Configs: this build's build and CDN configs, and the CDN indices that config names.
        foreach (var key in new[] { info["Build Key"], info["CDN Key"] }) CopyFile(source.ConfigPath("config", key), Path.Combine(to, "Data", "config", key[..2], key[2..4], key));
        var indexCount = 0;
        foreach (var name in new[] { "archives", "archive-group", "patch-archives", "patch-archive-group", "file-index", "patch-file-index" })
            foreach (var key in cdn.GetValueOrDefault(name, Array.Empty<string>()))
            {
                var path = Path.Combine(source.DataDirectory, "indices", key + ".index");
                if (File.Exists(path)) { CopyFile(path, Path.Combine(to, "Data", "indices", key + ".index")); indexCount++; }
            }
        // Residency (the loose program files this product has): location independent; shmem is rebuilt by the game.
        var residency = Path.Combine(source.DataDirectory, product);
        if (Directory.Exists(residency))
            foreach (var path in Directory.GetFiles(residency).Where(p => !Path.GetFileName(p).Equals("shmem", StringComparison.OrdinalIgnoreCase)))
                CopyFile(path, Path.Combine(to, "Data", product, Path.GetFileName(path)));
        Console.WriteLine($"Copied configs and {indexCount} CDN indices.");

        // Program folder without per-machine state, then the sandbox's own client config.
        var programTarget = Path.Combine(to, Path.GetFileName(programFolder));
        CopyTree(programFolder, programTarget, top => !SkippedProgramFolders.Contains(top, StringComparer.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.Combine(programTarget, "WTF"));
        foreach (var config in SandboxConfigs)
        {
            var path = Path.Combine(programFolder, "WTF", config);
            if (File.Exists(path)) CopyFile(path, Path.Combine(programTarget, "WTF", config));
        }
        var verifiedFiles = VerifyInstallFiles(installFiles, programTarget);
        Console.WriteLine($"Copied the program folder; {verifiedFiles} install files match the install manifest.");

        File.WriteAllLines(Path.Combine(to, ".build.info"), new[] { string.Join('|', info.Header), string.Join('|', info.Row) });
        var manifest = new Dictionary<string, object>
        {
            ["product"] = product, ["version"] = version, ["buildName"] = build.GetValueOrDefault("build-name", Array.Empty<string>()).FirstOrDefault(),
            ["buildKey"] = info["Build Key"], ["cdnKey"] = info["CDN Key"], ["programFolder"] = Path.GetFileName(programFolder),
            ["selectedTags"] = info.SelectedTags, ["entries"] = needed.Count, ["bytes"] = totalBytes, ["archives"] = archiveCount,
            ["installFilesVerified"] = verifiedFiles, ["holes"] = progress.Holes, ["cdnIndices"] = indexCount, ["layoutSha256"] = layoutHash,
            ["executables"] = new[] { "WowB.exe", "WowB_loader.dll" }.Where(f => File.Exists(Path.Combine(programTarget, f)))
                .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(programTarget, f))))),
            ["createdUtc"] = DateTime.UtcNow.ToString("O"), ["tool"] = "ForeverSnapshot 1",
        };
        File.WriteAllText(Path.Combine(to, "snapshot.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Delete(progressPath);
        Console.WriteLine($"Snapshot complete: {to}");
        return 0;
    }

    private static int Verify(string root, bool quick)
    {
        root = Path.GetFullPath(root);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "snapshot.json"))).RootElement;
        string product = manifest.GetProperty("product").GetString(), version = manifest.GetProperty("version").GetString();
        var info = BuildInfo.Read(root, product, version);
        using var storage = new SourceStorage(root);
        Console.WriteLine($"Indices: {storage.IndexFiles} files, {storage.Index.Count:N0} entries (hashes verified).");

        // Every stored entry: header key, size and checksums at its place; full mode also re-checks the data against its key.
        int failures = 0; long bytes = 0; var clock = Stopwatch.StartNew(); var last = TimeSpan.Zero;
        var foundHoles = new List<string>();
        var buffer = new byte[1 << 20];
        foreach (var (key, location) in storage.Index.OrderBy(p => p.Value.Archive).ThenBy(p => p.Value.Offset))
        {
            var size = quick || location.Size == HeaderSize ? HeaderSize : location.Size;
            if (buffer.Length < size) buffer = new byte[size];
            storage.ReadRaw(location with { Size = size }, buffer);
            var raw = buffer.AsSpan(0, size);
            var ok = HeaderKeyMatches(raw, key) && ChecksumsValid(raw, location.Archive, location.Offset) && BitConverter.ToInt32(buffer, 0x10) == location.Size;
            if (ok && size > HeaderSize && !BlteMatchesKey(raw[HeaderSize..], key))
            {
                if (raw[HeaderSize..].IndexOfAnyExcept((byte)0) < 0) foundHoles.Add(key.ToString());
                else ok = false;
            }
            if (!ok && failures++ < 10) Console.WriteLine($"\nBad entry {key} at data.{location.Archive:D3}+{location.Offset}");
            bytes += size;
            if (clock.Elapsed - last > TimeSpan.FromSeconds(2)) { last = clock.Elapsed; Console.Write($"\rVerifying entries: {bytes / 1e9:F2} GB   "); }
        }
        Console.WriteLine($"\rEntries: {storage.Index.Count:N0} checked{(quick ? " (headers)" : " (headers and data)")}, {failures} bad.          ");
        if (!quick)
        {
            var recorded = manifest.TryGetProperty("holes", out var h) ? h.EnumerateArray().Select(e => e.GetString()).ToHashSet() : new HashSet<string>();
            var holesMatch = recorded.SetEquals(foundHoles);
            if (!holesMatch) failures++;
            Console.WriteLine($"Never-downloaded entries: {foundHoles.Count} ({(holesMatch ? "as recorded" : "DIFFERENT from the record")}).");
        }

        // Completeness against the snapshot's own manifests, the program files, and the executables.
        var build = SourceStorage.ReadConfig(storage.ConfigPath("config", info["Build Key"]));
        var encoding = new EncodingTable(storage.ReadFile(Convert.FromHexString(build["encoding"][1])));
        var (download, downloadTags) = Manifests.ReadDownload(storage.ReadFile(Convert.FromHexString(build["download"][1])));
        var (install, installTags) = Manifests.ReadInstall(storage.ReadFile(Convert.FromHexString(build["install"][1])));
        var selectDownload = Manifests.Selector(downloadTags, info.SelectedTags); var selectInstall = Manifests.Selector(installTags, info.SelectedTags);
        var installFiles = install.Where(e => selectInstall(e.Index)).ToList();
        var looseKeys = installFiles.SelectMany(e => encoding.EKeys(e.CKey)).Select(k => Key9.From(k)).ToHashSet();
        var absent = download.Where(e => selectDownload(e.Index)).Select(e => Key9.From(e.EKey)).Count(k => !storage.Index.ContainsKey(k) && !looseKeys.Contains(k));
        Console.WriteLine($"Build content missing: {absent}.");
        var programFolder = Path.Combine(root, manifest.GetProperty("programFolder").GetString());
        var installOk = 0;
        try { installOk = VerifyInstallFiles(installFiles, programFolder); Console.WriteLine($"Install files: {installOk} match."); }
        catch (InvalidDataException error) { failures++; Console.WriteLine(error.Message); }
        foreach (var exe in manifest.GetProperty("executables").EnumerateObject())
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(programFolder, exe.Name))));
            var match = actual == exe.Value.GetString();
            if (!match) failures++;
            Console.WriteLine($"{exe.Name}: {(match ? "matches" : "DIFFERS from")} the snapshot record.");
        }
        // The server's reader on the snapshot: Map.db2 must open.
        using (var casc = ForeverCasc.Open(root, version, _ => null))
            Console.WriteLine($"Server reader: Map.db2 {casc.OpenFile(1349477).Length:N0} bytes.");

        var passed = failures == 0 && absent == 0;
        Console.WriteLine(passed ? "Snapshot verified." : "Snapshot verification FAILED.");
        return passed ? 0 : 1;
    }

    private static string ProgramFolder(string install, string product)
    {
        foreach (var folder in Directory.GetDirectories(install, "_*_"))
        {
            var flavor = Path.Combine(folder, ".flavor.info");
            if (File.Exists(flavor) && File.ReadAllLines(flavor).Skip(1).Any(l => l.Trim() == product)) return folder;
        }
        throw new DirectoryNotFoundException($"No program folder with .flavor.info for {product}.");
    }

    private static int VerifyInstallFiles(List<Manifests.InstallEntry> files, string programFolder)
    {
        var bad = new List<string>();
        foreach (var file in files)
        {
            var path = Path.Combine(programFolder, file.Name.Replace('/', '\\'));
            if (!File.Exists(path) || !MD5.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(file.CKey)) bad.Add(file.Name);
        }
        if (bad.Count > 0) throw new InvalidDataException($"{bad.Count} program files are missing or differ from the install manifest (first: {bad[0]}).");
        return files.Count;
    }

    private static void CopyTree(string from, string to, Func<string, bool> includeTop)
    {
        foreach (var path in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, path);
            var top = relative.Split('\\')[0];
            if (relative.Contains('\\') && !includeTop(top)) continue;
            CopyFile(path, Path.Combine(to, relative));
        }
    }

    private static void CopyFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(to + ".tmp", FileMode.Create, FileAccess.Write);
        input.CopyTo(output);
        output.Close();
        File.Move(to + ".tmp", to, true);
    }

    private static string LayoutHash(List<SnapshotWriter.Placed> placed)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = stackalloc byte[9 + 12];
        foreach (var p in placed)
        {
            p.Key.Write(row); BitConverter.TryWriteBytes(row[9..], p.Archive); BitConverter.TryWriteBytes(row[13..], p.Offset); BitConverter.TryWriteBytes(row[17..], p.Source.Size);
            sha.AppendData(row);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void SaveProgress(string path, Progress progress)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(progress));
        File.Move(path + ".tmp", path, true);
    }
}
