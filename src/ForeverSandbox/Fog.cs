// Full fog off (on by default; "fullFogOff": false in sandbox.json turns it off). The chat command .fog off makes the
// SandboxTools addon turn the client's volumetric-fog setting off; while that setting is off, this also turns off the two
// fog sources no setting reaches, in the running game's memory only:
//   - Map.Flags[2] bit 15 of the current map's record in the client's in-memory Map table. The client re-derives its map-driven
//     volumetric fog flag from that record every frame, so with the bit clear it keeps that fog off itself.
//   - the main world scene's standard-fog switch (scene +0x71f).
// .fog on (volumetric fog back on) restores exactly the values changed here. Executable code is never patched: the client
// checks its code and crashed about 48 s after a one-byte code patch (docs/fog.md). Writes go only to
// committed private read/write data of the client this launcher started, re-validated while the client is paused. Nothing on
// disk changes; it is gone when the game closes.
using System.Buffers.Binary;
using System.Diagnostics;
using static ForeverSandbox.ClientMemory;

namespace ForeverSandbox;

internal sealed class FogMonitor
{
    private const uint StagedGraphicsFlags = 0x56AF5B4, MapDb = 0x603AC60, MapRowLookup = 0x678550;
    private const uint VolumeFogBit = 0x100, MapFogBit = 0x8000;
    private const int MapFlagsField = 25, SceneFogSwitch = 0x71F;

    // The Map table the owned flag addresses belong to; a reloaded table means those addresses are gone.
    private readonly record struct MapTable(ulong Loader, ulong Descriptors, ulong Palettes, ulong Palette, uint PaletteSize, uint BitOffset, uint Width);

    private readonly ClientMemory memory;
    private MapTable? owner;
    private readonly Dictionary<ulong, uint> ownedFlags = new(); // Map.Flags[2] address -> original value
    private ulong ownedScene;
    private (MapTable Table, uint Map, ulong Address)? lookup;
    private string lastStatus;
    private bool reportedUnsupported;

    public FogMonitor(Process owned) { memory = new ClientMemory(owned); }

    // Returns a status line when something changed.
    public string Poll()
    {
        if (!memory.Attach())
        {
            if (!memory.Unsupported || reportedUnsupported) return null;
            reportedUnsupported = true;
            return "the running game's image is not the expected 70338 layout; .fog off only changes the game's settings.";
        }
        if (memory.Data(StagedGraphicsFlags, 4) is not { } flags) return null;
        var status = (BinaryPrimitives.ReadUInt32LittleEndian(flags) & VolumeFogBit) == 0 ? Remove() : Restore();
        if (status == null || status == lastStatus) return null;
        lastStatus = status;
        return status;
    }

    private string Remove()
    {
        var scene = memory.MainScene();
        if (scene == 0 || memory.Map(scene) is not { } map) return null;   // not in the world
        if (ownedScene != scene) ownedScene = 0; // never touch a scene this did not change
        var table = Table();
        if (owner != null && table != owner) { ownedFlags.Clear(); owner = null; }
        ulong flags = 0;
        if (table is { } t)
        {
            if (lookup is not { } known || known.Table != t || known.Map != map) lookup = (t, map, FlagsAddress(t, map));
            flags = lookup.Value.Address;
        }
        var flagOn = flags != 0 && memory.U32(flags) is { } value && (value & MapFogBit) != 0;
        var switchOn = memory.Read(scene + SceneFogSwitch, 1) is [1];
        if (flagOn || switchOn)
            memory.Paused(() =>
            {
                if (memory.MainScene() != scene) return;
                if (memory.Read(scene + SceneFogSwitch, 1) is [1] && memory.Write(scene + SceneFogSwitch, new byte[] { 0 })) ownedScene = scene;
                if (flags != 0 && Table() == table && memory.U32(flags) is { } current && (current & MapFogBit) != 0
                    && memory.Write(flags, BitConverter.GetBytes(current & ~MapFogBit)))
                {
                    ownedFlags.TryAdd(flags, current);
                    owner = table;
                }
            });
        var sceneOff = memory.Read(scene + SceneFogSwitch, 1) is [0];
        if (flags == 0) return $"fog off except map {map}'s own fog (its Map record layout is not supported)" + (sceneOff ? "" : "; scene fog switch not changed");
        return sceneOff && memory.U32(flags) is { } now && (now & MapFogBit) == 0 ? $"all fog off (map {map})" : $"could not turn all fog off (map {map})";
    }

    private string Restore()
    {
        if (ownedScene == 0 && ownedFlags.Count == 0) return lastStatus == null ? null : "fog back on";
        var restored = memory.Paused(() =>
        {
            if (ownedScene != 0 && memory.MainScene() == ownedScene && memory.Read(ownedScene + SceneFogSwitch, 1) is [0]) memory.Write(ownedScene + SceneFogSwitch, new byte[] { 1 });
            if (owner != null && Table() == owner)
                foreach (var (address, original) in ownedFlags)
                    if (memory.U32(address) is { } value && (value & ~MapFogBit) == (original & ~MapFogBit)) memory.Write(address, BitConverter.GetBytes(value | (original & MapFogBit)));
        });
        if (!restored) return null;
        ownedScene = 0; ownedFlags.Clear(); owner = null;
        return "fog back on";
    }

    // The Map table (DB object with 26 fields) and its Flags field: palette-compressed, three values per entry.
    private MapTable? Table()
    {
        if (memory.Data(MapDb, 0x220) is not { } db || db[0x20D] != 1 || U64(db, 0x18) != 26 || U64(db, 0x48) != 26) return null;
        ulong loader = U64(db, 0x160), descriptors = U64(db, 0x10), palettes = U64(db, 0x40);
        if (memory.Read(descriptors + MapFlagsField * 24, 24) is not { } d || memory.Q(palettes + MapFlagsField * 8) is not { } palette || palette == 0) return null;
        uint paletteSize = U32(d, 4), kind = U32(d, 8), bitOffset = U32(d, 12), width = U32(d, 16), values = U32(d, 20);
        if (kind != 4 || values != 3 || width is < 1 or > 8 || paletteSize is 0 or > 4096 || paletteSize % 12 != 0) return null;
        if (memory.Q(loader) is not { } vtable || memory.Q(vtable + 8) != memory.Image + MapRowLookup) return null;
        return new MapTable(loader, descriptors, palettes, palette, paletteSize, bitOffset, width);
    }

    // Address of Map.Flags[2] for one map, primary (palette-compressed) records only; 0 when unsupported.
    private ulong FlagsAddress(MapTable table, uint map)
    {
        if (memory.Read(table.Loader, 0xE0) is not { } head) return 0;
        ulong rows = U64(head, 8), vector = U64(head, 0xB0), entries = U64(head, 0xB8);
        uint stride = U32(head, 0x38), min = U32(head, 0x40), max = U32(head, 0x44);
        var count = head[0x48];
        if (map < min || map > max || stride is <= 32 or >= 1024 || count is 0 or >= 254 || entries is 0 or > 4096) return 0;
        if (memory.Read(vector, (int)entries * 4) is not { } ids) return 0;
        var target = (ushort)(map - min); var row = -1;
        for (var i = 0; i < ids.Length; i += 4)
            if (BinaryPrimitives.ReadUInt16LittleEndian(ids.AsSpan(i)) == target) { if (row >= 0) return 0; row = ids[i + 2]; }
        if (row < 0 || row >= count) return 0;
        var size = (int)((table.BitOffset % 8 + table.Width + 7) / 8); var field = 32 + table.BitOffset / 8;
        if (field + size > stride || memory.Read(rows + (ulong)row * stride + field, size) is not { } raw) return 0;
        ulong bits = 0;
        for (var i = size - 1; i >= 0; i--) bits = bits << 8 | raw[i];
        var index = (bits >> (int)(table.BitOffset % 8)) & ((1UL << (int)table.Width) - 1);
        return index * 12 + 12 <= table.PaletteSize ? table.Palette + index * 12 + 8 : 0;
    }
}
