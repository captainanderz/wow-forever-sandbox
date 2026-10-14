// Independent 70338 world entry: PLAYER_LOGIN -> SMSG_CONNECT_TO -> instance socket
// (AuthContinuedSession, signed EnterEncryptedMode) -> login packets with the active
// player's create block. Factual layouts: pinned 5a125dfe WorldSession.cpp,
// AuthenticationPackets, BaseEntity.cpp (create/movement blocks), UpdateData.cpp,
// CharacterPackets/MiscPackets; field order in ForeverUpdateFields.g.cs.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AuthServer.Network;

// Update-field values by path ("Unit.Health", "Player.Customizations[0].ChrCustomizationOptionID").
internal sealed class UfValues
{
    public const int Owner = 1, PartyMember = 2, UnitAll = 4, Empath = 8;
    public static readonly byte[] EmptyGuid = new byte[16];
    private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
    public byte Flags { get; init; } = Owner | PartyMember;   // the player's own view of itself
    public UfValues Set(string path, object value) { values[path] = value; return this; }
    public UfValues Count(string path, int count) => Set(path + "#", count);
    public long I(string path) => values.TryGetValue(path, out var value) ? Convert.ToInt64(value) : 0;
    public float F(string path) => values.TryGetValue(path, out var value) ? Convert.ToSingle(value) : 0f;
    public byte[] G(string path) => values.TryGetValue(path, out var value) ? (byte[])value : EmptyGuid;
    public string S(string path) => values.TryGetValue(path, out var value) ? (string)value : "";
    public int N(string path) => values.TryGetValue(path + "#", out var value) ? Convert.ToInt32(value) : 0;
    public bool Has(string path) => values.TryGetValue(path + "?", out var value) && (bool)value;
    public bool Vis(int mask) => (Flags & mask) != 0;
}

internal static class ForeverPlayerObject
{
    public const uint UpdateObjectOpcode = 0x5E0000;
    // Classic native player models (male, female). Forever races 95/96 have no known server-side model data yet.
    public static int DisplayId(byte race, byte sex) => (race switch {
        1 => (49, 50), 2 => (51, 52), 3 => (53, 54), 4 => (55, 56), 5 => (57, 58), 6 => (59, 60), 7 => (1563, 1564), 8 => (1478, 1479), _ => (49, 50) }) is var (male, female) && sex == 1 ? female : male;
    public static int FactionTemplate(byte race) => race switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 7 => 115, 8 => 116, 96 => 2, _ => 1 };
    public static byte PowerType(byte playerClass) => playerClass switch { 1 => 1, 4 => 3, _ => 0 };   // rage, energy, else mana

    // Languages (skill line, language spell): the client only offers chat languages the player knows; without one it sends no
    // /say at all. Common / Orcish by faction plus the racial language (classic data); Forever races 95/96 get both.
    private static readonly (int skill, uint spell) Common = (98, 668), Orcish = (109, 669);
    public static (int skill, uint spell)[] Languages(byte race) => race switch {
        1 => new[] { Common }, 3 => new[] { Common, (111, 672u) }, 4 => new[] { Common, (113, 671u) }, 7 => new[] { Common, (313, 7340u) },
        2 => new[] { Orcish }, 5 => new[] { Orcish, (673, 17737u) }, 6 => new[] { Orcish, (115, 670u) }, 8 => new[] { Orcish, (315, 7341u) },
        _ => new[] { Common, Orcish } };

    public static UfValues Values(ForeverCharacter character)
    {
        var display = DisplayId(character.Race, character.Sex); var power = PowerType(character.Class);
        var v = new UfValues()
            .Set("Object.Scale", 1f)
            .Set("Unit.DisplayID", display).Set("Unit.NativeDisplayID", display).Set("Unit.Race", character.Race).Set("Unit.ClassId", character.Class)
            .Set("Unit.PlayerClassId", character.Class).Set("Unit.Sex", character.Sex).Set("Unit.DisplayPower", power)
            .Set("Unit.Health", 50).Set("Unit.MaxHealth", 50).Set("Unit.Level", character.Level).Set("Unit.FactionTemplate", FactionTemplate(character.Race))
            .Set("Unit.Flags", 0x8).Set("Unit.Flags2", 0x800)                       // player controlled, regenerate power
            .Set("Unit.AttackRoundBaseTime[0]", 2000).Set("Unit.AttackRoundBaseTime[1]", 2000).Set("Unit.RangedAttackRoundBaseTime", 2000)
            .Set("Unit.BoundingRadius", 0.389f).Set("Unit.CombatReach", 1.5f).Set("Unit.DisplayScale", 1f).Set("Unit.NativeXDisplayScale", 1f)
            .Set("Unit.HoverHeight", 1f).Set("Unit.ModCastingSpeed", 1f).Set("Unit.ModCastingSpeedNeg", 1f).Set("Unit.ModSpellHaste", 1f)
            .Set("Unit.ModHaste", 1f).Set("Unit.ModRangedHaste", 1f).Set("Unit.ModHasteRegen", 1f).Set("Unit.ModTimeRate", 1f)
            .Set("Unit.ManaCostMultiplier", 1f).Set("Unit.MaxHealthModifier", 1f).Set("Unit.GlideEventSpeedDivisor", 1f).Set("Unit.AutoAttackRangeMod", 1f)
            .Set("Player.NativeSex", character.Sex).Set("Player.VirtualPlayerRealm", BgsRealmV2.RealmAddress).Set("Player.ClassicSuperDistrictID", ForeverCharacterPackets.SuperDistrict)
            .Set("Player.Name", character.Name ?? "").Set("Player.surname", character.Surname ?? "")
            .Set("ActivePlayer.NextLevelXP", 400).Set("ActivePlayer.MaxLevel", 60)
            .Set("ActivePlayer.ModHealingPercent", 1f).Set("ActivePlayer.ModPeriodicHealingDonePercent", 1f).Set("ActivePlayer.ModSpellPowerPercent", 1f)
            .Set("ActivePlayer.ModPetHaste", 1f);
        // Power slot 0 is the class's primary power: rage starts empty, mana/energy full.
        var max = power == 1 ? 1000 : 100;
        v.Set("Unit.MaxPower[0]", max).Set("Unit.Power[0]", power == 1 ? 0 : max);
        // Defaults every reference player has (pinned Player.cpp Create / InitStatsForLevel, Unit.cpp): no empower stage (-1), base
        // stats and health, armor = agility * 2, 16 backpack slots (INVENTORY_DEFAULT_SIZE), no watched faction (-1), XP and honor
        // rest states normal (2), and the session's WoW / Battle.net account GUIDs (HighGuid 29 / 30, account 1).
        v.Set("Unit.SpellEmpowerStage", -1).Set("Unit.BaseHealth", 50).Set("Unit.BaseMana", power == 0 ? max : 0).Set("Unit.Resistances[0]", 40);
        for (var i = 0; i < 5; i++) v.Set($"Unit.Stats[{i}]", 20);
        v.Set("ActivePlayer.NumBackpackSlots", 16).Set("ActivePlayer.WatchedFactionIndex", -1)
            .Set("ActivePlayer.RestInfo[0].StateID", 2).Set("ActivePlayer.RestInfo[1].StateID", 2)
            .Set("Player.WowAccount", AccountGuid(29, 1)).Set("Player.BnetAccount", AccountGuid(30, 1));
        for (var i = 0; i < 7; i++) v.Set($"ActivePlayer.ModDamageDonePercent[{i}]", 1f).Set($"ActivePlayer.ModHealingDonePercent[{i}]", 1f);
        for (var i = 0; i < 3; i++) v.Set($"ActivePlayer.WeaponDmgMultipliers[{i}]", 1f).Set($"ActivePlayer.WeaponAtkSpeedMultipliers[{i}]", 1f);
        var languages = Languages(character.Race);
        for (var i = 0; i < languages.Length; i++)
            v.Set($"ActivePlayer.Skill.SkillLineID[{i}]", languages[i].skill).Set($"ActivePlayer.Skill.SkillRank[{i}]", 300)
             .Set($"ActivePlayer.Skill.SkillStartingRank[{i}]", 1).Set($"ActivePlayer.Skill.SkillMaxRank[{i}]", 300);
        var choices = character.Customizations ?? Array.Empty<uint>();
        v.Count("Player.Customizations", choices.Length / 2);
        for (var i = 0; i < choices.Length / 2; i++)
            v.Set($"Player.Customizations[{i}].ChrCustomizationOptionID", choices[i * 2]).Set($"Player.Customizations[{i}].ChrCustomizationChoiceID", choices[i * 2 + 1]);
        return v;
    }

    // Global GUID (ObjectGuidFactory::CreateGlobal): high = type << 58, low = id.
    public static byte[] AccountGuid(int highType, ulong id)
    {
        var guid = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(guid, id);
        BinaryPrimitives.WriteUInt64LittleEndian(guid.AsSpan(8), (ulong)highType << 58);
        return guid;
    }

    // SMSG_UPDATE_OBJECT: uint16 map, uint32 blocks, bits (1, no destroys), uint32 size, blocks.
    public static byte[] CreateSelf(ForeverCharacter character)
    {
        var guid = ForeverCharacterPackets.PlayerGuid(character.Counter);
        var block = new ForeverPacketWriter().U8(2).Guid(guid).U8(7);               // CREATE_OBJECT2, ActivePlayer type
        Movement(block, character, guid);
        var values = new ForeverPacketWriter().U8(UfValues.Owner | UfValues.PartyMember)
            .U8(3).U8(204).U8(205).U8(255)                                           // fragments: CGObject (retail 2 + 1), Tag_Unit, Tag_Player, End
            .U8(1);                                                                  // CGObject is indirect: active
        var v = Values(character);
        ForeverUpdateFields.ObjectData(values, v, "Object");
        ForeverUpdateFields.UnitData(values, v, "Unit");
        ForeverUpdateFields.PlayerData(values, v, "Player");
        ForeverUpdateFields.ActivePlayerData(values, v, "ActivePlayer");
        var valueBytes = values.ToArray();
        var blockBytes = block.U32((uint)valueBytes.Length).Raw(valueBytes).ToArray();
        return new ForeverPacketWriter().U16((ushort)character.Map).U32(1).Bit(true).Bit(false).U32((uint)blockBytes.Length).Raw(blockBytes).ToArray();
    }

    private static void Movement(ForeverPacketWriter w, ForeverCharacter c, byte[] guid)
    {
        // HasEntityPosition .. MeshObject (21 bits): entity position, this-is-you, movement update, active player.
        foreach (var bit in new[] { true, false, false, false, true, true, false, false, false, false, false, false, false, false, false, false, true, false, false, false, false }) w.Bit(bit);
        w.Flush().U32(0);                                                            // pause times
        w.Guid(guid).U64(0).U32(0).F32(c.X).F32(c.Y).F32(c.Z).F32(c.O)               // mover, flags, time, position, facing
            .F32(0).F32(0).U32(0).U32(0).F32(1f);                                    // pitch, step-up, remove forces, move index, gravity
        for (var i = 0; i < 9; i++) w.Bit(false);                                    // no GO, transport, fall, spline, ...
        w.Flush().F32(2.5f).F32(7f).F32(4.5f).F32(4.722222f).F32(2.5f).F32(7f).F32(4.5f).F32(3.141594f).F32(3.141594f);
        w.U32(0).F32(1f);                                                            // no movement forces
        for (var i = 0; i < 17; i++) w.F32(0f);                                      // Classic: no advanced-flying defaults
        w.Bit(false).Flush();                                                        // no spline
        w.Bit(false).Bit(false).Flush();                                             // active player: no scene instances, no runes
    }
}

// Login handoff from the realm socket to the instance socket (ConnectToKey).
// RealmSend writes on the realm connection (thread-safe): realm-connection login packets go there (pinned Opcodes.cpp).
internal sealed record ForeverLoginTicket(ulong Key, byte[] SessionKey, ForeverCharacter Character, DateTime Expires, Action<uint, byte[]> RealmSend);
internal static class ForeverInstanceRegistry
{
    private static readonly ConcurrentDictionary<ulong, ForeverLoginTicket> tickets = new();
    public static ulong Issue(byte[] sessionKey, ForeverCharacter character, Action<uint, byte[]> realmSend = null, uint accountId = 1)
    {
        var key = accountId | 1UL << 32 | (ulong)(RandomNumberGenerator.GetInt32(int.MaxValue) & 0x7FFFFFFF) << 33;   // AccountId:32, ConnectionType:1 (instance), Key:31
        tickets[key] = new ForeverLoginTicket(key, sessionKey.ToArray(), character, DateTime.UtcNow.AddMinutes(2), realmSend ?? ((_, _) => { }));
        return key;
    }
    public static ForeverLoginTicket Take(ulong key) => tickets.TryRemove(key, out var ticket) && ticket.Expires > DateTime.UtcNow ? ticket : null;
}

internal static class ForeverLoginPackets
{
    public const uint ConnectToOpcode = 0x4E0008, ResumeCommsOpcode = 0x4E0006, LoginVerifyWorldOpcode = 0x47002F, TutorialFlagsOpcode = 0x470269;
    public const uint LoginSetTimeSpeedOpcode = 0x4701B9, TimeSyncRequestOpcode = 0x600000;
    public const uint AuthContinuedSessionOpcode = 0x460003, QueuedMessagesEndOpcode = 0x46000B, LoadingScreenNotifyOpcode = 0x440024;
    public const uint TimeSyncResponseOpcode = 0x42005C, MoveInitActiveMoverCompleteOpcode = 0x420064, ConnectToFailedOpcode = 0x440000;
    public const uint WorldAttempt1 = 17;

    // uint32 payloads, uint32 serial, uint8 connection (instance), uint64 key, uint32 native realm, uint32 key3;
    // payload: address type IPv4, 4 address bytes, uint16 port, empty bleep token (5/24/6-bit sizes, int64 lifespan).
    public static byte[] ConnectTo(ulong key, int port) => new ForeverPacketWriter()
        .U32(1).U32(WorldAttempt1).U8(1).U64(key).U32(BgsRealmV2.RealmAddress).U32(0)
        .U8(1).Raw(new byte[] { 127, 0, 0, 1 }).U16((ushort)port)
        .Bits(0, 5).Bits(1, 24).Bits(0, 6).I64(0).ToArray();

    // CMSG_AUTH_CONTINUED_SESSION: uint64 DOS, 32 local challenge, 24 digest, uint64 key, uint32 realm, uint32 key3.
    public static (byte[] local, byte[] digest, ulong key) ReadContinuedSession(byte[] payload)
    {
        if (payload.Length != 80) throw new FormatException("Invalid continued-session size.");
        return (payload[8..40], payload[40..64], BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(64)));
    }

    public static byte[] LoginVerifyWorld(ForeverCharacter c) => new ForeverPacketWriter().I32(c.Map).F32(c.X).F32(c.Y).F32(c.Z).F32(c.O).U32(0).ToArray();

    // Logout (client 0x3F0074 / 0x3F0075 per the official 70338 sniff notes in ClassicClientOpcodes.inc; server retail 0x45012F-0x450131).
    public const uint LogoutRequestOpcode = 0x3F0074, LogoutCancelOpcode = 0x3F0075;
    public const uint LogoutResponseOpcode = 0x470130, LogoutCompleteOpcode = 0x470131, LogoutCancelAckOpcode = 0x470132;
    // SMSG_LOGOUT_RESPONSE: int32 result (0 = ok), 1 bit instant.
    public static byte[] LogoutResponse() => new ForeverPacketWriter().I32(0).Bit(true).Flush().ToArray();
    public static byte[] TutorialFlags() => new byte[32];
    public static byte[] TimeSyncRequest(uint sequence) => new ForeverPacketWriter().U32(sequence).ToArray();

    // Packed WowTime: minute 6, hour 5, weekday 3, month day 6 (0-based), month 4 (0-based), year-2000 5.
    public static uint WowTime(DateTime time) => (uint)(time.Minute | time.Hour << 6 | (int)time.DayOfWeek << 11 | (time.Day - 1) << 14 | (time.Month - 1) << 20 | (time.Year - 2000) % 100 << 24);
    public static byte[] LoginSetTimeSpeed(DateTime now) { var packed = WowTime(now); return new ForeverPacketWriter().U32(packed).U32(packed).F32(0.01666667f).I32(0).I32(0).ToArray(); }

    public const uint FeatureSystemStatusOpcode = 0x470063;
    public const uint SendKnownSpellsOpcode = 0x690019;   // retail 0x670019, server groups +2 (instance connection)

    // SMSG_SEND_KNOWN_SPELLS: 1 bit initial login (flushed by the counts), uint32 known count, uint32 favorite count, spell IDs.
    public static byte[] SendKnownSpells(IReadOnlyList<uint> spells)
    {
        var w = new ForeverPacketWriter().Bit(true).U32((uint)spells.Count).U32(0);
        foreach (var spell in spells) w.U32(spell);
        return w.ToArray();
    }   // retail 0x450063, server groups +2 (like the glue screen's 0x470064)

    // Game rules the official 70338 beta sends (pinned World.cpp, from sniffs of 70124-70338): they switch Classic behaviour on in the
    // client. 251-253 are new in 70338; without them the client asserts isRuleActive (group finder) and fails later in the session.
    private static readonly (int rule, int value)[] GameRules = {
        (213, 1), (216, 2), (174, 1), (206, 1), (109, 1), (114, 1), (211, 1), (112, 1), (173, 1), (154, 0),
        (180, 0), (181, 1), (182, 1), (195, 1), (23, 1), (98, 0), (93, 2), (102, 1), (42, 1), (40, 0),
        (157, 1), (190, 0), (162, 1), (209, 1), (89, 1), (177, 1), (197, 1), (199, -5), (63, 1), (90, 1),
        (244, 1), (33, 20), (129, 0), (228, 1), (241, 1), (243, 1), (101, 1), (237, 0),
        (70, 1), (242, 1), (247, 1), (251, 1), (252, 1), (253, 0) };
    public static int GameRuleCount => GameRules.Length + 1;

    // In-game SMSG_FEATURE_SYSTEM_STATUS (pinned SystemPackets.cpp FeatureSystemStatus::Write, values as CharacterHandler.cpp
    // SendFeatureSystemStatus with Classic.OfficialFeatureFlags: the official server's flags, field names in retail order).
    public static byte[] FeatureSystemStatus(int contentSet = 137, int realmId = 70)
    {
        var w = new ForeverPacketWriter().U8(2).U32(2).I32(realmId)                 // complaints enabled with auto-ignore, CfgRealmID, CfgRealmRecID
            .U32(0).U32(0).U32(0).U32(0).U32(0)                                     // RAF: max recruits, months, uses, days in cycle, rewards version
            .U32(300);                                                              // commerce price poll seconds
        foreach (var value in new[] { 7f, 10f, 1f, 1f, 5f, 1f, 0f, 60f, 20f, 0f, 50f, 1f, 10f, 50f, 100f, 50f, 1f, 1f, 100f, 1f, 850f, 80f }) w.F32(value);
        w.Bit(false)                                                                // quick-join toasts not disabled
            .I64(0).U32(0).U32(60000)                                               // redeem-for-balance amount, club presence delays
            .Bit(false).Guid(UfValues.EmptyGuid).Guid(UfValues.EmptyGuid)          // squelch: not squelched, no bnet account / guild
            .I32(0).I32(contentSet)                                                 // social restriction, content set (137 = Normal)
            .U32(2).U32((uint)GameRuleCount).I32(0).I32(32767)                      // disabled game modes, game rules, timerunning season, seconds left
            .I16(32).I16(32).U32(10).U32(0)                                         // name lookups per request, telemetry interval, not-found cache, PvP override
            .I32(10).I32(1).I32(1).I32(1).I32(20).I32(1).I32(20)                    // addon / guild / group chat throttles
            .F32(0).F32(0).F32(0)                                                   // addon performance thresholds
            .U8(3).I32(13).I32(0).U8(2).I32(5).I32(0);                              // disabled game modes (official)
        foreach (var (rule, value) in GameRules) w.I32(rule).I32(value).F32(0);
        w.I32(200).I32(0).F32(0.5f);
        foreach (var bit in new[] {
            true, true, true, false, false, false, false, true,                     // voice, Europa ticket config present, Bpay store, item restoration, session alert, RAF x2, undelete
            false, false, true, true, false, false, true, true,                     // restricted, commerce server, tutorials, veteran/world token kick, kiosk, competitive, redeem
            true, true, true, false, false, true, false, false,                     // war mode, communities, bnet groups, character communities, presence-all, voice parental x2, quest session
            false, true, true, false, false, false, false, false,                   // chat muted, club finder, community finder, crash reporter, speak-for-me, AADC x2, LFG authenticator
            false, true, false, false, true, false, false, false,                   // scripts disallowed, timerunning, identity options, content tracking, LFD, LFR, pet happiness, guild events
            false }) w.Bit(bit);                                                    // guild trade skills
        w.Bits(2, 10).Bit(false).Bit(false).Bit(true).Bit(false).Bit(false)         // Classic 10-bit field (official 2), currency transfer, NetEase, lobby queue, lobby customizations, addon profiling
            .Bit(false).Bit(false).Bit(false).Flush();                              // UGC mute, UGC risky, friends disabled
        // Europa ticket config: tickets off, bugs / complaints / suggestions on, then the two throttle states.
        w.Bit(false).Bit(true).Bit(true).Bit(true).U32(10).U32(60000).U32(0).U32(130929).U32(1).U32(60000).U32(0).U32(130929);
        return w.ToArray();
    }

    // CMSG_LOADING_SCREEN_NOTIFY: int32 map, 1 bit showing.
    public static (int map, bool showing) ReadLoadingScreen(byte[] payload) { var reader = new ForeverPacketReader(payload); return (reader.I32(), reader.Bit()); }
    // CMSG_CONNECT_TO_FAILED: uint8 connection, uint32 serial.
    public static (byte con, uint serial) ReadConnectToFailed(byte[] payload) { var reader = new ForeverPacketReader(payload); return (reader.U8(), reader.U32()); }
}

// Instance socket (port 3724): the client reconnects here after SMSG_CONNECT_TO and proves
// the realm session with CMSG_AUTH_CONTINUED_SESSION. Instance-connection login packets go here
// (pinned Opcodes.cpp: LOGIN_VERIFY_WORLD, LOGIN_SET_TIME_SPEED, TIME_SYNC_REQUEST, UPDATE_OBJECT).
internal sealed class ForeverInstanceSocket
{
    // Diagnostics: movement floods the connection, so only its first packets are logged; other opcodes up to a larger bound.
    private const int MaxMovementEvents = 64, MaxFrameEvents = 1024;
    private readonly Socket socket;
    public ForeverInstanceSocket(Socket client) { socket = client; }

    public void Accept()
    {
        var connection = global::AuthServer.LocalAuthDiagnostics.NewConnection();
        using var crypt = new ForeverPacketCrypt();
        byte[] sessionKey = null, encryptKey = null;
        ForeverWorldPlayer player = null;
        var stage = "handshake";   // diagnostics: where a failure happened
        var loggedOut = false;
        try
        {
            socket.Blocking = true; socket.NoDelay = true; socket.ReceiveTimeout = 1800000; socket.SendTimeout = 5000;
            using var stream = new NetworkStream(socket, true);
            stream.Write(Encoding.ASCII.GetBytes(ForeverWorldWire.ServerInitializer));
            if (ForeverWorldWire.ReadInitializer(stream) != ForeverWorldWire.ClientInitializer) throw new FormatException("Unexpected world initializer.");
            var serverChallenge = RandomNumberGenerator.GetBytes(32);
            var challenge = new byte[65]; RandomNumberGenerator.Fill(challenge.AsSpan(0, 32)); serverChallenge.CopyTo(challenge, 32); challenge[64] = 1;
            stream.Write(ForeverWorldWire.Seal(crypt, ForeverWorldWire.AuthChallengeOpcode, challenge));
            var frame = ForeverWorldWire.ReadSealed(stream, crypt);
            if (frame?.Opcode != ForeverLoginPackets.AuthContinuedSessionOpcode)
            { Emit("instance_unexpected_frame", new { connection, opcode = frame == null ? null : $"0x{frame.Opcode:X8}" }); return; }
            var (local, digest, key) = ForeverLoginPackets.ReadContinuedSession(frame.Payload);
            var ticket = ForeverInstanceRegistry.Take(key);
            var valid = ticket != null && CryptographicOperations.FixedTimeEquals(ForeverWorldKeys.ContinuedSessionDigest(ticket.SessionKey, key, local, serverChallenge), digest);
            Emit("instance_continued_session", new { connection, ticketFound = ticket != null, digestValid = valid, instanceType = (key >> 32 & 1) == 1 });
            if (!valid) return;
            sessionKey = ticket.SessionKey; var character = ticket.Character;
            encryptKey = ForeverWorldKeys.EncryptKey(sessionKey, local, serverChallenge);
            // Same client process as the realm socket: its key store already holds the interop key.
            stream.Write(ForeverWorldWire.Seal(crypt, ForeverWorldPackets.EnterEncryptedModeOpcode, ForeverWorldKeys.EnterEncryptedMode(encryptKey, ForeverWorldKeys.InteropSigningSeed)));

            for (int movementFrames = 0, otherFrames = 0; ; )
            {
                frame = ForeverWorldWire.ReadSealed(stream, crypt);
                if (frame == null) { Emit("instance_eof", new { connection, encrypted = crypt.Enabled }); return; }
                var movement = frame.Opcode >> 16 == 0x42;
                if (movement ? movementFrames++ < MaxMovementEvents : otherFrames++ < MaxFrameEvents)
                    Emit("instance_frame", new { connection, opcode = $"0x{frame.Opcode:X8}", payloadBytes = frame.Payload.Length, encrypted = crypt.Enabled });
                try
                {
                    if (movement && player != null) player.TrackMovement(frame.Payload);
                    // The client sends chat on this connection once in the world; replies go to the realm connection. Commands answer as
                    // system messages, other lines are echoed back as the player's /say (only lengths are logged).
                    if (player != null && frame.Opcode >= ForeverChat.FirstChatMessageOpcode && frame.Opcode <= ForeverChat.LastChatMessageOpcode
                        && ForeverChat.TryReadMessage(frame.Payload, out var text))
                    {
                        if (ForeverChat.HandleCommand(text, player) is { } reply)
                        {
                            player.Save();
                            player.RealmSend(ForeverChat.ChatOpcode, ForeverChat.SystemMessage(reply));
                            Emit("instance_chat_command", new { connection, opcode = $"0x{frame.Opcode:X8}", commandLength = text.Split(' ')[0].Length });
                        }
                        else
                        {
                            player.RealmSend(ForeverChat.ChatOpcode, ForeverChat.Say(player, ForeverChat.Language(frame.Payload), text));
                            Emit("instance_chat_say", new { connection, opcode = $"0x{frame.Opcode:X8}", textLength = text.Length });
                        }
                        continue;
                    }
                    switch (frame.Opcode)
                    {
                        case ForeverWorldPackets.EnterEncryptedModeAckOpcode when !crypt.Enabled:
                            crypt.Enable(encryptKey);
                            stage = "login";
                            SendLogin(stream, crypt, connection, character, ticket.RealmSend);
                            stage = "world";
                            ForeverWorldPlayer.Current = player = new ForeverWorldPlayer(character, (opcode, payload) => Send(stream, crypt, opcode, payload), ticket.RealmSend)
                            {
                                // Like the reference CloseSocket (shutdown send, close). A logout on the realm connection ends the read loop
                                // below this way; the finally block then saves and releases the character.
                                CloseInstance = () =>
                                {
                                    loggedOut = true;
                                    try { socket.Shutdown(SocketShutdown.Send); } catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
                                    socket.Close();
                                },
                            };
                            break;
                        case ForeverLoginPackets.LoadingScreenNotifyOpcode:
                            var (map, showing) = ForeverLoginPackets.ReadLoadingScreen(frame.Payload);
                            Emit("instance_loading_screen", new { connection, map, showing });
                            break;
                        case ForeverLoginPackets.MoveInitActiveMoverCompleteOpcode:
                            Emit("instance_active_mover_complete", new { connection });
                            if (player != null && player.PendingMoverInit)
                            {
                                player.PendingMoverInit = false;
                                try { player.OnActiveMoverReady(); } catch (Exception error) when (error is System.IO.IOException or ObjectDisposedException) { }
                                Emit("instance_flight_enabled", new { connection, canFly = player.CanFly, flightSpeed = ForeverMovementPackets.Flight * player.SpeedMultiplier * player.FlightMultiplier });
                            }
                            break;
                        case ForeverActionBar.SetActionButtonOpcode when player != null:
                            var (packed, index) = ForeverActionBar.ReadSetActionButton(frame.Payload);
                            player.SetActionButton(index, packed);
                            Emit("instance_set_action_button", new { connection, index, type = $"0x{packed >> 56:X2}", action = packed & 0x00FFFFFFFFFFFFFF });
                            break;
                        case ForeverTeleportPackets.SuspendTokenResponseOpcode when player != null:
                            Emit("instance_suspend_token_response", new { connection, handled = player.OnSuspendTokenResponse() });
                            break;
                        case ForeverTeleportPackets.WorldPortResponseOpcode when player != null:
                            Emit("instance_world_port_response", new { connection, handled = player.OnWorldPortResponse(), map = character.Map });
                            break;
                        case ForeverTeleportPackets.MoveTeleportAckOpcode:
                            Emit("instance_move_teleport_ack", new { connection });
                            break;
                        case 0x42003A:   // CMSG_MOVE_FEATHER_FALL_ACK (retail 0x41003A)
                            Emit("instance_feather_fall_ack", new { connection });
                            break;
                        case ForeverMovementPackets.MoveSetCanFlyAckOpcode:
                            Emit("instance_can_fly_ack", new { connection });
                            break;
                        case ForeverLoginPackets.LogoutRequestOpcode when player != null:
                            player.Logout();
                            Emit("instance_logout", new { connection, map = character.Map });
                            // Reference WorldSession::LogoutPlayer: after LOGOUT_COMPLETE the instance connection is closed (Logout closes
                            // it); the next character login opens a new one with CONNECT_TO.
                            Emit("instance_closed_after_logout", new { connection });
                            return;
                        case ForeverLoginPackets.LogoutCancelOpcode when player != null:
                            player.CancelLogout();
                            break;
                        case ForeverLoginPackets.QueuedMessagesEndOpcode:
                            Emit("instance_queued_messages_end", new { connection });
                            break;
                        case ForeverWorldPackets.PingOpcode when frame.Payload.Length >= 4:
                            Send(stream, crypt, ForeverWorldPackets.PongOpcode, ForeverWorldPackets.Pong(BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload)));
                            break;
                        case ForeverWorldPackets.LogDisconnectOpcode:
                            Emit("instance_log_disconnect", new { connection, reason = frame.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload) : (uint?)null });
                            break;
                    }
                }
                catch (FormatException) { Emit("instance_packet_malformed", new { connection, opcode = $"0x{frame.Opcode:X8}", payloadBytes = frame.Payload.Length }); }
            }
        }
        catch (AuthenticationTagMismatchException) { Emit("instance_decrypt_failure", new { connection }); }
        catch (Exception) when (loggedOut) { Emit("instance_closed_after_logout", new { connection }); }
        catch (Exception error) { Emit("instance_failure", new { connection, stage, errorType = error.GetType().Name }); }
        finally
        {
            if (player != null)
            {
                try { player.Save(); } catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
                if (ForeverWorldPlayer.Current == player) ForeverWorldPlayer.Current = null;
            }
            foreach (var secret in new[] { sessionKey, encryptKey }) if (secret != null) CryptographicOperations.ZeroMemory(secret);
            socket.Dispose();
        }
    }

    // Reference order (CharacterHandler.cpp HandlePlayerLogin): RESUME_COMMS; tutorial flags (realm); LOGIN_VERIFY_WORLD; the
    // character's account data times and the in-game FEATURE_SYSTEM_STATUS with the game rules (realm); before add to map
    // TIME_SYNC_REQUEST and LOGIN_SET_TIME_SPEED; then the player's own create block when it is added to the map.
    private static void SendLogin(NetworkStream stream, ForeverPacketCrypt crypt, int connection, ForeverCharacter character, Action<uint, byte[]> realmSend)
    {
        Send(stream, crypt, ForeverLoginPackets.ResumeCommsOpcode, Array.Empty<byte>());
        // Sandbox macros: missing defaults are added to the character's macro cache and put on empty main-bar buttons.
        var macros = ForeverAccountData.Current.Get(character.Counter, ForeverMacros.CharacterMacrosType);
        var merged = ForeverMacros.Merge(macros, out var addedMacros);
        if (addedMacros.Count > 0)
        {
            ForeverAccountData.Current.Set(character.Counter, ForeverMacros.CharacterMacrosType, merged, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            ForeverMacros.PlaceOnEmptyBar(character.ActionButtons, addedMacros);
            ForeverCharacterStore.Current.Persist();
        }
        var realmPackets = 0;
        void Realm(uint opcode, byte[] payload) { try { realmSend(opcode, payload); realmPackets++; } catch (Exception error) when (error is System.IO.IOException or ObjectDisposedException) { } }
        Realm(ForeverLoginPackets.TutorialFlagsOpcode, ForeverLoginPackets.TutorialFlags());
        Send(stream, crypt, ForeverLoginPackets.LoginVerifyWorldOpcode, ForeverLoginPackets.LoginVerifyWorld(character));
        Realm(ForeverAccountData.AccountDataTimesOpcode, ForeverAccountData.Current.AccountDataTimes(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ForeverCharacterPackets.PlayerGuid(character.Counter)));
        Realm(ForeverLoginPackets.FeatureSystemStatusOpcode, ForeverLoginPackets.FeatureSystemStatus());
        Realm(ForeverWorldPackets.SetTimeZoneInformationOpcode, ForeverWorldPackets.SetTimeZoneInformation());
        // Both connections are loopback; a short pause lets the client take the realm packets before the create block.
        System.Threading.Thread.Sleep(50);
        Send(stream, crypt, ForeverLoginPackets.TimeSyncRequestOpcode, ForeverLoginPackets.TimeSyncRequest(0));
        // Reference order: known spells (here the race's language spells) before the time speed and the create block.
        Send(stream, crypt, ForeverLoginPackets.SendKnownSpellsOpcode, ForeverLoginPackets.SendKnownSpells(ForeverPlayerObject.Languages(character.Race).Select(language => language.spell).ToArray()));
        Send(stream, crypt, ForeverActionBar.UpdateActionButtonsOpcode, ForeverActionBar.UpdateActionButtons(character.ActionButtons));
        Send(stream, crypt, ForeverLoginPackets.LoginSetTimeSpeedOpcode, ForeverLoginPackets.LoginSetTimeSpeed(DateTime.Now));
        var update = ForeverPlayerObject.CreateSelf(character);
        Send(stream, crypt, ForeverPlayerObject.UpdateObjectOpcode, update);
        Emit("instance_login_sent", new { connection, realmPackets, gameRules = ForeverLoginPackets.GameRuleCount, map = character.Map, zone = character.Zone, race = character.Race, playerClass = character.Class,
            displayId = ForeverPlayerObject.DisplayId(character.Race, character.Sex), updateObjectBytes = update.Length });
    }

    // Locked per connection: chat commands on the realm thread send movement packets here too.
    private static void Send(NetworkStream stream, ForeverPacketCrypt crypt, uint opcode, byte[] payload) { lock (crypt) stream.Write(ForeverWorldWire.Seal(crypt, opcode, payload)); }
    private static void Emit(string kind, object metadata) => global::AuthServer.LocalAuthDiagnostics.Emit(kind, metadata);
}
