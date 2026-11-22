// Entries the Battle.net index lists but whose data was never written (their place still holds an older file, or zeros). The
// live game fetches those from Blizzard's CDN on demand; a frozen snapshot has to hold them, so they are fetched the same way:
// the build's merged CDN index (archive-group, already on disk) gives archive and offset, one ranged HTTP GET gets the BLTE
// stream, and it must match its encoding key.
using System.Buffers.Binary;
using System.Net.Http.Headers;
using static ForeverSnapshot.CascFormat;

namespace ForeverSnapshot;

internal sealed class Cdn : IDisposable
{
    // CDN index v1: 4096-byte pages of sorted entries (16 key, 4 size, 2 archive, 4 offset, big-endian, zero padded), then per
    // page a 16-byte last key and an 8-byte checksum, then a 28-byte footer.
    private const int Page = 4096, Entry = 26, Footer = 28;
    private readonly byte[] group;
    private readonly string[] archives, bases;
    private readonly int pages;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public Cdn(BuildInfo info, Dictionary<string, string[]> cdnConfig, string dataDirectory)
    {
        archives = cdnConfig["archives"];
        bases = info["CDN Hosts"].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(host => $"http://{host}/{info["CDN Path"]}/data/").ToArray();
        group = File.ReadAllBytes(Path.Combine(dataDirectory, "indices", cdnConfig["archive-group"][0] + ".index"));
        var footer = group.AsSpan(group.Length - Footer);
        if (footer[8] != 1 || footer[11] != 4 || footer[12] != 6 || footer[13] != 4 || footer[14] != 16 || (group.Length - Footer) % (Page + 24) != 0)
            throw new InvalidDataException("Unsupported CDN archive-group index layout.");
        pages = (group.Length - Footer) / (Page + 24);
    }

    // The entry as a local archive entry (30-byte header: key byte-reversed, size, flags 0; checksums are the writer's), or
    // null when the CDN index does not list the key.
    public byte[] Fetch(Key9 key)
    {
        int lo = 0, hi = pages - 1, page = 0;   // last page whose first key is not above the key
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1; var first = Key9.From(group.AsSpan(mid * Page));
            if ((first.High, first.Low).CompareTo((key.High, key.Low)) <= 0) { page = mid; lo = mid + 1; } else hi = mid - 1;
        }
        for (var o = page * Page; o + Entry <= (page + 1) * Page; o += Entry)
        {
            var entry = group.AsSpan(o, Entry);
            if (Key9.From(entry) != key) continue;
            int size = BinaryPrimitives.ReadInt32BigEndian(entry[16..]), archive = BinaryPrimitives.ReadUInt16BigEndian(entry[20..]);
            long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[22..]);
            var name = archives[archive];
            var raw = new byte[HeaderSize + size];
            foreach (var root in bases)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}{name[..2]}/{name[2..4]}/{name}");
                request.Headers.Range = new RangeHeaderValue(offset, offset + size - 1);
                try
                {
                    using var response = http.Send(request);
                    if (!response.IsSuccessStatusCode) continue;
                    using var body = response.Content.ReadAsStream();
                    body.ReadExactly(raw, HeaderSize, size);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException) { continue; }
                if (!BlteMatchesKey(raw.AsSpan(HeaderSize), key)) continue;
                for (var i = 0; i < 16; i++) raw[15 - i] = entry[i];
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x10), raw.Length);
                return raw;
            }
            throw new IOException($"Entry {key} could not be fetched from the CDN ({string.Join(", ", bases)}).");
        }
        return null;
    }

    public void Dispose() => http.Dispose();
}
