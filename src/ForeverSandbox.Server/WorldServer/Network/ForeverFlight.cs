// Sandbox flight and chat commands for the 70338 world session. Layouts: pinned MovementPackets.cpp
// (MoveSetFlag, MoveSetSpeed), ChatPackets.cpp (ChatMessage::Read, Chat::Write); opcodes are retail
// numbers shifted like the rest of the session (movement +2 groups, chat group +2 with CHAT keeping index 1).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace AuthServer.Network;

internal static class ForeverMovementPackets
{
    public const uint SetCanFlyOpcode = 0x600035, UnsetCanFlyOpcode = 0x600036;
    public const uint SetRunSpeedOpcode = 0x600020, SetRunBackSpeedOpcode = 0x600021, SetSwimSpeedOpcode = 0x600022, SetSwimBackSpeedOpcode = 0x600023;
    public const uint SetFlightSpeedOpcode = 0x600024, SetFlightBackSpeedOpcode = 0x600025;
    public const uint MoveSetCanFlyAckOpcode = 0x420045;
    public const uint SetFeatherFallOpcode = 0x60002F, SetNormalFallOpcode = 0x600030;   // retail 0x5E002F / 0x5E0030 (MoveSetFlag)
    public const uint MovementFlagFlying = 0x01000000;                                    // MOVEMENTFLAG_FLYING

    // Base speeds of the create block (yards per second).
    public const float Run = 7f, RunBack = 4.5f, Swim = 4.722222f, SwimBack = 2.5f, Flight = 7f, FlightBack = 4.5f;

    // MoveSetFlag: packed mover GUID, uint32 sequence index.
    public static byte[] MoveSetFlag(byte[] mover, uint sequence) => new ForeverPacketWriter().Guid(mover).U32(sequence).ToArray();
    // MoveSetSpeed: packed mover GUID, uint32 sequence index, float speed.
    public static byte[] MoveSetSpeed(byte[] mover, uint sequence, float speed) => new ForeverPacketWriter().Guid(mover).U32(sequence).F32(speed).ToArray();
}

// The character in the world (one client per sandbox). Movement packets go to the instance connection,
// chat replies to the realm connection; both senders lock their connection's crypt.
internal sealed class ForeverWorldPlayer
{
    public const float DefaultFlightMultiplier = 3f;
    internal static volatile ForeverWorldPlayer Current;

    private readonly Action<uint, byte[]> instanceSend;
    private readonly object gate = new();
    private uint sequence;
    public ForeverCharacter Character { get; }
    public byte[] Guid { get; }
    public Action<uint, byte[]> RealmSend { get; }
    public bool CanFly { get; private set; }
    public bool SlowFall { get; private set; }
    // From the client's latest movement packet: flying state and pitch (blink follows them while flying).
    public bool Flying { get; private set; }
    public float Pitch { get; private set; }
    public float SpeedMultiplier { get; private set; } = 1f;
    public float FlightMultiplier { get; private set; } = DefaultFlightMultiplier;

    private readonly byte[] packedGuid;
    private DateTime lastSave = DateTime.UtcNow;
    private enum TeleportState { None, WaitingSuspendResponse, WaitingWorldPort }
    private TeleportState teleportState;
    private ForeverLocation teleportTarget;
    private int newWorldCounter;
    private uint timeSyncCounter = 1;
    // The client (re)builds its mover after login and after every far teleport, then sends MOVE_INIT_ACTIVE_MOVER_COMPLETE.
    public bool PendingMoverInit { get; set; } = true;
    // Closes the instance connection after logout (reference WorldSession::LogoutPlayer); set by the instance socket.
    public Action CloseInstance { get; set; }

    // Sandbox game objects shown to this player (.gob, .walls).
    public ForeverWorldObjects Objects { get; }

    // SMSG_UPDATE_OBJECT for sandbox game objects goes to the instance connection, like the player's own create block.
    public void SendObjects(IReadOnlyList<byte[]> createBlocks, IReadOnlyList<byte[]> outOfRange)
    {
        foreach (var packet in ForeverGameObjects.Packets(Character.Map, createBlocks, outOfRange)) instanceSend(ForeverPlayerObject.UpdateObjectOpcode, packet);
    }

    public ForeverWorldPlayer(ForeverCharacter character, Action<uint, byte[]> instance, Action<uint, byte[]> realm)
    {
        Objects = new ForeverWorldObjects(this);
        Character = character; Guid = ForeverCharacterPackets.PlayerGuid(character.Counter); instanceSend = instance; RealmSend = realm;
        packedGuid = new ForeverPacketWriter().Guid(Guid).ToArray();
        SpeedMultiplier = character.SpeedMultiplier is >= 0.1f and <= 50f ? character.SpeedMultiplier : 1f;
        FlightMultiplier = character.FlightMultiplier is >= 0.1f and <= 50f ? character.FlightMultiplier : DefaultFlightMultiplier;
    }

    public void SetCanFly(bool enable)
    {
        lock (gate) { CanFly = Character.CanFly = enable; instanceSend(enable ? ForeverMovementPackets.SetCanFlyOpcode : ForeverMovementPackets.UnsetCanFlyOpcode, ForeverMovementPackets.MoveSetFlag(Guid, sequence++)); }
    }

    // Slow Fall: SMSG_MOVE_SET_FEATHER_FALL / SET_NORMAL_FALL (Unit::SetFeatherFall), the client acks.
    public void SetSlowFall(bool enable)
    {
        lock (gate) { SlowFall = Character.SlowFall = enable; instanceSend(enable ? ForeverMovementPackets.SetFeatherFallOpcode : ForeverMovementPackets.SetNormalFallOpcode, ForeverMovementPackets.MoveSetFlag(Guid, sequence++)); }
    }

    // Blink: a same-map teleport along the facing. Flying: along facing and pitch at that height. On the ground the server has
    // no terrain heights, so it lands a little above the start height and the client drops to the ground.
    public const float BlinkDistance = 20f, BlinkGroundLift = 3f;
    public string Blink(float distance)
    {
        var c = Character; float x, y, z;
        if (Flying) { var horizontal = distance * MathF.Cos(Pitch); x = c.X + horizontal * MathF.Cos(c.O); y = c.Y + horizontal * MathF.Sin(c.O); z = c.Z + distance * MathF.Sin(Pitch); }
        else { x = c.X + distance * MathF.Cos(c.O); y = c.Y + distance * MathF.Sin(c.O); z = c.Z + BlinkGroundLift; }
        var reply = Teleport(new ForeverLocation("", c.Map, x, y, z, c.O, c.Zone));
        return reply.Contains("already") ? reply : string.Create(CultureInfo.InvariantCulture, $"Sandbox: blink {distance:0.#} yards.");
    }

    // Ground and swim speeds use the general multiplier; flight speeds use it times the flight multiplier.
    public void SetSpeeds(float multiplier, float flightMultiplier)
    {
        lock (gate)
        {
            SpeedMultiplier = Character.SpeedMultiplier = multiplier; FlightMultiplier = Character.FlightMultiplier = flightMultiplier;
            void Speed(uint opcode, float speed) => instanceSend(opcode, ForeverMovementPackets.MoveSetSpeed(Guid, sequence++, speed));
            Speed(ForeverMovementPackets.SetRunSpeedOpcode, ForeverMovementPackets.Run * multiplier);
            Speed(ForeverMovementPackets.SetRunBackSpeedOpcode, ForeverMovementPackets.RunBack * multiplier);
            Speed(ForeverMovementPackets.SetSwimSpeedOpcode, ForeverMovementPackets.Swim * multiplier);
            Speed(ForeverMovementPackets.SetSwimBackSpeedOpcode, ForeverMovementPackets.SwimBack * multiplier);
            Speed(ForeverMovementPackets.SetFlightSpeedOpcode, ForeverMovementPackets.Flight * multiplier * flightMultiplier);
            Speed(ForeverMovementPackets.SetFlightBackSpeedOpcode, ForeverMovementPackets.FlightBack * multiplier * flightMultiplier);
        }
    }

    // Once the client controls its mover: the character's saved flight state and speeds, and a hint (first time only).
    public void OnActiveMoverReady()
    {
        var firstTime = !hinted; hinted = true;
        if (Character.CanFly) SetCanFly(true);
        if (Character.SlowFall) SetSlowFall(true);
        SetSpeeds(SpeedMultiplier, FlightMultiplier);
        ForeverImpassWalls.Update(this);
        if (firstTime)
            RealmSend(ForeverChat.ChatOpcode, ForeverChat.SystemMessage((CanFly ? "Sandbox: flying is on - jump, then hold Space to climb." : "Sandbox: flying is off.")
                + " Commands: .fly, .speed <n>, .flyspeed <n>, .tele, .loc, .blink, .slowfall, .fog, .walls, .gob, .help"));
    }
    private bool hinted;

    public bool Teleporting { get { lock (gate) return teleportState != TeleportState.None; } }

    // Same map: SMSG_MOVE_TELEPORT (the client acks). Another map: transfer pending + suspend token; the rest follows the client.
    public string Teleport(ForeverLocation target)
    {
        lock (gate)
        {
            if (teleportState != TeleportState.None) return "Sandbox: a teleport is already in progress.";
            if (target.Map == Character.Map)
            {
                instanceSend(ForeverTeleportPackets.MoveTeleportOpcode, ForeverTeleportPackets.MoveTeleport(Guid, sequence++, target.X, target.Y, target.Z, target.O));
                Character.X = target.X; Character.Y = target.Y; Character.Z = target.Z; Character.O = target.O;
                if (target.Zone != 0) Character.Zone = target.Zone;
                Save();
                ForeverImpassWalls.Update(this);
                return $"Sandbox: teleported to {Describe(target)}.";
            }
            RealmSend(ForeverTeleportPackets.TransferPendingOpcode, ForeverTeleportPackets.TransferPending(target.Map, Character.X, Character.Y, Character.Z));
            instanceSend(ForeverTeleportPackets.SuspendTokenOpcode, ForeverTeleportPackets.Token(sequence));   // not incremented (reference)
            teleportTarget = target; teleportState = TeleportState.WaitingSuspendResponse;
            return $"Sandbox: teleporting to {Describe(target)}...";
        }
    }

    public bool OnSuspendTokenResponse()
    {
        lock (gate)
        {
            if (teleportState != TeleportState.WaitingSuspendResponse) return false;
            var t = teleportTarget;
            RealmSend(ForeverTeleportPackets.NewWorldOpcode, ForeverTeleportPackets.NewWorld(t.Map, t.X, t.Y, t.Z, t.O, ++newWorldCounter));
            teleportState = TeleportState.WaitingWorldPort;
            return true;
        }
    }

    // The client loaded the new map: resume, then the initial packets and the player's create block there (MovementHandler.cpp).
    public bool OnWorldPortResponse()
    {
        lock (gate)
        {
            if (teleportState != TeleportState.WaitingWorldPort) return false;
            var t = teleportTarget;
            Character.Map = t.Map; Character.X = t.X; Character.Y = t.Y; Character.Z = t.Z; Character.O = t.O; Character.Zone = t.Zone;
            teleportState = TeleportState.None; CanFly = false; SlowFall = false; Flying = false; PendingMoverInit = true;
            Objects.Forget();   // the client dropped every object with the old map
            instanceSend(ForeverTeleportPackets.ResumeTokenOpcode, ForeverTeleportPackets.Token(sequence));
            instanceSend(ForeverLoginPackets.TimeSyncRequestOpcode, ForeverLoginPackets.TimeSyncRequest(timeSyncCounter++));
            instanceSend(ForeverLoginPackets.SendKnownSpellsOpcode, ForeverLoginPackets.SendKnownSpells(ForeverPlayerObject.Languages(Character.Race).Select(l => l.spell).ToArray()));
            instanceSend(ForeverActionBar.UpdateActionButtonsOpcode, ForeverActionBar.UpdateActionButtons(Character.ActionButtons));
            instanceSend(ForeverLoginPackets.LoginSetTimeSpeedOpcode, ForeverLoginPackets.LoginSetTimeSpeed(DateTime.Now));
            instanceSend(ForeverPlayerObject.UpdateObjectOpcode, ForeverPlayerObject.CreateSelf(Character));
            Save();
            ForeverImpassWalls.Update(this);
            return true;
        }
    }

    public void SetActionButton(int index, ulong packed)
    {
        if (index is < 0 or >= ForeverActionBar.ButtonCount) return;
        lock (gate) { if (packed == 0) Character.ActionButtons.Remove(index); else Character.ActionButtons[index] = packed; }
        Save();
    }

    public static string Describe(ForeverLocation l) => (string.IsNullOrEmpty(l.Name) ? "" : l.Name + " ")
        + string.Create(CultureInfo.InvariantCulture, $"(map {l.Map}: {l.X:0.##} {l.Y:0.##} {l.Z:0.##})");

    // Client movement (CMSG_MOVE_*, acks): MovementInfo starts with the mover's packed GUID, uint64 flags, uint32 time, x, y, z, facing
    // (pinned MovementPackets.cpp). The player's position is kept and saved at most every 10 seconds, and at logout / disconnect.
    public bool TrackMovement(byte[] payload)
    {
        if (Teleporting) return false;
        var offset = packedGuid.Length + 12;
        if (payload.Length < offset + 16 || !payload.AsSpan(0, packedGuid.Length).SequenceEqual(packedGuid)) return false;
        float Read(int at) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(offset + at));
        float x = Read(0), y = Read(4), z = Read(8), o = Read(12);
        var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(packedGuid.Length));
        if (payload.Length >= offset + 20 && float.IsFinite(Read(16))) Pitch = Math.Clamp(Read(16), -MathF.PI / 2, MathF.PI / 2);
        Flying = (flags & ForeverMovementPackets.MovementFlagFlying) != 0;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(o) || Math.Abs(x) > 20000 || Math.Abs(y) > 20000 || Math.Abs(z) > 10000) return false;
        Character.X = x; Character.Y = y; Character.Z = z; Character.O = o;
        if (DateTime.UtcNow - lastSave > TimeSpan.FromSeconds(10)) Save();
        ForeverImpassWalls.Update(this);
        return true;
    }

    public void Save() { lastSave = DateTime.UtcNow; ForeverCharacterStore.Current.Persist(); }

    // CMSG_LOGOUT_REQUEST: instant logout (sandbox), like the reference's rest-area path: LOGOUT_RESPONSE on the instance connection,
    // LOGOUT_COMPLETE on the realm connection; the client returns to character select.
    public void Logout()
    {
        Save();
        instanceSend(ForeverLoginPackets.LogoutResponseOpcode, ForeverLoginPackets.LogoutResponse());
        RealmSend(ForeverLoginPackets.LogoutCompleteOpcode, Array.Empty<byte>());
        CloseInstance?.Invoke();
    }

    public void CancelLogout() => instanceSend(ForeverLoginPackets.LogoutCancelAckOpcode, Array.Empty<byte>());
}

internal static class ForeverChat
{
    public const uint ChatOpcode = 0x4C0001;   // SMSG_CHAT (retail 0x4A0001)
    // .fog replies. The SandboxTools addon watches system messages for exactly these two lines and then changes the client's fog
    // settings; the launcher clears the fog no setting reaches (src/ForeverSandbox/Fog.cs). Keep them in sync with the addon.
    public const string FogOffReply = "Sandbox: fog off.", FogOnReply = "Sandbox: fog on.";
    // Client chat group 0x2D: SAY is 0x2D0023 by the reference's default mapping (retail 0x2C0023, same index), but this group is
    // partly renumbered in Classic (CHANNEL_LIST retail 0x11 -> 0x0F), so commands are accepted from any chat message opcode past the
    // channel requests; the exact-length check and the leading '.' keep other messages out.
    public const uint FirstChatMessageOpcode = 0x2D0011, LastChatMessageOpcode = 0x2D002F;

    // CMSG_CHAT_MESSAGE_*: int32 language, 11-bit text length (+1 secure bit for say/party/raid), text. Both variants fit in 2 bit bytes.
    public static bool TryReadMessage(byte[] payload, out string text)
    {
        text = null;
        if (payload.Length < 6) return false;
        var length = (payload[4] << 3) | (payload[5] >> 5);
        if (6 + length != payload.Length) return false;
        try { text = new System.Text.UTF8Encoding(false, true).GetString(payload, 6, length); return true; }
        catch (ArgumentException) { return false; }
    }

    // SMSG_CHAT /say echo: the server shows a player's own /say (CHAT_MSG_SAY 1; sender = target = the player, like Player::Say).
    public static byte[] Say(ForeverWorldPlayer player, int language, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text); var name = System.Text.Encoding.UTF8.GetBytes(player.Character.Name);
        if (bytes.Length > 4095 || name.Length > 2047) throw new ArgumentException("Chat text exceeds its bit length.");
        return new ForeverPacketWriter().U8(1).U32((uint)language).Guid(player.Guid).Guid(UfValues.EmptyGuid).Guid(ForeverPlayerObject.AccountGuid(29, 1)).Guid(player.Guid)
            .U32(BgsRealmV2.RealmAddress).U32(BgsRealmV2.RealmAddress).I32(0).U32(0).F32(0).I32(0)
            .Bits((uint)name.Length, 11).Bits((uint)name.Length, 11).Bits(0, 5).Bits(0, 7).Bits((uint)bytes.Length, 12)
            .Bit(false).Bit(false).Bit(false).Bit(false).Bit(false).Flush()
            .Raw(name).Raw(name).Raw(bytes).ToArray();
    }

    public static int Language(byte[] payload) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);

    // SMSG_CHAT as a system message (CHAT_MSG_SYSTEM 0, LANG_UNIVERSAL 0, no sender).
    public static byte[] SystemMessage(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 4095) throw new ArgumentException("Chat text exceeds 12-bit length.");
        var empty = UfValues.EmptyGuid;
        return new ForeverPacketWriter().U8(0).U32(0).Guid(empty).Guid(empty).Guid(empty).Guid(empty)
            .U32(0).U32(0).I32(0).U32(0).F32(0).I32(0)                                    // target / sender realms, achievement, flags, display time, spell
            .Bits(0, 11).Bits(0, 11).Bits(0, 5).Bits(0, 7).Bits((uint)bytes.Length, 12) // sender name, target name, prefix, channel, text sizes
            .Bit(false).Bit(false).Bit(false).Bit(false).Bit(false).Flush()                // hide log, fake sender, no broadcast text / channel / encounter
            .Raw(bytes).ToArray();
    }

    // Dot commands from chat. Returns the reply, or null when the text is not a command.
    public static string HandleCommand(string text, ForeverWorldPlayer player)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '.') return null;
        var parts = text.Substring(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (player == null) return "Sandbox: no character in the world.";
        switch (parts[0].ToLowerInvariant())
        {
            case "fly":
                var enable = parts.Length < 2 ? !player.CanFly : parts[1].ToLowerInvariant() switch { "on" or "1" => true, "off" or "0" => false, _ => (bool?)null } ?? !player.CanFly;
                player.SetCanFly(enable);
                return enable ? "Sandbox: flying on - jump, then hold Space to climb." : "Sandbox: flying off.";
            case "speed":
                if (!TryMultiplier(parts, out var speed)) return "Sandbox: usage .speed <0.1-50> (1 = normal).";
                player.SetSpeeds(speed, player.FlightMultiplier);
                return $"Sandbox: speed x{speed.ToString("0.##", CultureInfo.InvariantCulture)} (flying x{(speed * player.FlightMultiplier).ToString("0.##", CultureInfo.InvariantCulture)}).";
            case "flyspeed":
                if (!TryMultiplier(parts, out var flight)) return "Sandbox: usage .flyspeed <0.1-50> (1 = run speed).";
                player.SetSpeeds(player.SpeedMultiplier, flight);
                return $"Sandbox: flight speed x{(player.SpeedMultiplier * flight).ToString("0.##", CultureInfo.InvariantCulture)}.";
            case "blink":
                var distance = ForeverWorldPlayer.BlinkDistance;
                if (parts.Length > 1 && (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out distance) || distance is < 1f or > 200f))
                    return "Sandbox: usage .blink [1-200 yards] (default 20).";
                return player.Blink(distance);
            case "slowfall":
                var slow = parts.Length < 2 ? !player.SlowFall : parts[1].ToLowerInvariant() switch { "on" or "1" => true, "off" or "0" => false, _ => (bool?)null } ?? !player.SlowFall;
                player.SetSlowFall(slow);
                return slow ? "Sandbox: slow fall on." : "Sandbox: slow fall off.";
            case "tele" or "go":
                return TeleportCommand(parts, player);
            case "fog":
                return (parts.Length > 1 ? parts[1].ToLowerInvariant() : "") switch
                {
                    "off" or "0" => FogOffReply,
                    "on" or "1" => FogOnReply,
                    _ => "Sandbox: usage .fog off | .fog on",
                };
            case "walls":
                return ForeverImpassWalls.Command(parts, player);
            case "gob":
                return GameObjectCommand(parts, player);
            case "loc":
                var c = player.Character;
                var here = new ForeverLocation(parts.Length > 1 ? parts[1] : "", c.Map, c.X, c.Y, c.Z, c.O, c.Zone);
                if (parts.Length > 1)
                {
                    if (!ForeverLocations.ValidName(parts[1]) || ForeverLocations.BuiltIn.Any(l => string.Equals(l.Name, parts[1], StringComparison.OrdinalIgnoreCase)))
                        return "Sandbox: usage .loc <name> (letters, digits, _ or -; not a built-in name).";
                    ForeverLocations.Current.Save(here);
                    return $"Sandbox: saved {ForeverWorldPlayer.Describe(here)} - use .tele {parts[1]}";
                }
                return string.Create(CultureInfo.InvariantCulture, $"Sandbox: map {c.Map} x {c.X:0.###} y {c.Y:0.###} z {c.Z:0.###} o {c.O:0.###}");
            case "help":
                return "Sandbox commands: .fly [on|off] - toggle flying; .speed <n> - run/swim/fly speed multiplier; .flyspeed <n> - flight-only multiplier; "
                    + ".tele <name> | .tele <x> <y> <z> [o] [map] - teleport; .loc [name] - show or save your position; .slowfall [on|off]; .fog off|on - remove all fog, or bring it back; "
                    + ".walls [on|off|overlay] - show impassable terrain walls (game objects, or the launcher overlay); .walls glass faint|light|medium|strong - wall tint; .walls look <yellow> <red> [scale] - force fields instead; "
                    + ".gob <display> [scale] | .gob clear - try a game object model. Client side (Sandbox Tools addon): /walls (same as .walls).";
            default:
                return $"Sandbox: unknown command .{parts[0]} - try .help";
        }
    }

    // .tele <name> | .tele <x> <y> <z> | .tele <x> <y> <z> <map> | .tele <x> <y> <z> <o> <map> (x y z o map order).
    private static string TeleportCommand(string[] parts, ForeverWorldPlayer player)
    {
        string Usage() => "Sandbox: usage .tele <name> or .tele <x> <y> <z> [o] [map]. Names: " + string.Join(", ", ForeverLocations.Current.Names());
        if (parts.Length == 2)
            return ForeverLocations.Current.Find(parts[1]) is { } named ? player.Teleport(named) : Usage();
        if (parts.Length is < 4 or > 6) return Usage();
        var numbers = new float[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i - 1]) || !float.IsFinite(numbers[i - 1])) return Usage();
        var c = player.Character;
        var map = numbers.Length >= 4 ? numbers[^1] : c.Map;
        var o = numbers.Length == 5 ? numbers[3] : c.O;
        if (map < 0 || map > 100000 || map != MathF.Floor(map) || Math.Abs(numbers[0]) > 20000 || Math.Abs(numbers[1]) > 20000 || Math.Abs(numbers[2]) > 10000) return Usage();
        return player.Teleport(new ForeverLocation("", (int)map, numbers[0], numbers[1], numbers[2], o, (int)map == c.Map ? c.Zone : 0));
    }

    // .gob <display ID> [scale]: a game object 6 yards ahead, facing you (for trying models); .gob clear removes them.
    private static int gobCounter;
    private static string GameObjectCommand(string[] parts, ForeverWorldPlayer player)
    {
        if (parts.Length == 2 && parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
            return $"Sandbox: removed {player.Objects.Clear("gob")} test object(s).";
        var scale = 1f;
        if (parts.Length is < 2 or > 3 || !int.TryParse(parts[1], out var display) || display <= 0
            || parts.Length == 3 && (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale is < 0.01f or > 100f))
            return "Sandbox: usage .gob <display ID> [scale 0.01-100] | .gob clear";
        var c = player.Character;
        player.Objects.Add("gob", "gob" + Interlocked.Increment(ref gobCounter),
            new ForeverWorldObjects.Placement(display, c.X + 6 * MathF.Cos(c.O), c.Y + 6 * MathF.Sin(c.O), c.Z, c.O + MathF.PI, scale));
        return string.Create(CultureInfo.InvariantCulture, $"Sandbox: game object display {display} (scale {scale:0.##}) 6 yards ahead; .gob clear removes it.");
    }

    private static bool TryMultiplier(string[] parts, out float value)
    {
        value = 0;
        return parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0.1f && value <= 50f;
    }
}
