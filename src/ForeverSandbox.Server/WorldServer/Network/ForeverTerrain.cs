// Terrain heights from the client's own map files (Map.db2 -> WDT MAID -> root ADT MCNK / MCVT), loaded on demand, and cached
// access to client tables. Height interpolation follows the reference GridMap (four triangles around each cell's centre
// vertex, holes excluded). Collision models and the navigation mesh are not part of this yet (needed for a 1:1 Blink).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AuthServer.Network;

internal sealed class ForeverTerrain
{
    public const int MapTableFileId = 1349477, TactKeyLookupFileId = 1302851, MapWdtField = 21;
    public const float TileSize = 533.33333f, ChunkSize = TileSize / 16, UnitSize = ChunkSize / 8;
    private const int MaxCachedTiles = 64;

    private readonly ForeverCasc casc;
    private readonly Dictionary<int, int> wdtFiles = new();
    private readonly Dictionary<int, byte[]> tileIndex = new();                    // map -> MAID
    private readonly Dictionary<(int map, int x, int y), Chunk[]> tiles = new();
    private readonly LinkedList<(int, int, int)> recent = new();
    private readonly object gate = new();

    private sealed record Chunk(float X, float Y, float Z, float[] Heights, ushort Holes, ulong HiresHoles, bool Hires, bool Impass);
    public readonly record struct ImpassEdge(float X1, float Y1, float X2, float Y2, float Floor, bool Red);

    // Opened in the background at server start; null until ready or when no client install was given.
    internal static volatile ForeverTerrain Current;
    internal static volatile string Status = "not configured";

    private ForeverTerrain(ForeverCasc storage)
    {
        casc = storage;
        var map = new ForeverDb2(casc.OpenFile(MapTableFileId));
        foreach (var id in map.Ids) wdtFiles[id] = map.Get(id, MapWdtField);
    }

    public static void StartLoading(string installDirectory, string version, Func<int, byte[]> tactKeyById)
    {
        Status = "loading";
        Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // TACT keys arrive by ID (hotfixes); encrypted blocks name them: TactKeyLookup (unencrypted) maps ID -> name.
                Dictionary<ulong, byte[]> keys = new();
                var casc = ForeverCasc.Open(installDirectory, version, name => keys.TryGetValue(name, out var key) ? key : null);
                var lookup = new ForeverDb2(casc.OpenFile(TactKeyLookupFileId));
                foreach (var id in lookup.Ids)
                {
                    var name = (ulong)(uint)lookup.Get(id, 0, 0) | (ulong)(uint)lookup.Get(id, 0, 1) << 32;
                    if (tactKeyById(id) is { Length: 16 } key) keys[name] = key;
                }
                var terrain = new ForeverTerrain(casc);
                Current = terrain;
                Status = $"ready ({terrain.wdtFiles.Count} maps, {keys.Count} TACT keys)";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException)
            {
                Status = "unavailable: " + error.GetType().Name + ": " + error.Message;
            }
            finally
            {
                global::AuthServer.LocalAuthDiagnostics.Emit("game_files_loaded", new { ready = Current != null, elapsedMs = started.ElapsedMilliseconds });
            }
        });
    }

    public bool HasMap(int map) => wdtFiles.ContainsKey(map);
    public bool HasFile(int fileDataId) => casc.Has(fileDataId);

    // Ground height at (x, y) on the map, or null (no terrain tile, hole, or unknown map).
    public float? Height(int map, float x, float y)
    {
        int tx = (int)MathF.Floor(32 - y / TileSize), ty = (int)MathF.Floor(32 - x / TileSize);
        if (tx is < 0 or > 63 || ty is < 0 or > 63) return null;
        var chunks = Tile(map, tx, ty);
        if (chunks == null) return null;
        float tileX = (32 - ty) * TileSize, tileY = (32 - tx) * TileSize;
        int cy = Math.Clamp((int)((tileX - x) / ChunkSize), 0, 15), cx = Math.Clamp((int)((tileY - y) / ChunkSize), 0, 15);
        var c = chunks[cy * 16 + cx];
        if (c == null) return null;
        float fx = (c.X - x) / UnitSize, fy = (c.Y - y) / UnitSize;   // 0..8 along X (rows) and Y (columns)
        int r = Math.Clamp((int)fx, 0, 7), col = Math.Clamp((int)fy, 0, 7);
        fx = Math.Clamp(fx - r, 0, 1); fy = Math.Clamp(fy - col, 0, 1);
        if (c.Hires ? (c.HiresHoles >> (r * 8 + col) & 1) != 0 : c.Holes != 0 && (c.Holes >> ((r / 2) * 4 + col / 2) & 1) != 0) return null;
        float Outer(int rr, int cc) => c.Heights[rr * 17 + cc];
        float tl = Outer(r, col), tr = Outer(r, col + 1), bl = Outer(r + 1, col), br = Outer(r + 1, col + 1), m = c.Heights[r * 17 + 9 + col];
        float h;
        if (fx + fy < 1)
            h = fy > fx ? tl + (tr - tl) * fy + 2 * (m - (tl + tr) / 2) * fx           // top triangle (TL, TR, centre)
                        : tl + (bl - tl) * fx + 2 * (m - (tl + bl) / 2) * fy;          // left triangle (TL, BL, centre)
        else
            h = fy > fx ? br + (tr - br) * (1 - fx) + 2 * (m - (tr + br) / 2) * (1 - fy) // right triangle (TR, BR, centre)
                        : br + (bl - br) * (1 - fy) + 2 * (m - (bl + br) / 2) * (1 - fx); // bottom triangle (BL, BR, centre)
        return c.Z + h;
    }

    // Impassable terrain (MCNK flag 0x2, undocumented): the client blocks the whole chunk from its lowest height upward. One edge per
    // side of each impassable chunk in the tiles within `radius` of (x, y), coloured like the wowmap layer: red next to another
    // impassable chunk or a missing chunk (no MCNK, or no ADT), yellow next to a walkable chunk or the edge of the 64x64 grid.
    // IndexY is the row (north to south, X decreasing), IndexX the column (west to east, Y decreasing); seams read the next tile.
    public List<ImpassEdge> ImpassWalls(int map, float x, float y, int radius)
    {
        int centreX = (int)MathF.Floor(32 - y / TileSize), centreY = (int)MathF.Floor(32 - x / TileSize);
        var edges = new List<ImpassEdge>();
        int State(int tx, int ty, int ix, int iy)   // 1 impassable, -1 walkable or outside the grid, 0 missing
        {
            if (ix < 0) { tx--; ix += 16; } else if (ix > 15) { tx++; ix -= 16; }
            if (iy < 0) { ty--; iy += 16; } else if (iy > 15) { ty++; iy -= 16; }
            if (tx is < 0 or > 63 || ty is < 0 or > 63) return -1;
            return Tile(map, tx, ty)?[iy * 16 + ix] is not { } c ? 0 : c.Impass ? 1 : -1;
        }
        for (var ty = Math.Max(0, centreY - radius); ty <= Math.Min(63, centreY + radius); ty++)
            for (var tx = Math.Max(0, centreX - radius); tx <= Math.Min(63, centreX + radius); tx++)
            {
                if (Tile(map, tx, ty) is not { } chunks) continue;
                for (var i = 0; i < 256; i++)
                {
                    if (chunks[i] is not { Impass: true } c) continue;
                    int ix = i % 16, iy = i / 16;
                    var floor = c.Z + c.Heights.Min();
                    float north = c.X, south = c.X - ChunkSize, west = c.Y, east = c.Y - ChunkSize;
                    edges.Add(new ImpassEdge(north, west, north, east, floor, State(tx, ty, ix, iy - 1) >= 0));
                    edges.Add(new ImpassEdge(south, west, south, east, floor, State(tx, ty, ix, iy + 1) >= 0));
                    edges.Add(new ImpassEdge(north, west, south, west, floor, State(tx, ty, ix - 1, iy) >= 0));
                    edges.Add(new ImpassEdge(north, east, south, east, floor, State(tx, ty, ix + 1, iy) >= 0));
                }
            }
        return edges;
    }

    private Chunk[] Tile(int map, int tx, int ty)
    {
        lock (gate)
        {
            var key = (map, tx, ty);
            if (tiles.TryGetValue(key, out var cached)) { recent.Remove(key); recent.AddFirst(key); return cached; }
            if (!wdtFiles.TryGetValue(map, out var wdt) || wdt <= 0) return null;
            if (!tileIndex.TryGetValue(map, out var maid))
            {
                maid = Chunks(casc.TryOpenFile(wdt) ?? Array.Empty<byte>()).FirstOrDefault(c => c.tag == "MAID").data;
                tileIndex[map] = maid;
            }
            Chunk[] result = null;
            var root = maid != null && maid.Length >= 64 * 64 * 32 ? BinaryPrimitives.ReadInt32LittleEndian(maid.AsSpan((ty * 64 + tx) * 32)) : 0;
            if (root > 0 && casc.TryOpenFile(root) is { } adt)
            {
                result = new Chunk[256];
                foreach (var (tag, d) in Chunks(adt))
                {
                    if (tag != "MCNK" || d.Length < 0x80) continue;
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
                    int ix = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)), iy = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8));
                    var mcvt = Chunks(d, 0x80).FirstOrDefault(s => s.tag == "MCVT").data;
                    if (mcvt == null || mcvt.Length < 145 * 4 || ix is < 0 or > 15 || iy is < 0 or > 15) continue;
                    var heights = new float[145];
                    for (var i = 0; i < 145; i++) heights[i] = BinaryPrimitives.ReadSingleLittleEndian(mcvt.AsSpan(i * 4));
                    result[iy * 16 + ix] = new Chunk(BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0x68)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0x6C)),
                        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(0x70)), heights, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x3C)),
                        BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(0x14)), (flags & 0x10000) != 0, (flags & 0x2) != 0);
                }
            }
            tiles[key] = result; recent.AddFirst(key);
            while (recent.Count > MaxCachedTiles) { tiles.Remove(recent.Last.Value); recent.RemoveLast(); }
            return result;
        }
    }

    private static IEnumerable<(string tag, byte[] data)> Chunks(byte[] data, int start = 0)
    {
        var p = start;
        while (p + 8 <= data.Length)
        {
            var tag = new string(new[] { (char)data[p + 3], (char)data[p + 2], (char)data[p + 1], (char)data[p] });
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4));
            if (size < 0 || p + 8 + size > data.Length) yield break;
            yield return (tag, data.AsSpan(p + 8, size).ToArray());
            p += 8 + size;
        }
    }

    // A client table by FileDataID (cached).
    private readonly Dictionary<int, ForeverDb2> tables = new();
    public ForeverDb2 Table(int fileDataId)
    {
        lock (gate)
        {
            if (!tables.TryGetValue(fileDataId, out var table)) tables[fileDataId] = table = new ForeverDb2(casc.OpenFile(fileDataId));
            return table;
        }
    }
}
