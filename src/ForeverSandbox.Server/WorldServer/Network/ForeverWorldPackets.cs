// Independent 70338 glue-screen packet writers. Factual layouts: pinned 5a125dfe
// AuthenticationPackets.cpp, CharacterPackets.cpp, SystemPackets.cpp (Classic
// FeatureSystemStatusGlueScreen) and ByteBuffer bit order. No reference code imported.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;

namespace AuthServer.Network;

// TrinityCore ByteBuffer semantics: bits fill MSB first; any byte write flushes.
internal sealed class ForeverPacketWriter
{
    private readonly MemoryStream data = new();
    private int bitPosition = 8;
    private byte bits;
    public ForeverPacketWriter Bit(bool value) { bitPosition--; if (value) bits |= (byte)(1 << bitPosition); if (bitPosition == 0) { data.WriteByte(bits); bits = 0; bitPosition = 8; } return this; }
    public ForeverPacketWriter Bits(uint value, int count) { for (var i = count - 1; i >= 0; i--) Bit(((value >> i) & 1) != 0); return this; }
    public ForeverPacketWriter Flush() { if (bitPosition != 8) { data.WriteByte(bits); bits = 0; bitPosition = 8; } return this; }
    public ForeverPacketWriter U8(byte value) { Flush(); data.WriteByte(value); return this; }
    public ForeverPacketWriter I8(sbyte value) => U8(unchecked((byte)value));
    public ForeverPacketWriter I16(short value) { Span<byte> buffer = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(buffer, value); return Raw(buffer); }
    public ForeverPacketWriter U16(ushort value) => I16(unchecked((short)value));
    public ForeverPacketWriter U32(uint value) { Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buffer, value); return Raw(buffer); }
    public ForeverPacketWriter I32(int value) => U32(unchecked((uint)value));
    public ForeverPacketWriter F32(float value) { Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(buffer, value); return Raw(buffer); }
    public ForeverPacketWriter I64(long value) { Span<byte> buffer = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(buffer, value); return Raw(buffer); }
    public ForeverPacketWriter U64(ulong value) => I64(unchecked((long)value));
    public ForeverPacketWriter Raw(ReadOnlySpan<byte> value) { Flush(); data.Write(value); return this; }
    public byte[] ToArray() { Flush(); return data.ToArray(); }
}

internal static class ForeverWorldPackets
{
    public const uint EnterEncryptedModeOpcode = 0x4E0004, PongOpcode = 0x4E0009;
    public const uint AuthResponseOpcode = 0x470001, EnumCharactersResultOpcode = 0x470018;
    public const uint FeatureSystemStatusGlueScreenOpcode = 0x470064, SetTimeZoneInformationOpcode = 0x470124;
    public const uint EnterEncryptedModeAckOpcode = 0x460005, PingOpcode = 0x460006, LogDisconnectOpcode = 0x460007;
    public const uint EnumCharactersOpcode = 0x440014, KeepAliveOpcode = 0x4400AB;
    public const string RealmName = "Forever Sandbox", NormalizedRealmName = "ForeverSandbox";

    // Classic race -> class availability (Human, Orc, Dwarf, Night Elf, Undead, Tauren, Gnome, Troll), the Forever
    // combos the pinned reference ships start data for (Dwarf Shaman, Undead Paladin), and the two new Forever
    // races 95 and 96 (reference race_unlock_requirement / class_expansion_requirement; they start on Zephras Isle).
    private static readonly (byte race, byte[] classes)[] RaceClasses = {
        (1, new byte[] { 1, 2, 4, 5, 8, 9 }), (2, new byte[] { 1, 3, 4, 7, 9 }), (3, new byte[] { 1, 2, 3, 4, 5, 7 }), (4, new byte[] { 1, 3, 4, 5, 11 }),
        (5, new byte[] { 1, 2, 4, 5, 8, 9 }), (6, new byte[] { 1, 3, 7, 11 }), (7, new byte[] { 1, 4, 8, 9 }), (8, new byte[] { 1, 3, 4, 5, 7, 8 }),
        (95, new byte[] { 1, 3, 4, 8, 11 }), (96, new byte[] { 1, 3, 4, 7, 11 }) };
    public static bool IsForeverRace(byte race) => race >= 95;

    public static byte[] AuthResponseOk(long unixTime)
    {
        var name = Encoding.UTF8.GetBytes(RealmName); var normalized = Encoding.UTF8.GetBytes(NormalizedRealmName);
        var writer = new ForeverPacketWriter().U32(0).Bit(true).Bit(false).Flush();          // ERROR_OK, SuccessInfo, no WaitInfo
        writer.U32(BgsRealmV2.RealmAddress).U32(1).U32(0).U8(0).U8(0).U32(0)                // realm, 1 virtual realm, rested, expansions, kick
            .U32((uint)RaceClasses.Length).U32(0).U32(0)                                     // available classes, templates, currency
            .U32(0).U32(0).U32(0).Bit(false).Bit(false).Bit(false).Flush()                   // GameTime
            .I64(unixTime)
            .U32(BgsRealmV2.RealmAddress).Bit(true).Bit(false).Bits((uint)name.Length, 8).Bits((uint)normalized.Length, 8).Flush().Raw(name).Raw(normalized);
        foreach (var (race, classes) in RaceClasses) {
            writer.U8(race).U32((uint)classes.Length);
            foreach (var playerClass in classes) writer.U8(playerClass).U8(0).U8(0).U8(0);
        }
        // Expansion trial, forced template, then four absent optionals.
        return writer.Bit(false).Bit(false).Bit(false).Bit(false).Bit(false).Bit(false).ToArray();
    }

    public static byte[] SetTimeZoneInformation(string zone = "Europe/Paris")
    {
        var text = Encoding.UTF8.GetBytes(zone);
        if (text.Length > 127) throw new ArgumentException("Time zone name exceeds 7-bit length.");
        return new ForeverPacketWriter().Bits((uint)text.Length, 7).Bits((uint)text.Length, 7).Bits((uint)text.Length, 7).Flush().Raw(text).Raw(text).Raw(text).ToArray();
    }

    // Classic layout: six flag bytes (all off), no kiosk duration, empty arrays.
    public static byte[] FeatureSystemStatusGlueScreen(int maxCharacters = 10, int contentSet = 137) => new ForeverPacketWriter()
        .Raw(new byte[6]).U32(0).I64(0).I32(maxCharacters).U32(0).I32(0).I32(0).I32(0).I32(0).I32(contentSet)
        .U32(0).U32(0).U32(0).I32(0).I32(0).I32(86400).I32(-1).I16(50).I16(600).U32(10).U32(0).I32(0).U32(0).ToArray();

    public static bool IsAvailable(byte race, byte playerClass) => RaceClasses.Any(entry => entry.race == race && entry.classes.Contains(playerClass));

    // Classic starting points (map, zone, x, y, z) shown behind character select.
    public static (int map, int zone, float x, float y, float z) StartPosition(byte race) => race switch {
        95 or 96 => (2991, 16593, 4088.6f, 1848.9f, 976.3f),     // Forever races: Zephras Isle (reference game_tele)
        2 or 8 => (1, 14, -618.518f, -4251.67f, 38.718f),          // Orc, Troll: Durotar
        3 or 7 => (0, 1, -6240.32f, 331.033f, 382.758f),           // Dwarf, Gnome: Dun Morogh
        4 => (1, 141, 10311.3f, 832.463f, 1326.41f),               // Night Elf: Teldrassil
        5 => (0, 85, 1676.71f, 1678.31f, 121.67f),                 // Undead: Tirisfal Glades
        6 => (1, 215, -2917.58f, -257.98f, 52.9968f),              // Tauren: Mulgore
        _ => (0, 12, -8949.95f, -132.493f, 83.5312f) };            // Human: Elwynn Forest

    // Characters go out as realmless regionwide entries, as the official 70235 sniff does.
    // Every race needs an unlock entry, or the client shows it as achievement-locked.
    public static byte[] EnumCharactersResult(ForeverCharacter[] characters)
    {
        var writer = new ForeverPacketWriter()
            // Success, Realmless, then all restriction flags off exactly like the reference. IsNewcomerChatCompleted
            // must stay 0: older builds named this bit IsNewPlayer ("forbids allied races"), and with it set this
            // client locked every race behind "complete an achievement".
            .Bit(true).Bit(characters.Length > 0).Bit(false).Bit(false).Bit(false).Bit(false).Bit(false).Bit(false)
            .Bit(true).Bit(false)                                                                                  // ClassDisableMask present (0), ForceCharacterListSort
            .U32(0).U32((uint)characters.Length).I32(Math.Max(1, characters.Select(c => (int)c.Level).DefaultIfEmpty(1).Max()))
            .U32((uint)RaceClasses.Length).U32(0).U32(0).U32(0).U32(0);
        for (var i = 0; i < characters.Length; i++)
            writer.Basic(characters[i], i).U64(0).F32(0).F32(0).I32(0).I8(-1).I16(0);              // money, item level, M+ score, PvP rating/bracket/spec
        foreach (var (race, classes) in RaceClasses) {
            writer.I8((sbyte)race).U32((uint)classes.Length);
            foreach (var playerClass in classes) writer.I8((sbyte)playerClass).U32(0).Bit(true).Bit(true).Bit(true).Flush();  // expansion, achievement, entitlement
            // License, achievement, no heritage armor, entitlement, shown, balance on, has classes. Forever locks
            // the original races behind an account achievement (the reference sends 0 there); this sandbox reports
            // it as earned so every race can be created. Races 95/96 have no achievement requirement.
            writer.Bit(true).Bit(!IsForeverRace(race)).Bit(false).Bit(true).Bit(false).Bit(false).Bit(false).Flush();
        }
        return writer.ToArray();
    }

    public static byte[] Pong(uint serial) => new ForeverPacketWriter().U32(serial).ToArray();
}
