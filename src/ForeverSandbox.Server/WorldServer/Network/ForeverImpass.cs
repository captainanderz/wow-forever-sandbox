// .walls: impassable terrain walls (ForeverTerrain.ImpassWalls) around the player, recomputed when the player enters another ADT
// tile or map, or logs in again. Two ways to show them:
//   objects (default): real game objects the client renders itself, so terrain, buildings and units in front hide them. With the
//     sandbox wall models in the snapshot (ForeverSnapshot walls install; ForeverWallModels): one model per edge, a translucent
//     yellow or red plane from the chunk floor up with solid edge lines (the wowmap 3D look). Otherwise, or after .walls look:
//     force-field segments along each edge, standing on the ground; model 523 on yellow edges, 2290 on red ones.
//   overlay: impass-walls.json in the server's state directory for the ForeverSandbox launcher, which draws the wowmap look (faces
//     from the chunk floor up, through everything) over the game window; {"enabled":false} turns it off.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AuthServer.Network;

internal static class ForeverImpassWalls
{
    public enum Mode { Off, Objects, Overlay }
    public const int ObjectRadiusTiles = 1, OverlayRadiusTiles = 2;   // 3 x 3 or 5 x 5 ADT tiles around the player
    private const float SegmentWidth = 9.5f;   // models 523 / 2290: planes along their local Y, about 9.5 yards wide (GameObjectDisplayInfo box)
    internal static string OutputPath;
    private static readonly object gate = new();
    private static Mode mode;
    private static int yellowDisplay = 523, redDisplay = 2290;
    private static float scale = 1f;
    private static bool forceFields;   // .walls look: force-field segments even when the wall models are installed
    private static int tint = ForeverWallModels.DefaultTint;   // .walls glass: which wall models (face tint)
    private static bool computing;
    private static (ForeverWorldPlayer player, int map, int tx, int ty) area, pending;
    private static int generation;

    public static string Command(string[] parts, ForeverWorldPlayer player)
    {
        var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        Mode next;
        switch (arg)
        {
            case "": next = Current == Mode.Off ? Mode.Objects : Mode.Off; break;
            case "on" or "1" or "objects": next = Mode.Objects; break;
            case "off" or "0": next = Mode.Off; break;
            case "overlay": next = Mode.Overlay; break;
            case "look":
                if (parts.Length is < 4 or > 5 || !int.TryParse(parts[2], out var yellow) || !int.TryParse(parts[3], out var red) || yellow <= 0 || red <= 0
                    || parts.Length == 5 && (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s is < 0.1f or > 10f))
                    return "Sandbox: usage .walls look <yellow display ID> <red display ID> [scale 0.1-10]";
                lock (gate)
                {
                    yellowDisplay = yellow; redDisplay = red; forceFields = true;
                    if (parts.Length == 5) scale = float.Parse(parts[4], CultureInfo.InvariantCulture);
                    if (mode == Mode.Objects) { player.Objects.Clear("walls"); area = default; }
                }
                Update(player);
                return string.Create(CultureInfo.InvariantCulture, $"Sandbox: wall objects use display {yellowDisplay} (yellow edges) and {redDisplay} (red edges), scale {scale:0.##}; .walls models goes back to the wall models.");
            case "models" or "glass":
                var names = string.Join("|", ForeverWallModels.Tints.Select(t => t.Name));
                var level = arg == "glass" ? Array.FindIndex(ForeverWallModels.Tints, t => parts.Length == 3 && t.Name == parts[2].ToLowerInvariant()) : -2;
                if (level == -1) return $"Sandbox: usage .walls glass {names}";
                lock (gate) { forceFields = false; if (level >= 0) tint = level; if (mode == Mode.Objects) { player.Objects.Clear("walls"); area = default; } }
                Update(player);
                return ForeverWallModels.Installed ? $"Sandbox: walls use the sandbox wall models, {ForeverWallModels.Tints[tint].Name} glass."
                    : "Sandbox: the wall models are not in this client snapshot (ForeverSandbox snapshot walls install); force fields are used.";
            default:
                return $"Sandbox: usage .walls [on|off|overlay|models] | .walls glass {string.Join("|", ForeverWallModels.Tints.Select(t => t.Name))} | .walls look <yellow display> <red display> [scale]";
        }
        if (next == Mode.Overlay && OutputPath == null) return "Sandbox: the walls overlay needs the ForeverSandbox launcher.";
        if (next != Mode.Off && ForeverTerrain.Current == null) return "Sandbox: the game files are not loaded (" + ForeverTerrain.Status + ").";
        lock (gate)
        {
            if (mode == Mode.Objects && next != Mode.Objects) player.Objects.Clear("walls");
            if (mode == Mode.Overlay && next != Mode.Overlay) WriteOverlay("{\"enabled\":false,\"generation\":" + Interlocked.Increment(ref generation) + "}");
            mode = next; area = default;
        }
        Update(player);
        return next switch
        {
            Mode.Objects => "Sandbox: impassable walls on (force fields: yellow-edge model next to walkable ground, red-edge model next to impassable or missing ground).",
            Mode.Overlay => "Sandbox: impassable walls as the launcher overlay (yellow: next to walkable ground, red: next to impassable or missing ground).",
            _ => "Sandbox: impassable walls off.",
        };
    }

    private static Mode Current { get { lock (gate) return mode; } }

    // Cheap check on every movement / teleport / login: recompute only when the player, tile or map changed.
    public static void Update(ForeverWorldPlayer player)
    {
        var c = player.Character;
        var key = (player, c.Map, (int)MathF.Floor(32 - c.Y / ForeverTerrain.TileSize), (int)MathF.Floor(32 - c.X / ForeverTerrain.TileSize));
        lock (gate)
        {
            if (mode == Mode.Off || key == area) return;
            area = key; pending = key;
            if (computing) return;
            computing = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                (ForeverWorldPlayer player, int map, int tx, int ty) job; Mode jobMode;
                lock (gate) { job = pending; jobMode = mode; pending = default; if (job.player == null || jobMode == Mode.Off) { computing = false; return; } }
                try
                {
                    var terrain = ForeverTerrain.Current;
                    if (terrain == null) continue;
                    var tileX = (32 - job.ty - 0.5f) * ForeverTerrain.TileSize; var tileY = (32 - job.tx - 0.5f) * ForeverTerrain.TileSize;
                    var edges = terrain.ImpassWalls(job.map, tileX, tileY, jobMode == Mode.Objects ? ObjectRadiusTiles : OverlayRadiusTiles);
                    var shown = jobMode == Mode.Objects ? ShowObjects(job.player, job.map, terrain, edges) : ShowOverlay(job.map, edges);
                    global::AuthServer.LocalAuthDiagnostics.Emit("impass_walls", new { mode = jobMode.ToString(), job.map, job.tx, job.ty, edges = edges.Count, shown });
                }
                catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                {
                    global::AuthServer.LocalAuthDiagnostics.Emit("impass_walls_failed", new { job.map, job.tx, job.ty, error = error.GetType().Name });
                }
            }
        });
    }

    // Force-field segments along each edge, rotated so the model's plane (local Y) follows the edge, standing on the ground at the
    // segment's middle (never below the chunk floor). Edges shared by two impassable chunks get one set of segments.
    private static int ShowObjects(ForeverWorldPlayer player, int map, ForeverTerrain terrain, List<ForeverTerrain.ImpassEdge> edges)
    {
        int yellow, red, glass; float size; bool fields;
        lock (gate) { yellow = yellowDisplay; red = redDisplay; size = scale; fields = forceFields; glass = tint; }
        var placements = new Dictionary<string, ForeverWorldObjects.Placement>();
        static string Key(ForeverTerrain.ImpassEdge e, int segment) =>
            string.Create(CultureInfo.InvariantCulture, $"{MathF.Min(e.X1, e.X2):0.#},{MathF.Min(e.Y1, e.Y2):0.#},{MathF.Max(e.X1, e.X2):0.#},{MathF.Max(e.Y1, e.Y2):0.#},{segment}");
        if (!fields && ForeverWallModels.Installed)
        {
            // One wall model per edge (33 1/3 yards along its local Y), standing on the chunk floor like the wowmap walls; an edge
            // between two impassable chunks is shown once, from the lower floor.
            foreach (var e in edges)
            {
                float dx = e.X2 - e.X1, dy = e.Y2 - e.Y1, length = MathF.Sqrt(dx * dx + dy * dy);
                var key = Key(e, -1);
                var floor = placements.TryGetValue(key, out var shared) ? MathF.Min(shared.Z, e.Floor) : e.Floor;
                placements[key] = new ForeverWorldObjects.Placement(ForeverWallModels.Display(glass, e.Red),
                    (e.X1 + e.X2) / 2, (e.Y1 + e.Y2) / 2, floor, MathF.Atan2(-dx / length, dy / length), 1f);
            }
            lock (gate)
            {
                if (mode != Mode.Objects || area.player != player) return 0;
                player.Objects.Replace("walls", placements);
            }
            return placements.Count;
        }
        foreach (var e in edges)
        {
            float dx = e.X2 - e.X1, dy = e.Y2 - e.Y1, length = MathF.Sqrt(dx * dx + dy * dy);
            var facing = MathF.Atan2(-dx / length, dy / length);   // the model's local Y axis = (-sin, cos) of the facing
            var segments = Math.Max(1, (int)MathF.Ceiling(length / (SegmentWidth * size)));
            for (var i = 0; i < segments; i++)
            {
                float t = (i + 0.5f) / segments, x = e.X1 + dx * t, y = e.Y1 + dy * t;
                var z = MathF.Max(terrain.Height(map, x, y) ?? e.Floor, e.Floor);
                placements[Key(e, i)] = new ForeverWorldObjects.Placement(e.Red ? red : yellow, x, y, z, facing, size);
            }
        }
        lock (gate)
        {
            if (mode != Mode.Objects || area.player != player) return 0;
            player.Objects.Replace("walls", placements);
        }
        return placements.Count;
    }

    private static int ShowOverlay(int map, List<ForeverTerrain.ImpassEdge> edges)
    {
        var json = new StringBuilder(64 + edges.Count * 64);
        json.Append(CultureInfo.InvariantCulture, $"{{\"enabled\":true,\"generation\":{Interlocked.Increment(ref generation)},\"map\":{map},\"edges\":[");
        json.AppendJoin(',', edges.Select(e => string.Create(CultureInfo.InvariantCulture, $"[{e.X1:0.###},{e.Y1:0.###},{e.X2:0.###},{e.Y2:0.###},{e.Floor:0.###},{(e.Red ? 1 : 0)}]")));
        json.Append("]}");
        lock (gate) if (mode == Mode.Overlay) WriteOverlay(json.ToString());
        return edges.Count;
    }

    // GameObjectDisplayInfo rows (one hotfix push) for ForeverWallModels' display IDs: the two test displays, then a yellow and a red
    // wall per glass tint. The client caches hotfixes by push, so changed or added rows need a new push ID (2100001 had four rows).
    public const uint GameObjectDisplayInfoTable = 0x6D100DCB;
    private const int DisplayPush = 2100002;
    public static IEnumerable<ForeverHotfixStore.Record> DisplayHotfixes()
    {
        float[] field = { -0.31f, -4.79f, -1.5f, 0.27f, 4.72f, 7.71f };   // display 523's box
        float t = ForeverWallModels.Bar / 2, w = ForeverWallModels.Width / 2;
        float[] wall = { -t, -w - t, 0, t, w + t, ForeverWallModels.Height };
        var rows = new List<(int id, int file, float[] box)>
        {
            (ForeverWallModels.OriginalDisplay, ForeverWallModels.CopySource, field), (ForeverWallModels.CopyDisplay, ForeverWallModels.CopyFile, field),
        };
        for (var glass = 0; glass < ForeverWallModels.Tints.Length; glass++)
            foreach (var red in new[] { false, true }) rows.Add((ForeverWallModels.Display(glass, red), ForeverWallModels.ModelFile(glass, red), wall));
        return rows.Select((r, i) => new ForeverHotfixStore.Record(DisplayPush, (uint)(DisplayPush * 16 + i), GameObjectDisplayInfoTable, r.id, 1, DisplayRow(r.box, r.file))).ToList();
    }

    // Record layout 16D1DD61 (WoWDBDefs, 1.60.1): GeoBox[6], FileDataID, ObjectEffectPackageID (int16), OverrideLootEffectScale,
    // OverrideNameScale, AlternateDisplayType, ClientCreatureDisplayInfoID, ClientItemID, Field_11_0_0_54210_008 (uint16); the ID
    // is not part of the record.
    public static byte[] DisplayRow(float[] box, int fileDataId)
    {
        var w = new ForeverPacketWriter();
        foreach (var f in box) w.F32(f);
        return w.I32(fileDataId).I16(0).F32(0).F32(0).I32(0).I32(0).I32(0).U16(0).ToArray();
    }

    private static void WriteOverlay(string json)
    {
        try { File.WriteAllText(OutputPath + ".tmp", json); File.Move(OutputPath + ".tmp", OutputPath, true); }
        catch (IOException) { }
    }
}
