// Decoder: recorded world connections + realm-join key data -> game object placements.
// Wire facts (same as the sandbox server writes, ForeverWorldWire / ForeverWorldCrypto / ForeverGameObjects): a connection is one
// text line, then frames of uint32 size, 12-byte tag, body (uint32 opcode + payload). Frames are AES-256-GCM once the server has
// sent EnterEncryptedMode; the IV is the frame's index on that connection plus a direction word. Large packets travel inside a
// "compressed packet": uint32 size, uint32 Adler of the plain bytes, uint32 Adler of the packed bytes, then raw deflate from one
// stream per connection (pinned reference WorldSocket.cpp). Nothing here needs opcode numbers, so a new client build that only
// renumbers its messages still decodes.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using AuthServer.Network;

namespace ForeverCapture;

// One recorded world connection: what the game sent and what the server sent.
internal sealed record WorldConnection(string Server, byte[] ClientBytes, byte[] ServerBytes, bool Complete);

internal sealed record PlacedObject(string Guid, int Map, int Entry, int Display, int Type, int State, uint Flags,
    float X, float Y, float Z, float Facing, float[] Rotation, float[] ParentRotation, float Scale, uint ArtKit, bool LayoutExact);

internal sealed class DecodeStats
{
    public int Connections, Decrypted, NoKey, Truncated, Frames, CompressedFrames, CompressionErrors;
    public int Sightings, PlayerMade, RejectedFlags, RejectedPosition, RejectedValues, LayoutInexact;
    public int TrackPoints, TrackWithoutMap;
    public SortedDictionary<string, int> Opcodes = new();
    public List<string> ConnectionLog = new();
}

// A stretch of the character's own path on one map (x, y, z per point).
internal sealed record TrackSegment(int Map, List<float[]> Points);

internal static class WorldDecoder
{
    private const uint ServerMagic = 0x52565253, ClientMagic = 0x544E4C43, AdlerSeed = 0x9827D8F1;
    // The two numbers the path needs (objects need none): an update packet starts with the map it is for, and most of
    // them travel in a bundle. Both are unchanged from the 70338 beta to the first live capture (70938).
    private const uint UpdateObjectOpcode = 0x5E0000, MultiplePacketsOpcode = 0x4E000D;
    private const int MaxFrameBytes = 1 << 24;
    // Yards between kept path points, and the jump that starts a new stretch (a teleport on the same connection).
    private const float TrackStep = 3, TrackJump = 200;

    public static (List<PlacedObject> Objects, List<TrackSegment> Track, DecodeStats Stats) Decode(IEnumerable<WorldConnection> connections, IReadOnlyList<byte[]> joinKeyData)
    {
        var stats = new DecodeStats();
        var objects = new Dictionary<string, PlacedObject>();
        var track = new List<TrackSegment>();
        var sessionKeys = new List<byte[]>();
        foreach (var connection in connections)
        {
            stats.Connections++;
            if (!connection.Complete) stats.Truncated++;
            var frames = Frames(connection.ServerBytes);
            var key = FindKey(connection, frames, joinKeyData, sessionKeys, out var firstSealed, out var counterShift);
            var hello = FirstClientFrame(connection.ClientBytes);
            stats.ConnectionLog.Add($"{connection.Server}: game opened with {(hello == null ? "nothing readable" : $"0x{hello.Value.Opcode:X6} ({hello.Value.Payload.Length} bytes)")}, "
                + $"{frames.Count} server packets, first sealed #{firstSealed}, {connection.ServerBytes.Length} bytes, {(key == null ? "NO KEY" : "decrypted")}");
            if (key == null) { stats.NoKey++; continue; }
            stats.Decrypted++;
            using var aes = new AesGcm(key, 12);
            using var inflater = new PacketInflater();
            var inflating = true;
            var map = -1;
            for (var i = firstSealed; i < frames.Count; i++)
            {
                var (tag, body) = frames[i];
                var plain = new byte[body.Count];
                try { aes.Decrypt(Iv((ulong)(i + counterShift)), body, tag, plain); }
                catch (CryptographicException) { stats.Truncated++; break; }   // bytes lost in the recording: nothing after this can be read
                stats.Frames++;
                var payload = plain.AsSpan(4);
                var opcode = BinaryPrimitives.ReadUInt32LittleEndian(plain);
                if (inflating && payload.Length > 12 && Adler(AdlerSeed, payload[12..]) == BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]))
                {
                    stats.CompressedFrames++;
                    var inner = inflater.Inflate(payload[12..].ToArray(), BinaryPrimitives.ReadInt32LittleEndian(payload));
                    if (inner == null || inner.Length < 4 || Adler(AdlerSeed, inner) != BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]))
                    { stats.CompressionErrors++; inflating = false; continue; }   // the shared deflate stream is out of step from here on
                    opcode = BinaryPrimitives.ReadUInt32LittleEndian(inner);
                    payload = inner.AsSpan(4);
                }
                var name = $"0x{opcode:X6}";
                stats.Opcodes[name] = stats.Opcodes.GetValueOrDefault(name) + 1;
                GameObjectScanner.Scan(payload, objects, stats);
                // The map this connection plays, from its update packets: alone, or inside a bundle (uint16 payload
                // size, uint32 opcode, payload, repeated).
                if (opcode == UpdateObjectOpcode && payload.Length >= 2) map = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                else if (opcode == MultiplePacketsOpcode)
                    for (var at = 0; at + 6 <= payload.Length;)
                    {
                        var size = BinaryPrimitives.ReadUInt16LittleEndian(payload[at..]);
                        if (at + 6 + size > payload.Length) break;
                        if (size >= 2 && BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 2)..]) == UpdateObjectOpcode) map = BinaryPrimitives.ReadUInt16LittleEndian(payload[(at + 6)..]);
                        at += 6 + size;
                    }
            }
            // shortcut: one map per connection (the server opens a new one on a map change); split by the update packets'
            // order if a capture ever shows two maps on one connection.
            foreach (var stretch in CharacterPath(connection.ClientBytes, aes))
            {
                stats.TrackPoints += stretch.Count;
                if (map >= 0) track.Add(new TrackSegment(map, stretch)); else stats.TrackWithoutMap++;
            }
        }
        return (objects.Values.OrderBy(o => o.Map).ThenBy(o => o.Entry).ThenBy(o => o.Guid, StringComparer.Ordinal).ToList(), track, stats);
    }

    // The character's path, from the game's own movement packets (opcode group 0x42): the player's packed GUID, uint64
    // flags, uint32 time, then the position, as ForeverWorldPlayer.TrackMovement reads them. The game's frames count
    // from the start of the connection like the server's, with their own direction word.
    private static List<List<float[]>> CharacterPath(byte[] clientStream, AesGcm aes)
    {
        var stretches = new List<List<float[]>>();
        var frames = Frames(clientStream);
        for (var i = 0; i < frames.Count; i++)
        {
            var (tag, body) = frames[i];
            if (!tag.Any(b => b != 0)) continue;   // sent before encryption started
            var plain = new byte[body.Count];
            try { aes.Decrypt(Iv((ulong)i, ClientMagic), body, tag, plain); }
            catch (CryptographicException) { break; }
            if (BinaryPrimitives.ReadUInt32LittleEndian(plain) >> 16 != 0x42) continue;
            var at = PlayerPosition(plain.AsSpan(4));
            if (at == null) continue;
            var last = stretches.Count > 0 ? stretches[^1][^1] : null;
            var moved = last == null ? float.MaxValue : MathF.Sqrt((at[0] - last[0]) * (at[0] - last[0]) + (at[1] - last[1]) * (at[1] - last[1]));
            if (moved > TrackJump) stretches.Add(new List<float[]> { at });
            else if (moved >= TrackStep) stretches[^1].Add(at);
        }
        return stretches;
    }

    private static float[] PlayerPosition(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2) return null;
        // Packed GUID: two mask bytes, then only the non-zero bytes. A player's GUID has high type 2 in its top byte.
        int mask = packet[0] | packet[1] << 8, q = 2, top = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((mask & 1 << i) == 0) continue;
            if (q >= packet.Length) return null;
            if (i == 15) top = packet[q];
            q++;
        }
        if (top >> 2 != 2 || q + 24 > packet.Length) return null;
        float x = BinaryPrimitives.ReadSingleLittleEndian(packet[(q + 12)..]), y = BinaryPrimitives.ReadSingleLittleEndian(packet[(q + 16)..]),
            z = BinaryPrimitives.ReadSingleLittleEndian(packet[(q + 20)..]);
        return MathF.Abs(x) < 20000 && MathF.Abs(y) < 20000 && MathF.Abs(z) < 10000 ? new[] { x, y, z } : null;
    }

    // Frames of a server stream; stops quietly at the first incomplete or impossible frame.
    private static List<(byte[] Tag, ArraySegment<byte> Body)> Frames(byte[] stream)
    {
        var frames = new List<(byte[], ArraySegment<byte>)>();
        var position = Array.IndexOf(stream, (byte)'\n') + 1;
        if (position == 0) return frames;
        while (position + 16 <= stream.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(position));
            if (size < 4 || size > MaxFrameBytes || position + 16 + size > stream.Length) break;
            frames.Add((stream[(position + 4)..(position + 16)], new ArraySegment<byte>(stream, position + 16, size)));
            position += 16 + size;
        }
        return frames;
    }

    // The connection's AES key: tried against the first sealed frame, whose tag proves the right one. A realm connection derives it
    // from a realm join (and yields the session key); an instance connection derives it from a session key already found.
    private static byte[] FindKey(WorldConnection connection, List<(byte[] Tag, ArraySegment<byte> Body)> frames, IReadOnlyList<byte[]> joinKeyData,
        List<byte[]> sessionKeys, out int firstSealed, out int counterShift)
    {
        counterShift = 0;
        firstSealed = frames.FindIndex(f => f.Tag.Any(b => b != 0));
        if (firstSealed < 1 || frames[0].Body.Count < 4 + 64) return null;
        var serverChallenge = frames[0].Body.AsSpan(4 + 32, 32).ToArray();   // auth challenge: 32 anti-flood bytes, then the challenge
        var auth = FirstClientFrame(connection.ClientBytes)?.Payload;
        if (auth == null) return null;
        var candidates = new List<(byte[] Key, byte[] Session)>();
        if (auth.Length >= 52)   // auth session: uint64, three uint32, 32-byte local challenge
        {
            var local = auth[20..52];
            foreach (var keyData in joinKeyData.Reverse())
            {
                var session = ForeverWorldKeys.SessionKey(keyData, serverChallenge, local);
                candidates.Add((ForeverWorldKeys.EncryptKey(session, local, serverChallenge), session));
            }
        }
        if (auth.Length >= 40)   // continued session: uint64, 32-byte local challenge
        {
            var local = auth[8..40];
            foreach (var session in Enumerable.Reverse(sessionKeys)) candidates.Add((ForeverWorldKeys.EncryptKey(session, local, serverChallenge), null));
        }
        var (tag, body) = frames[firstSealed];
        var scratch = new byte[body.Count];
        foreach (var (key, session) in candidates)
        {
            using var aes = new AesGcm(key, 12);
            // Every frame counts from the start of the connection; the other starting points are tried once in case a server does not.
            foreach (var counter in new[] { firstSealed }.Concat(Enumerable.Range(0, firstSealed)))
            {
                try { aes.Decrypt(Iv((ulong)counter), body, tag, scratch); }
                catch (CryptographicException) { continue; }
                counterShift = counter - firstSealed;
                if (session != null) sessionKeys.Add(session);
                return key;
            }
        }
        return null;
    }

    // The game's first packet on a connection (sent before encryption starts): its opcode and payload.
    private static (uint Opcode, byte[] Payload)? FirstClientFrame(byte[] stream)
    {
        var position = Array.IndexOf(stream, (byte)'\n') + 1;
        if (position == 0 || position + 20 > stream.Length) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(position));
        if (size < 4 || size > 65536 || position + 16 + size > stream.Length) return null;
        return (BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(position + 16)), stream[(position + 20)..(position + 16 + size)]);
    }

    private static byte[] Iv(ulong counter, uint direction = ServerMagic)
    {
        var iv = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(iv, counter);
        BinaryPrimitives.WriteUInt32LittleEndian(iv.AsSpan(8), direction);
        return iv;
    }

    internal static uint Adler(uint seed, ReadOnlySpan<byte> data)
    {
        uint a = seed & 0xFFFF, b = seed >> 16;
        foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return b << 16 | a;
    }

    // One raw-deflate stream per connection: each compressed packet continues where the last one stopped (ends on a sync flush).
    private sealed class PacketInflater : IDisposable
    {
        private readonly Feed feed = new();
        private readonly DeflateStream deflate;
        public PacketInflater() { deflate = new DeflateStream(feed, CompressionMode.Decompress); }
        public byte[] Inflate(byte[] packed, int size)
        {
            if (size < 4 || size > MaxFrameBytes) return null;
            feed.Next(packed);
            var plain = new byte[size];
            try
            {
                for (var done = 0; done < size;)
                {
                    var read = deflate.Read(plain, done, size - done);
                    if (read == 0) return null;
                    done += read;
                }
            }
            catch (InvalidDataException) { return null; }
            return plain;
        }
        public void Dispose() => deflate.Dispose();

        private sealed class Feed : Stream
        {
            private byte[] current = Array.Empty<byte>();
            private int position;
            public void Next(byte[] data) { current = data; position = 0; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var take = Math.Min(count, current.Length - position);
                Buffer.BlockCopy(current, position, buffer, offset, take);
                position += take;
                return take;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

// Finds game object create blocks anywhere in a packet's bytes, so it needs neither the update packet's opcode nor the layouts
// of the creatures and players that travel in the same packet. Block layout (pinned BaseEntity.cpp BuildCreateUpdateBlockForPlayer /
// BuildMovementUpdate, ObjectGuid.cpp, generated ObjectData / GameObjectData): update type 1 or 2, packed GUID with high type 11,
// object type, 21 flag bits, pause-time count, position, optional parts, packed rotation, pause times, then size-prefixed values.
// shortcut: a pattern scan, proven by the entry in the GUID matching the entry in the values. Objects riding a transport (no
// stationary position) are skipped; read whole update packets instead if those are ever wanted.
internal static class GameObjectScanner
{
    public static void Scan(ReadOnlySpan<byte> data, Dictionary<string, PlacedObject> into, DecodeStats stats)
    {
        for (var p = 0; p + 40 < data.Length; p++)
        {
            if (data[p] is not (1 or 2) || (data[p + 2] & 0x80) == 0) continue;
            var end = TryRead(data, p + 1, stats, out var found);
            if (end < 0) continue;
            if (found != null) { stats.Sightings++; into[found.Guid] = found; if (!found.LayoutExact) stats.LayoutInexact++; }
            p = end - 1;
        }
    }

    // Returns the offset after the block, or -1 when the bytes at `q` are not a game object create block.
    private static int TryRead(ReadOnlySpan<byte> d, int q, DecodeStats stats, out PlacedObject found)
    {
        found = null;
        Span<byte> guid = stackalloc byte[16];
        if (!PackedGuid(d, ref q, guid)) return -1;
        var low = BinaryPrimitives.ReadUInt64LittleEndian(guid); var high = BinaryPrimitives.ReadUInt64LittleEndian(guid[8..]);
        if (high >> 58 != 11 || q + 8 > d.Length) return -1;
        int objectType = d[q], f0 = d[q + 1], f1 = d[q + 2], f2 = d[q + 3];
        q += 4;
        // Needs stationary + rotation; no movement update, transport position, combat victim, vehicle, scene object or later kinds.
        if ((f0 & 0x07) != 0x01 || (f1 & 0xA9) != 0x08 || f2 != 0) { if (objectType == 8) stats.RejectedFlags++; return -1; }
        var pauseTimes = BinaryPrimitives.ReadUInt32LittleEndian(d[q..]); q += 4;
        if (pauseTimes > 64 || q + 16 > d.Length) return -1;
        float x = F(d, q), y = F(d, q + 4), z = F(d, q + 8), facing = F(d, q + 12);
        q += 16;
        if (!(MathF.Abs(x) < 40000 && MathF.Abs(y) < 40000 && MathF.Abs(z) < 20000 && MathF.Abs(facing) < 16)) { stats.RejectedPosition++; return -1; }
        if ((f1 & 0x40) != 0) q += 4;                    // server time
        if ((f1 & 0x10) != 0) q += 6;                    // anim kits
        if (q + 8 > d.Length) return -1;
        var rotation = Unpack(BinaryPrimitives.ReadUInt64LittleEndian(d[q..])); q += 8;
        if ((f1 & 0x04) != 0)                            // game object part: world effect, three bits, transport times, path progress
        {
            if (q + 5 > d.Length) return -1;
            var bits = d[q + 4]; q += 5;
            if ((bits & 0x40) != 0) q += 9;
            if ((bits & 0x80) != 0) q += 4;
            if ((bits & 0x20) != 0) q += 4;
        }
        if ((f1 & 0x02) != 0)                            // smooth phasing: three bits, optional replaced object
        {
            if (q + 1 > d.Length) return -1;
            var bits = d[q++];
            if ((bits & 0x20) != 0 && !PackedGuid(d, ref q, stackalloc byte[16], allowEmpty: true)) return -1;
        }
        q += (int)pauseTimes * 4;
        if (q + 4 > d.Length) return -1;
        var size = BinaryPrimitives.ReadInt32LittleEndian(d[q..]); q += 4;
        if (size < 24 || size > 8192 || q + size > d.Length) return -1;
        var values = d.Slice(q, size);
        var entry = (int)(high >> 6 & 0x7FFFFF);
        // Values: flag byte, fragment list, then ObjectData (entry, dynamic flags, scale) and GameObjectData.
        var at = -1;
        for (var o = 1; o < 40 && o + 12 <= values.Length; o++)
            if (BinaryPrimitives.ReadInt32LittleEndian(values[o..]) == entry && F(values, o + 8) is > 0.001f and <= 100f) { at = o; break; }
        if (at < 0) { stats.RejectedValues++; return -1; }
        var block = q + size;
        var fields = ReadFields(values[(at + 12)..]);
        if (fields == null) { stats.RejectedValues++; return -1; }
        var f = fields.Value;
        if (f.PlayerMade) { stats.PlayerMade++; return block; }   // campfires, traps, fishing bobbers: not part of the world
        found = new PlacedObject($"{high:X16}{low:X16}", (int)(high >> 29 & 0x1FFF), entry, f.Display, f.Type, f.State, f.Flags,
            x, y, z, facing, rotation, f.ParentRotation, F(values, at + 8), f.ArtKit, f.Exact);
        return block;
    }

    private readonly record struct Fields(int Display, uint Flags, float[] ParentRotation, int State, int Type, uint ArtKit, bool PlayerMade, bool Exact);

    // GameObjectData at create time, in the order the generated ForeverUpdateFields.GameObjectData writes it.
    private static Fields? ReadFields(ReadOnlySpan<byte> v)
    {
        var q = 0;
        if (v.Length < 28) return null;
        var display = BinaryPrimitives.ReadInt32LittleEndian(v);
        var stateEffects = BinaryPrimitives.ReadUInt32LittleEndian(v[20..]);
        if (stateEffects > 32) return null;
        q = 28 + (int)stateEffects * 4;
        Span<byte> createdBy = stackalloc byte[16];
        if (!PackedGuid(v, ref q, createdBy, allowEmpty: true) || !PackedGuid(v, ref q, stackalloc byte[16], allowEmpty: true)) return null;
        if (q + 71 > v.Length) return null;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(v[q..]);
        var parent = new[] { F(v, q + 8), F(v, q + 12), F(v, q + 16), F(v, q + 20) };
        int state = (sbyte)v[q + 32], type = (sbyte)v[q + 33];
        var artKit = BinaryPrimitives.ReadUInt32LittleEndian(v[(q + 35)..]);
        var doodadSets = BinaryPrimitives.ReadUInt32LittleEndian(v[(q + 39)..]);
        var worldEffects = BinaryPrimitives.ReadUInt32LittleEndian(v[(q + 67)..]);
        // flags, flags B, parent rotation, faction, level, state, type, health, art kit, doodad set count, custom parameter, level,
        // animation group, three widget fields, world effect count; then the two lists and one bit.
        var end = q + 71 + (long)doodadSets * 4 + (long)worldEffects * 4 + 1;
        var playerMade = createdBy.IndexOfAnyExcept((byte)0) >= 0;
        return new Fields(display, flags, parent, state, type, artKit, playerMade, end == v.Length && v[^1] == 0);
    }

    // Two mask bytes, then only the non-zero bytes of the 16.
    private static bool PackedGuid(ReadOnlySpan<byte> d, ref int q, Span<byte> guid, bool allowEmpty = false)
    {
        if (q + 2 > d.Length) return false;
        var mask = d[q] | d[q + 1] << 8;
        q += 2;
        if (mask == 0) return allowEmpty;
        guid.Clear();
        for (var i = 0; i < 16; i++)
        {
            if ((mask & 1 << i) == 0) continue;
            if (q >= d.Length || d[q] == 0) return false;
            guid[i] = d[q++];
        }
        return true;
    }

    // GameObject::UpdatePackedRotation backwards: x in 22 bits, y and z in 21 bits, w is the positive rest.
    private static float[] Unpack(ulong packed)
    {
        float x = (float)((long)packed >> 42) / (1 << 21);
        float y = (float)((long)(packed << 22) >> 43) / (1 << 20);
        float z = (float)((long)(packed << 43) >> 43) / (1 << 20);
        return new[] { x, y, z, MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y - z * z)) };
    }

    private static float F(ReadOnlySpan<byte> d, int q) => BinaryPrimitives.ReadSingleLittleEndian(d[q..]);
}
