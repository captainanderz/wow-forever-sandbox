// Sandbox teleports, action bars and default macros for the 70338 world session. Layouts: pinned MovementPackets.cpp
// (MoveTeleport, TransferPending, SuspendToken / ResumeToken, NewWorld + TeleportLocation), SpellPackets.cpp
// (UpdateActionButtons: 360 buttons, SetActionButton: uint64 action, uint16 index), Player.h (action packing, NEW_WORLD_NORMAL).
// Far teleports follow Player::TeleportTo and MovementHandler.cpp: transfer pending -> suspend token -> client response ->
// new world -> client world port response -> resume token, initial packets and the player's create block on the new map.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuthServer.Network;

internal static class ForeverTeleportPackets
{
    public const uint MoveTeleportOpcode = 0x600034;                     // retail 0x5E0034 (instance)
    public const uint TransferPendingOpcode = 0x470071, NewWorldOpcode = 0x47002B;   // retail 0x450071 / 0x45002B (realm)
    public const uint SuspendTokenOpcode = 0x470041, ResumeTokenOpcode = 0x470042;   // retail 0x450041 / 0x450042 (instance)
    public const uint MoveTeleportAckOpcode = 0x420016, SuspendTokenResponseOpcode = 0x460008, WorldPortResponseOpcode = 0x440025;
    public const uint NewWorldNormal = 16;

    // MoveTeleport: packed mover GUID, uint32 sequence, position (x, y, z), facing, bits: transport, vehicle, preload world.
    public static byte[] MoveTeleport(byte[] mover, uint sequence, float x, float y, float z, float o) =>
        new ForeverPacketWriter().Guid(mover).U32(sequence).F32(x).F32(y).F32(z).F32(o).Bit(false).Bit(false).Bit(false).Flush().ToArray();
    // TransferPending: int32 map, old position (x, y, z), bits: ship, transfer spell, taxi path.
    public static byte[] TransferPending(int map, float x, float y, float z) =>
        new ForeverPacketWriter().I32(map).F32(x).F32(y).F32(z).Bit(false).Bit(false).Bit(false).Flush().ToArray();
    // SuspendToken / ResumeToken: uint32 sequence, 2-bit reason (1 = normal teleport).
    public static byte[] Token(uint sequence) => new ForeverPacketWriter().U32(sequence).Bits(1, 2).Flush().ToArray();
    // NewWorld: int32 map, TeleportLocation (x, y, z, o, int32 floor difficulty -1, int32 floor index -1), uint32 reason,
    // movement offset (x, y, z), int32 counter, uint64 instance.
    public static byte[] NewWorld(int map, float x, float y, float z, float o, int counter) =>
        new ForeverPacketWriter().I32(map).F32(x).F32(y).F32(z).F32(o).I32(-1).I32(-1).U32(NewWorldNormal).F32(0).F32(0).F32(0).I32(counter).U64(0).ToArray();
}

// A teleport destination: map, position, facing and (when known) the zone shown in the character list.
internal sealed record ForeverLocation(string Name, int Map, float X, float Y, float Z, float O, int Zone = 0);

// Named destinations: the reference's Zephras Isle game_tele (pinned world baseline SQL) and TrinityCore's well-known game_tele
// coordinates for the classic capitals and starting areas; .loc <name> adds the player's own (sandbox-data/locations.json).
internal sealed class ForeverLocations
{
    public static readonly ForeverLocation[] BuiltIn = {
        new("Northshire", 0, -8949.95f, -132.493f, 83.5312f, 0f, 12), new("Goldshire", 0, -9464f, 62f, 56f, 0f, 12),
        new("Stormwind", 0, -8833.38f, 628.628f, 94.0066f, 1.06535f, 1519), new("Ironforge", 0, -4918.88f, -940.406f, 501.564f, 5.42347f, 1537),
        new("Undercity", 0, 1584.07f, 241.987f, -52.1534f, 0.049647f, 1497), new("BootyBay", 0, -14297.2f, 530.993f, 8.77916f, 3.98863f, 33),
        new("Orgrimmar", 1, 1629.36f, -4373.39f, 31.2564f, 3.54839f, 1637), new("ThunderBluff", 1, -1277.37f, 124.804f, 131.287f, 5.22274f, 1638),
        new("Darnassus", 1, 9949.56f, 2284.21f, 1341.4f, 1.59587f, 1657), new("Gadgetzan", 1, -7177.15f, -3785.34f, 8.36981f, 6.10237f, 440),
        new("Zephras", 2991, 4088.6f, 1848.9f, 976.3f, 0f, 16593) };

    private readonly object gate = new();
    private readonly string path;
    private readonly List<ForeverLocation> saved;
    internal static ForeverLocations Current = new(null);

    public ForeverLocations(string file)
    {
        path = file;
        saved = file != null && File.Exists(file) ? JsonSerializer.Deserialize<List<ForeverLocation>>(File.ReadAllText(file)) ?? new() : new();
    }

    public ForeverLocation Find(string name)
    {
        lock (gate)
            return saved.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? BuiltIn.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public string[] Names() { lock (gate) return BuiltIn.Select(l => l.Name).Concat(saved.Select(l => l.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }

    public void Save(ForeverLocation location)
    {
        lock (gate)
        {
            saved.RemoveAll(l => string.Equals(l.Name, location.Name, StringComparison.OrdinalIgnoreCase));
            saved.Add(location);
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
    }

    public static bool ValidName(string name) => name is { Length: >= 1 and <= 32 } && name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');
}

internal static class ForeverActionBar
{
    public const uint UpdateActionButtonsOpcode = 0x470083;   // retail 0x450083 (instance)
    public const uint SetActionButtonOpcode = 0x440062;       // retail 0x430062
    public const int ButtonCount = 360;                       // Classic 1.60 (reference SpellPackets.cpp)
    public const byte TypeMacro = 0x40;                       // ACTION_BUTTON_MACRO
    public const byte TypeCharacterMacro = 0x41;              // ACTION_BUTTON_CMACRO: what the 70338 client sets for character macros

    // SMSG_UPDATE_ACTION_BUTTONS: 360 x uint64 (type << 56 | action), uint8 reason (0 = initial).
    public static byte[] UpdateActionButtons(IReadOnlyDictionary<int, ulong> buttons)
    {
        var w = new ForeverPacketWriter();
        for (var slot = 0; slot < ButtonCount; slot++) w.U64(buttons != null && buttons.TryGetValue(slot, out var packed) ? packed : 0);
        return w.U8(0).ToArray();
    }

    // CMSG_SET_ACTION_BUTTON: uint64 packed action, uint16 index.
    public static (ulong packed, int index) ReadSetActionButton(byte[] payload)
    {
        if (payload.Length != 10) throw new FormatException("Invalid action button size.");
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload), System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8)));
    }

    public static ulong Pack(byte type, ulong action) => (ulong)type << 56 | action & 0x00FFFFFFFFFFFFFF;
}

// Sandbox macros in a character's macro cache (account data type 5): written on its first login, and any default missing by name
// is appended later (the player's own macros and edits are kept). New macros go on the first empty main-bar buttons.
// Verified with the 70338 client: character macro IDs are 0x01 << 56 | index (index 121+, after the 120 account macros), and
// the action bar refers to them as type 0x41 with action = index.
internal static class ForeverMacros
{
    public const int CharacterMacrosType = 5;   // PER_CHARACTER_MACROS_CACHE
    public const int FirstCharacterMacroIndex = 121, MaxCharacterMacros = 18, MainBarButtons = 12;
    private const ulong CharacterMacroIdFlag = 0x0100000000000000;
    public static readonly (string name, string icon, string[] lines)[] Defaults = {
        ("Fly", "Ability_Mount_Gryphon_01", new[] { "/s .fly" }),
        ("Fly Fast", "Ability_Druid_FlightForm", new[] { "/s .flyspeed 6" }),
        ("Fly Normal", "Spell_Nature_Swiftness", new[] { "/s .flyspeed 3" }),
        ("Run Fast", "Ability_Rogue_Sprint", new[] { "/s .speed 2" }),
        ("Speed Reset", "Spell_Holy_Restoration", new[] { "/s .speed 1", "/s .flyspeed 3" }),
        ("Blink", "Spell_Arcane_Blink", new[] { "/s .blink" }),
        ("Slow Fall", "Spell_Magic_FeatherFall", new[] { "/s .slowfall" }) };

    private static readonly System.Text.RegularExpressions.Regex Header = new(@"^VER 3 ([0-9A-Fa-f]{16}) ""([^""]*)""", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static string Block(ulong index, (string name, string icon, string[] lines) macro) =>
        $"VER 3 {(CharacterMacroIdFlag | index).ToString("X16", CultureInfo.InvariantCulture)} \"{macro.name}\" \"{macro.icon}\"\r\n{string.Join("\r\n", macro.lines)}\r\nEND\r\n";

    public static string CacheText() => Merge("", out _);

    // Appends the defaults missing (by name) after the highest macro index; returns the text and the new macros' indices.
    public static string Merge(string existing, out List<int> added)
    {
        added = new List<int>();
        var matches = Header.Matches(existing ?? "");
        var names = new HashSet<string>(matches.Select(m => m.Groups[2].Value), StringComparer.OrdinalIgnoreCase);
        var next = matches.Count == 0 ? FirstCharacterMacroIndex
            : Math.Max(FirstCharacterMacroIndex, matches.Max(m => (int)(ulong.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0xFFFF)) + 1);
        var text = new System.Text.StringBuilder(existing ?? "");
        if (text.Length > 0 && text[^1] != '\n') text.Append("\r\n");
        foreach (var macro in Defaults)
        {
            if (names.Contains(macro.name) || matches.Count + added.Count >= MaxCharacterMacros) continue;
            text.Append(Block((ulong)next, macro)); added.Add(next++);
        }
        return text.ToString();
    }

    // New macros fill the first empty main-bar buttons (keys 1 to =).
    public static void PlaceOnEmptyBar(IDictionary<int, ulong> buttons, IEnumerable<int> macroIndices)
    {
        var slot = 0;
        foreach (var index in macroIndices)
        {
            while (slot < MainBarButtons && buttons.ContainsKey(slot)) slot++;
            if (slot >= MainBarButtons) return;
            buttons[slot] = ForeverActionBar.Pack(ForeverActionBar.TypeCharacterMacro, (ulong)index);
        }
    }
}
