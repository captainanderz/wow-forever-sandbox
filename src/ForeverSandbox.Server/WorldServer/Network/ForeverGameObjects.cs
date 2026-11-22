// Server-side game objects for the 70888 client (no database): create blocks and removals in SMSG_UPDATE_OBJECT.
// Layouts: pinned BaseEntity.cpp (BuildCreateUpdateBlockForPlayer, BuildMovementUpdate: a GameObject has the WorldObject entity
// position plus Stationary and Rotation), UpdateData.cpp (out-of-range list), ObjectGuid.cpp CreateWorldObject (HighGuid
// GameObject 11), GameObject::UpdatePackedRotation, and the generated ObjectData / GameObjectData writers.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuthServer.Network;

internal static class ForeverGameObjects
{
    public const byte TypeGeneric = 5;                 // GAMEOBJECT_TYPE_GENERIC
    public const uint FlagNotSelectable = 0x10;        // GO_FLAG_NOT_SELECTABLE
    private const int MaxBlocksPerPacket = 100;

    public readonly record struct Spawn(byte[] Guid, uint Entry, int Display, float X, float Y, float Z, float Facing,
        (float X, float Y, float Z, float W) Rotation, float Scale, uint Flags = FlagNotSelectable, byte Type = TypeGeneric,
        (float X, float Y, float Z, float W)? ParentRotation = null);

    // ObjectGuidFactory::CreateWorldObject(GameObject, subType 0, realm 70, map, server 0, entry, counter).
    public static byte[] Guid(int map, uint entry, ulong counter)
    {
        var guid = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(guid, counter & 0xFFFFFFFFFF);
        BinaryPrimitives.WriteUInt64LittleEndian(guid.AsSpan(8), (11UL << 58) | (70UL << 42) | ((ulong)(map & 0x1FFF) << 29) | ((ulong)(entry & 0x7FFFFF) << 6));
        return guid;
    }

    // Quaternion for a rotation of `yaw` about Z, then `pitch` about the object's Y and `roll` about its X (G3D fromEulerAnglesZYX).
    public static (float X, float Y, float Z, float W) Rotation(float yaw, float pitch = 0, float roll = 0)
    {
        float cz = MathF.Cos(yaw / 2), sz = MathF.Sin(yaw / 2), cy = MathF.Cos(pitch / 2), sy = MathF.Sin(pitch / 2), cx = MathF.Cos(roll / 2), sx = MathF.Sin(roll / 2);
        return (cz * cy * sx - sz * sy * cx, cz * sy * cx + sz * cy * sx, sz * cy * cx - cz * sy * sx, cz * cy * cx + sz * sy * sx);
    }

    // GameObject::UpdatePackedRotation: x in 22 bits, y and z in 21 bits, signs flipped so w >= 0.
    public static ulong PackRotation((float X, float Y, float Z, float W) q)
    {
        const int packYZ = 1 << 20, packX = packYZ << 1;
        const long maskYZ = (packYZ << 1) - 1, maskX = (packX << 1) - 1;
        var sign = q.W >= 0 ? 1 : -1;
        long x = (long)((int)(q.X * packX) * sign) & maskX, y = (long)((int)(q.Y * packYZ) * sign) & maskYZ, z = (long)((int)(q.Z * packYZ) * sign) & maskYZ;
        return (ulong)(z | (y << 21) | (x << 42));
    }

    // One CREATE_OBJECT2 block (BaseEntity::BuildCreateUpdateBlockForPlayer) for a viewer that does not own the object.
    public static byte[] CreateBlock(Spawn s)
    {
        var block = new ForeverPacketWriter().U8(2).Guid(s.Guid).U8(8);              // CREATE_OBJECT2, TYPEID_GAMEOBJECT
        // HasEntityPosition .. MeshObject (21 bits): entity position (0), stationary (7), rotation (12).
        for (var i = 0; i < 21; i++) block.Bit(i is 0 or 7 or 12);
        block.Flush().U32(0)                                                          // pause times
            .F32(s.X).F32(s.Y).F32(s.Z).F32(s.Facing)                                 // stationary position
            .U64(PackRotation(s.Rotation));
        var parent = s.ParentRotation ?? (0f, 0f, 0f, 1f);
        var v = new UfValues { Flags = 0 }
            .Set("Object.EntryID", s.Entry).Set("Object.Scale", s.Scale)
            .Set("GameObject.DisplayID", s.Display).Set("GameObject.Flags", s.Flags)
            .Set("GameObject.ParentRotation.x", parent.X).Set("GameObject.ParentRotation.y", parent.Y).Set("GameObject.ParentRotation.z", parent.Z).Set("GameObject.ParentRotation.w", parent.W)
            .Set("GameObject.State", 1).Set("GameObject.TypeID", s.Type).Set("GameObject.PercentHealth", 255);   // GO_STATE_READY
        var values = new ForeverPacketWriter().U8(0)                                  // no update-field flags (not the owner)
            .U8(3).U8(206).U8(255)                                                    // fragments: CGObject (retail 2 + 1), Tag_GameObject, End
            .U8(1);                                                                   // CGObject is indirect: active
        ForeverUpdateFields.ObjectData(values, v, "Object");
        ForeverUpdateFields.GameObjectData(values, v, "GameObject");
        var valueBytes = values.ToArray();
        return block.U32((uint)valueBytes.Length).Raw(valueBytes).ToArray();
    }

    // SMSG_UPDATE_OBJECT packets (UpdateData::BuildPacket): uint16 map, uint32 blocks, bit 1, bit removals
    // [uint16 destroyed, uint32 destroyed + out of range, packed GUIDs], uint32 size, blocks. Removals go out of range.
    public static IEnumerable<byte[]> Packets(int map, IReadOnlyList<byte[]> createBlocks, IReadOnlyList<byte[]> outOfRange)
    {
        var first = true;
        for (var start = 0; first || start < createBlocks.Count; start += MaxBlocksPerPacket, first = false)
        {
            var blocks = createBlocks.Skip(start).Take(MaxBlocksPerPacket).ToList();
            var removals = first ? outOfRange : Array.Empty<byte[]>();
            if (blocks.Count == 0 && removals.Count == 0) yield break;
            var w = new ForeverPacketWriter().U16((ushort)map).U32((uint)blocks.Count).Bit(true).Bit(removals.Count > 0);
            if (removals.Count > 0)
            {
                w.U16(0).U32((uint)removals.Count);
                foreach (var guid in removals) w.Guid(guid);
            }
            var data = blocks.SelectMany(b => b).ToArray();
            yield return w.U32((uint)data.Length).Raw(data).ToArray();
        }
    }
}

// The sandbox's own game objects shown to one player (.gob test objects, .walls), in named sets. The client forgets every object
// on a far teleport, so the sets are forgotten then (without packets).
internal sealed class ForeverWorldObjects
{
    public const uint Entry = 900000;   // sandbox-only template entry (no database; the client's name query goes unanswered)
    // Without a rotation the object stands upright, turned to its facing.
    public readonly record struct Placement(int Display, float X, float Y, float Z, float Facing, float Scale,
        (float X, float Y, float Z, float W)? Rotation = null, (float X, float Y, float Z, float W)? ParentRotation = null);

    private readonly ForeverWorldPlayer player;
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, (byte[] Guid, Placement Placement)>> sets = new();
    private ulong counter;

    public ForeverWorldObjects(ForeverWorldPlayer owner) { player = owner; }

    // Shows exactly `placements` in set `tag`: creates the new keys and removes the keys no longer listed, in one update.
    public (int Added, int Removed) Replace(string tag, IReadOnlyDictionary<string, Placement> placements)
    {
        lock (gate)
        {
            if (!sets.TryGetValue(tag, out var shown)) sets[tag] = shown = new();
            var removed = shown.Where(p => !placements.ContainsKey(p.Key)).ToList();
            foreach (var r in removed) shown.Remove(r.Key);
            var blocks = new List<byte[]>();
            var map = player.Character.Map;
            foreach (var (key, p) in placements)
            {
                if (shown.ContainsKey(key)) continue;
                var guid = ForeverGameObjects.Guid(map, Entry, ++counter);
                shown[key] = (guid, p);
                blocks.Add(ForeverGameObjects.CreateBlock(new ForeverGameObjects.Spawn(guid, Entry, p.Display, p.X, p.Y, p.Z, p.Facing,
                    p.Rotation ?? ForeverGameObjects.Rotation(p.Facing), p.Scale, ParentRotation: p.ParentRotation)));
            }
            player.SendObjects(blocks, removed.Select(r => r.Value.Guid).ToList());
            return (blocks.Count, removed.Count);
        }
    }

    public void Add(string tag, string key, Placement placement)
    {
        lock (gate)
        {
            var current = sets.TryGetValue(tag, out var shown) ? shown.ToDictionary(p => p.Key, p => p.Value.Placement) : new();
            current[key] = placement;
            Replace(tag, current);
        }
    }

    public int Clear(string tag) => Replace(tag, new Dictionary<string, Placement>()).Removed;
    public void Forget() { lock (gate) sets.Clear(); }
}

// Game objects recorded from the live game (ForeverCapture merge writes sandbox-data\gameobjects.json), shown around the player
// as scenery: generic, not selectable, under the sandbox's own entry. They have no name and nothing happens on use.
internal static class ForeverCapturedObjects
{
    private const float Cell = 100f;   // yards
    private const int Reach = 4;       // cells around the player's own: everything within 400 yards is shown
    private static readonly object gate = new();
    private static Dictionary<(int Map, int X, int Y), List<(string Key, ForeverWorldObjects.Placement Placement)>> cells = new();
    private static Dictionary<int, int> perMap = new();
    private static (ForeverWorldPlayer Player, int Map, int X, int Y) shown;
    private static bool hidden;
    private const string NoFile = "none merged yet: ForeverCapture merge <sandbox folder>";
    private static string status = NoFile;

    // Reads the file once at start. Objects with unusable values are left out; an unreadable file leaves none (.gobs says why).
    public static int Load(string path)
    {
        var read = new Dictionary<(int, int, int), List<(string, ForeverWorldObjects.Placement)>>();
        var counts = new Dictionary<int, int>();
        var problem = NoFile;
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var index = 0;
                foreach (var o in document.RootElement.GetProperty("objects").EnumerateArray())
                {
                    float F(string name) => o.GetProperty(name).GetSingle();
                    (float, float, float, float)? Quaternion(string name) =>
                        o.TryGetProperty(name, out var q) && q.GetArrayLength() == 4 ? (q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle()) : null;
                    int map = o.GetProperty("map").GetInt32(), display = o.GetProperty("display").GetInt32();
                    float x = F("x"), y = F("y"), z = F("z"), facing = F("facing"), scale = F("scale");
                    var rotation = Quaternion("rotation");
                    if (map is < 0 or > 0x1FFF || display <= 0 || rotation == null || !(MathF.Abs(x) < 40000 && MathF.Abs(y) < 40000 && MathF.Abs(z) < 20000 && MathF.Abs(facing) < 16 && scale is > 0.001f and <= 100f)) continue;
                    var cell = (map, (int)MathF.Floor(x / Cell), (int)MathF.Floor(y / Cell));
                    if (!read.TryGetValue(cell, out var list)) read[cell] = list = new();
                    list.Add(("c" + index++, new ForeverWorldObjects.Placement(display, x, y, z, facing, scale, rotation, Quaternion("parentRotation"))));
                    counts[map] = counts.GetValueOrDefault(map) + 1;
                }
                problem = "the file holds no usable objects";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            read.Clear(); counts.Clear(); problem = "gameobjects.json could not be read: " + error.GetType().Name;
        }
        lock (gate) { cells = read; perMap = counts; shown = default; status = problem; }
        return counts.Values.Sum();
    }

    // Cheap check on every movement / teleport / login: the shown set changes only when the player enters another cell or map.
    public static void Update(ForeverWorldPlayer player)
    {
        var c = player.Character;
        var here = (player, c.Map, (int)MathF.Floor(c.X / Cell), (int)MathF.Floor(c.Y / Cell));
        lock (gate)
        {
            if (cells.Count == 0 || hidden || here == shown) return;
            shown = here;
            var near = new Dictionary<string, ForeverWorldObjects.Placement>();
            for (var x = here.Item3 - Reach; x <= here.Item3 + Reach; x++)
                for (var y = here.Item4 - Reach; y <= here.Item4 + Reach; y++)
                    if (cells.TryGetValue((c.Map, x, y), out var list))
                        foreach (var (key, placement) in list) near[key] = placement;
            player.Objects.Replace("captured", near);
        }
    }

    // .gobs [on|off]
    public static string Command(string[] parts, ForeverWorldPlayer player)
    {
        int here, all;
        lock (gate)
        {
            var show = (parts.Length > 1 ? parts[1].ToLowerInvariant() : "") switch { "" => hidden, "on" or "1" => true, "off" or "0" => false, _ => (bool?)null };
            if (show == null || parts.Length > 2) return "Sandbox: usage .gobs [on|off]";
            if (cells.Count == 0) return $"Sandbox: no captured objects ({status}).";
            hidden = !show.Value; shown = default;
            if (hidden) { player.Objects.Clear("captured"); return "Sandbox: captured objects off."; }
            here = perMap.GetValueOrDefault(player.Character.Map); all = perMap.Values.Sum();
        }
        Update(player);
        return $"Sandbox: captured objects on ({here} on this map, {all} in all; shown within {Cell * Reach:0} yards).";
    }
}
