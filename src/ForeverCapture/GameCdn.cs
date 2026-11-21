// Files the game build lists but never stored on disk: the game streams those from Blizzard's CDN while it plays, so the map
// window fetches them the same way. The build's merged CDN index (archive-group, kept on disk by Battle.net) gives the archive
// and offset of an encoding key; one ranged HTTP GET gets the BLTE stream, which must hash to that key. A key the index does not
// list is a loose CDN file, fetched whole.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AuthServer.Network;

namespace ForeverCapture;

internal sealed class GameCdn : IDisposable
{
    // CDN index: 4096-byte pages of sorted entries (16 key, 4 size, 2 archive, 4 offset, big-endian), then the last key of
    // each page (16 bytes), each page's checksum (8 bytes) and a 28-byte footer.
    private const int Page = 4096, Entry = 26, Footer = 28, MaxBytes = 64 << 20;
    private readonly FileStream index;
    private readonly byte[] lastKeys;
    private readonly int pages;
    private readonly string[] archives, bases;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<int, Lazy<byte[]>> fetched = new();

    private GameCdn(FileStream group, string[] archiveNames, string[] cdnBases)
    {
        index = group; archives = archiveNames; bases = cdnBases;
        var footer = new byte[Footer];
        index.Position = index.Length - Footer; index.ReadExactly(footer);
        if (footer[8] != 1 || footer[12] != 6 || footer[13] != 4 || footer[14] != 16 || (index.Length - Footer) % (Page + 24) != 0)
            throw new InvalidDataException("Unsupported CDN archive-group index layout.");
        pages = (int)((index.Length - Footer) / (Page + 24));
        lastKeys = new byte[pages * 16];
        index.Position = (long)pages * Page; index.ReadExactly(lastKeys);
    }

    /// <summary>The CDN of a client build, or null (with the reason) when its install does not name one.</summary>
    public static GameCdn Open(string installDirectory, string version, out string reason)
    {
        reason = null;
        var lines = File.ReadAllLines(Path.Combine(installDirectory, ".build.info"));
        var columns = lines[0].Split('|').Select(c => c.Split('!')[0]).ToArray();
        string Column(string[] row, string name)
        {
            var i = Array.IndexOf(columns, name);
            return i >= 0 && i < row.Length ? row[i] : "";
        }
        var row = lines.Skip(1).Select(l => l.Split('|')).FirstOrDefault(r => Column(r, "Version") == version);
        var hosts = row == null ? Array.Empty<string>() : Column(row, "CDN Hosts").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Length == 0) { reason = $"no CDN hosts for {version} in .build.info"; return null; }
        var key = Column(row, "CDN Key");
        var config = File.ReadAllLines(Path.Combine(installDirectory, "Data", "config", key[..2], key[2..4], key))
            .Where(l => l.Contains(" = ")).Select(l => l.Split(" = ", 2)).ToDictionary(p => p[0], p => p[1].Split(' '));
        var group = Path.Combine(installDirectory, "Data", "indices", config["archive-group"][0] + ".index");
        if (!File.Exists(group)) { reason = "the build's CDN index is not on disk"; return null; }
        var cdnPath = Column(row, "CDN Path");
        return new GameCdn(new FileStream(group, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), config["archives"],
            hosts.Select(host => $"http://{host}/{cdnPath}/data/").ToArray());
    }

    /// <summary>A listed file the disk lacks, decoded (once per file; null when the CDN cannot give it).</summary>
    // shortcut: a failed fetch is not tried again until the map window restarts; retry after a delay if flaky networks show up.
    public byte[] Fetch(ForeverCasc game, int fileDataId) => fetched.GetOrAdd(fileDataId, id => new Lazy<byte[]>(() =>
    {
        foreach (var key in game.EncodingKeys(id))
        {
            var blte = Download(key);
            if (blte != null) return ForeverCasc.Blte(blte, _ => null);
        }
        return null;
    })).Value;

    private byte[] Download(byte[] key)
    {
        var (archive, offset, size) = Find(key);
        if (size > MaxBytes) return null;
        var hex = Convert.ToHexString(key).ToLowerInvariant();
        foreach (var root in bases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, archive == null ? $"{root}{hex[..2]}/{hex[2..4]}/{hex}" : $"{root}{archive[..2]}/{archive[2..4]}/{archive}");
            if (archive != null) request.Headers.Range = new RangeHeaderValue(offset, offset + size - 1);
            try
            {
                using var response = http.Send(request);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) continue;
                using var memory = new MemoryStream();
                response.Content.ReadAsStream().CopyTo(memory);
                var bytes = memory.ToArray();
                if (Matches(bytes, key)) return bytes;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException) { }
        }
        return null;
    }

    // Archive name, offset and size of a key in the archive-group index; a null name when it is not there (a loose file).
    private (string Archive, long Offset, int Size) Find(byte[] key)
    {
        int lo = 0, hi = pages - 1, page = -1;   // the first page whose last key is not below the key
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (lastKeys.AsSpan(mid * 16, 16).SequenceCompareTo(key) >= 0) { page = mid; hi = mid - 1; } else lo = mid + 1;
        }
        if (page < 0) return (null, 0, 0);
        var data = new byte[Page];
        lock (index) { index.Position = (long)page * Page; index.ReadExactly(data); }
        for (var o = 0; o + Entry <= Page; o += Entry)
        {
            var entry = data.AsSpan(o, Entry);
            if (!entry[..16].SequenceEqual(key)) continue;
            var archive = BinaryPrimitives.ReadUInt16BigEndian(entry[20..]);
            return archive < archives.Length ? (archives[archive], BinaryPrimitives.ReadUInt32BigEndian(entry[22..]), BinaryPrimitives.ReadInt32BigEndian(entry[16..])) : (null, 0, 0);
        }
        return (null, 0, 0);
    }

    // The encoding key is the MD5 of the BLTE header (its size field counts from the stream start), or of the whole stream
    // when it has no chunk table.
    internal static bool Matches(byte[] blte, byte[] key)
    {
        if (blte.Length < 8 || blte[0] != 'B' || blte[1] != 'L' || blte[2] != 'T' || blte[3] != 'E') return false;
        var headerSize = BinaryPrimitives.ReadInt32BigEndian(blte.AsSpan(4));
        if (headerSize < 0 || headerSize > blte.Length) return false;
        return MD5.HashData(blte.AsSpan(0, headerSize == 0 ? blte.Length : headerSize)).AsSpan().SequenceEqual(key);
    }

    public void Dispose() { http.Dispose(); index.Dispose(); }
}
