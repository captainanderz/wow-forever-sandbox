// Independent 70338 hotfix and account-data glue support. The character list stays
// "waiting on startup prerequisites" (client General.log: hotfixes, TACT encryption,
// account settings) until these arrive. Factual layouts: pinned 5a125dfe
// HotfixPackets.cpp, HotfixHandler.cpp, ClientConfigPackets.cpp, MiscHandler.cpp.
// Record data comes from sandbox-data/hotfixes.json (tools/extract_reference_hotfixes.py).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace AuthServer.Network;

internal sealed class ForeverHotfixStore
{
    public sealed record Record(int Push, uint Unique, uint Table, int RecordId, byte Status, byte[] Data);
    public const uint AvailableHotfixesOpcode = 0x4B0001, HotfixConnectOpcode = 0x4B0003, DbReplyOpcode = 0x4B0000;
    public const uint HotfixRequestOpcode = 0x440011, DbQueryBulkOpcode = 0x440010;
    private const byte StatusValid = 1, StatusInvalid = 3;
    private readonly Dictionary<int, Record[]> pushes;
    private readonly Dictionary<(uint table, int record), Record> records;
    internal static ForeverHotfixStore Current = new(Array.Empty<Record>());

    public ForeverHotfixStore(IEnumerable<Record> all)
    {
        var list = all.ToArray();
        pushes = list.GroupBy(r => r.Push).ToDictionary(g => g.Key, g => g.ToArray());
        records = list.Where(r => r.Status == StatusValid).GroupBy(r => (r.Table, r.RecordId)).ToDictionary(g => g.Key, g => g.Last());
    }

    public int Count => pushes.Count;
    public int OverlayPushes { get { lock (overlayGate) return overlay.Count; } }
    // Runtime pushes: replaced as a whole, offered with the loaded ones, one HOTFIX_CONNECT per push.
    private Dictionary<int, Record[]> overlay = new();
    private readonly object overlayGate = new();
    public void SetOverlay(IEnumerable<Record> records)
    {
        var grouped = records.GroupBy(r => r.Push).ToDictionary(g => g.Key, g => g.ToArray());
        lock (overlayGate) overlay = grouped;
    }
    public const uint TactKeyTable = 0xDF2F53CF;
    // A valid record's data (e.g. a TactKey row: the 16-byte key), or null.
    public byte[] RecordData(uint table, int id) => records.TryGetValue((table, id), out var record) ? record.Data : null;

    public static ForeverHotfixStore Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 8 });
        var list = new List<Record>();
        foreach (var item in document.RootElement.GetProperty("records").EnumerateArray())
        {
            var data = Convert.FromHexString(item.GetProperty("data").GetString());
            if (data.Length > 65536 || list.Count >= 100000) throw new InvalidDataException("Hotfix data exceeds limits.");
            list.Add(new Record(item.GetProperty("push").GetInt32(), item.GetProperty("unique").GetUInt32(), item.GetProperty("table").GetUInt32(),
                item.GetProperty("record").GetInt32(), item.GetProperty("status").GetByte(), data));
        }
        return new ForeverHotfixStore(list);
    }

    // int32 realm, count, then (int32 push, uint32 unique) for the first record of each push.
    public byte[] AvailableHotfixes(uint realmAddress)
    {
        Dictionary<int, Record[]> extra; lock (overlayGate) extra = overlay;
        var all = pushes.Values.Concat(extra.Values).OrderBy(p => p[0].Push).ToArray();
        var writer = new ForeverPacketWriter().U32(realmAddress).U32((uint)all.Length);
        foreach (var push in all) writer.I32(push[0].Push).U32(push[0].Unique);
        return writer.ToArray();
    }

    // CMSG_HOTFIX_REQUEST: uint32 client build, uint32 data build, uint32 count, int32 push IDs.
    public static int[] ReadHotfixRequest(byte[] payload)
    {
        var reader = new ForeverPacketReader(payload); reader.U32(); reader.U32();
        var count = reader.U32();
        if (count > 200000 || count * 4 != reader.Remaining) throw new FormatException("Invalid hotfix request count.");
        return Enumerable.Range(0, (int)count).Select(_ => reader.I32()).ToArray();
    }

    // Loaded pushes in one packet, each runtime push in its own (they can be large).
    public IEnumerable<byte[]> HotfixConnectPackets(IEnumerable<int> requested)
    {
        var ids = requested.Distinct().ToArray();
        Dictionary<int, Record[]> extra; lock (overlayGate) extra = overlay;
        yield return HotfixConnect(ids.Where(pushes.ContainsKey).SelectMany(id => pushes[id]));
        foreach (var id in ids.Where(extra.ContainsKey)) yield return HotfixConnect(extra[id]);
    }

    public byte[] HotfixConnect(IEnumerable<int> requested) => HotfixConnect(requested.Distinct().Where(pushes.ContainsKey).SelectMany(id => pushes[id]));

    // Records: (int32 push, uint32 unique, uint32 table, int32 record, uint32 size, 3-bit status); then the content blob.
    private static byte[] HotfixConnect(IEnumerable<Record> records)
    {
        var selected = records.ToArray();
        var writer = new ForeverPacketWriter().U32((uint)selected.Length);
        using var content = new MemoryStream();
        foreach (var record in selected)
        {
            var size = record.Status == StatusValid ? record.Data.Length : 0;
            writer.I32(record.Push).U32(record.Unique).U32(record.Table).I32(record.RecordId).U32((uint)size).Bits(record.Status, 3).Flush();
            if (size > 0) content.Write(record.Data);
        }
        return writer.U32((uint)content.Length).Raw(content.ToArray()).ToArray();
    }

    // CMSG_DB_QUERY_BULK: uint32 table, 13-bit count, uint32 record IDs. One SMSG_DB_REPLY per record.
    public static (uint table, uint[] ids) ReadDbQueryBulk(byte[] payload)
    {
        var reader = new ForeverPacketReader(payload); var table = reader.U32(); var count = (int)reader.Bits(13);
        if (count * 4 != reader.ResetBits().Remaining) throw new FormatException("Invalid DB query count.");
        return (table, Enumerable.Range(0, count).Select(_ => reader.U32()).ToArray());
    }

    public byte[] DbReply(uint table, uint id, uint timestamp)
    {
        Dictionary<int, Record[]> extra; lock (overlayGate) extra = overlay;
        var record = extra.Values.SelectMany(p => p).LastOrDefault(r => r.Table == table && r.RecordId == (int)id);
        var found = record != null || records.TryGetValue((table, (int)id), out record);
        var data = found ? record.Data : Array.Empty<byte>();
        return new ForeverPacketWriter().U32(table).U32(id).U32(timestamp).Bits(found ? StatusValid : StatusInvalid, 3).U32((uint)data.Length).Raw(data).ToArray();
    }
}

// Account-wide UI data (bindings, settings) kept per type; optionally persisted.
internal sealed class ForeverAccountData
{
    public const uint AccountDataTimesOpcode = 0x4701B6, UpdateAccountDataOpcode = 0x4701B4, UpdateCompleteOpcode = 0x4701B5;
    public const uint RequestOpcode = 0x4400C3, UpdateOpcode = 0x4400C4;
    public const int TypeCount = 20, MaxBytes = 1 << 20;   // 70338: 20 timestamps (reference ClassicOpcodes note)
    // Account-wide and per-character types (pinned WorldSession.h GLOBAL_CACHE_MASK / PER_CHARACTER_CACHE_MASK).
    public const uint GlobalMask = 0x000BA515, PerCharacterMask = 0x00045AEA;
    private sealed class Entry { public long Time { get; set; } public uint Size { get; set; } public string Data { get; set; } = ""; }
    private readonly object gate = new();
    private readonly string path;
    // Keys: "<type>" for account-wide data, "c<character>:<type>" for a character's own; a per-character type stored without a
    // character (older stores shared them) is still offered to every character until it saves its own.
    private readonly Dictionary<string, Entry> entries;
    internal static ForeverAccountData Current = new(null);

    public ForeverAccountData(string file)
    {
        path = file;
        entries = file != null && File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new() : new();
        if (entries.Keys.Any(key => !ValidKey(key))) throw new InvalidDataException("Account data store is invalid.");
    }

    public static bool IsPerCharacter(int type) => (PerCharacterMask >> type & 1) != 0;
    private static string Key(ulong character, int type) => character != 0 && IsPerCharacter(type) ? $"c{character}:{type}" : type.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static bool ValidKey(string key)
    {
        var parts = key.Split(':');
        return parts.Length == 1 ? int.TryParse(key, out var type) && type is >= 0 and < TypeCount
            : parts.Length == 2 && parts[0].Length > 1 && parts[0][0] == 'c' && ulong.TryParse(parts[0][1..], out _) && int.TryParse(parts[1], out type) && type is >= 0 and < TypeCount && IsPerCharacter(type);
    }
    private Entry Find(ulong character, int type) =>
        entries.TryGetValue(Key(character, type), out var entry) ? entry : character != 0 && IsPerCharacter(type) && entries.TryGetValue(Key(0, type), out entry) ? entry : null;
    private static ulong Character(byte[] guid) => guid == null || guid.All(b => b == 0) ? 0 : ForeverCharacterPackets.Counter(guid);

    // Empty GUID: account-wide times (glue); the player GUID: the character's times, sent at login (pinned CharacterHandler.cpp).
    public byte[] AccountDataTimes(long now, byte[] playerGuid = null)
    {
        lock (gate)
        {
            var character = Character(playerGuid); var mask = character == 0 ? GlobalMask : PerCharacterMask;
            var writer = new ForeverPacketWriter().Guid(playerGuid ?? new byte[16]).I64(now);
            for (var type = 0; type < TypeCount; type++) writer.I64((mask >> type & 1) != 0 && Find(character, type) is { } entry ? entry.Time : 0);
            return writer.ToArray();
        }
    }

    // A character's (or account-wide) stored text, and a server-side replacement: the newer time makes the client download it.
    public string Get(ulong character, int type) { lock (gate) return Find(character, type)?.Data; }
    public void Set(ulong character, int type, string text, long now)
    {
        lock (gate)
        {
            entries[Key(character, type)] = new Entry { Time = now, Size = (uint)System.Text.Encoding.UTF8.GetByteCount(text), Data = text };
            SaveLocked();
        }
    }

    // Seeds a character's data (e.g. default macros) when it has none; the newer server time makes the client download it.
    public bool SeedIfMissing(ulong character, int type, string text, long now)
    {
        lock (gate)
        {
            if (Find(character, type) != null) return false;
            entries[Key(character, type)] = new Entry { Time = now, Size = (uint)System.Text.Encoding.UTF8.GetByteCount(text), Data = text };
            SaveLocked();
            return true;
        }
    }

    // CMSG_REQUEST_ACCOUNT_DATA: packed player GUID, int32 type -> SMSG_UPDATE_ACCOUNT_DATA with zlib data.
    public byte[] Request(byte[] payload)
    {
        var reader = new ForeverPacketReader(payload); var guid = reader.PackedGuid(); var type = reader.I32();
        if (type is < 0 or >= TypeCount) throw new FormatException("Invalid account data type.");
        lock (gate)
        {
            var entry = Find(Character(guid), type);
            var text = entry?.Data ?? "";
            var compressed = Compress(System.Text.Encoding.UTF8.GetBytes(text));
            return new ForeverPacketWriter().I64(entry?.Time ?? 0).U32(entry?.Size ?? 0).Guid(guid).I32(type).U32((uint)compressed.Length).Raw(compressed).ToArray();
        }
    }

    // CMSG_UPDATE_ACCOUNT_DATA: int64 time, uint32 size, packed GUID, int32 type, uint32 length, zlib data -> complete (result 0).
    public byte[] Update(byte[] payload)
    {
        var reader = new ForeverPacketReader(payload);
        var time = (long)(reader.U32() | (ulong)reader.U32() << 32); var size = reader.U32(); var guid = reader.PackedGuid(); var type = reader.I32();
        var length = reader.U32();
        if (type is < 0 or >= TypeCount || size > MaxBytes || length > MaxBytes || length != reader.Remaining) throw new FormatException("Invalid account data update.");
        var text = size == 0 ? "" : System.Text.Encoding.UTF8.GetString(Decompress(reader.Bytes((int)length), (int)size));
        lock (gate)
        {
            entries[Key(Character(guid), type)] = new Entry { Time = size == 0 ? 0 : time, Size = size, Data = text };
            SaveLocked();
        }
        return new ForeverPacketWriter().Guid(guid).I32(type).I32(0).ToArray();
    }

    private void SaveLocked()
    {
        if (path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries));
        File.Move(path + ".tmp", path, true);
    }

    private static byte[] Compress(byte[] data) { using var output = new MemoryStream(); using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, true)) zlib.Write(data); return output.ToArray(); }
    private static byte[] Decompress(byte[] data, int size)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress); var result = new byte[size]; var read = 0;
        while (read < size) { var count = input.Read(result, read, size - read); if (count == 0) throw new FormatException("Truncated account data."); read += count; }
        return result;
    }
}
