// Independent 70338 character-select support: race/class unlocks, name checks,
// random names, create/delete and a small persistent character list. Factual
// layouts: pinned 5a125dfe CharacterPackets.cpp, CharacterHandler.cpp,
// ObjectGuid.cpp and SharedDefines.h. No reference code imported.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthServer.Network;

// TrinityCore ByteBuffer read semantics: bits MSB first; ResetBitPos drops the rest.
internal sealed class ForeverPacketReader
{
    private readonly byte[] data;
    private int position, bitPosition = 8;
    private byte bits;
    public ForeverPacketReader(byte[] payload) { data = payload ?? throw new ArgumentNullException(nameof(payload)); }
    public bool Bit() { if (bitPosition == 8) { bits = Take(1)[0]; bitPosition = 0; } return ((bits >> (7 - bitPosition++)) & 1) != 0; }
    public uint Bits(int count) { uint value = 0; for (var i = 0; i < count; i++) value = (value << 1) | (Bit() ? 1u : 0u); return value; }
    public ForeverPacketReader ResetBits() { bitPosition = 8; return this; }
    public byte U8() => ResetBits().Take(1)[0];
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(ResetBits().Take(4));
    public int I32() => (int)U32();
    public string Text(int length) => new UTF8Encoding(false, true).GetString(ResetBits().Take(length));
    public byte[] Bytes(int length) => ResetBits().Take(length);
    public byte[] PackedGuid()
    {
        var mask = (ushort)(U8() | U8() << 8); var guid = new byte[16];
        for (var i = 0; i < 16; i++) if ((mask & (1 << i)) != 0) guid[i] = U8();
        return guid;
    }
    public int Remaining => data.Length - position;
    private byte[] Take(int count) { if (count < 0 || position + count > data.Length) throw new FormatException("Truncated world packet."); var slice = data[position..(position + count)]; position += count; return slice; }
}

internal sealed class ForeverCharacter
{
    public ulong Counter { get; set; }
    public string Name { get; set; } = "";
    public string Surname { get; set; } = "";
    public byte Race { get; set; }
    public byte Class { get; set; }
    public byte Sex { get; set; }
    public byte Level { get; set; } = 1;
    public int Map { get; set; }
    public int Zone { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float O { get; set; }
    // Sandbox preferences, kept between logins (.fly / .speed / .flyspeed).
    public bool CanFly { get; set; } = true;
    public bool SlowFall { get; set; }
    public float SpeedMultiplier { get; set; } = 1f;
    public float FlightMultiplier { get; set; } = ForeverWorldPlayer.DefaultFlightMultiplier;
    // Action bar: button slot -> packed action (type << 56 | action), as the client set it.
    public Dictionary<int, ulong> ActionButtons { get; set; } = new();
    // Spells taught with .learn (kept for the spell work; class spells come from the class).
    public List<int> LearnedSpells { get; set; } = new();
    public bool FirstLogin { get; set; } = true;
    public long CreateTime { get; set; }
    public uint[] Customizations { get; set; } = Array.Empty<uint>();   // option, choice pairs
}

internal sealed class ForeverCharacterStore
{
    public const int MaxCharacters = 10, MaxCustomizations = 250;
    private sealed class Document { public ulong NextCounter { get; set; } = 1; public List<ForeverCharacter> Characters { get; set; } = new(); }
    private readonly object gate = new();
    private readonly string path;
    private Document document;
    internal static ForeverCharacterStore Current = new(null);
    public ForeverCharacterStore(string file)
    {
        path = file;
        document = file != null && File.Exists(file) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(file)) ?? new() : new();
        if (document.Characters.Count > MaxCharacters || document.Characters.Any(c => !ValidName(c.Name) || c.Customizations.Length > MaxCustomizations * 2))
            throw new InvalidDataException("Character store is invalid.");
    }
    public ForeverCharacter[] List() { lock (gate) return document.Characters.ToArray(); }
    public bool NameTaken(string name) { lock (gate) return document.Characters.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)); }
    public uint Create(ForeverCharacter character)
    {
        lock (gate)
        {
            if (!ValidName(character.Name)) return ForeverCharacterPackets.CharNameFailure;
            if (document.Characters.Any(c => string.Equals(c.Name, character.Name, StringComparison.OrdinalIgnoreCase))) return ForeverCharacterPackets.CharCreateNameInUse;
            if (document.Characters.Count >= MaxCharacters || !ForeverWorldPackets.IsAvailable(character.Race, character.Class)) return ForeverCharacterPackets.CharCreateFailed;
            character.Counter = document.NextCounter++;
            document.Characters.Add(character);
            Save();
            return ForeverCharacterPackets.CharCreateSuccess;
        }
    }
    // Writes in-place changes (position, preferences) of listed characters.
    public void Persist() { lock (gate) Save(); }
    public bool Delete(ulong counter) { lock (gate) { var removed = document.Characters.RemoveAll(c => c.Counter == counter) > 0; if (removed) Save(); return removed; } }
    // Classic names: 2..12 letters. Surnames are optional and bounded the same way.
    public static bool ValidName(string name) => name is { Length: >= 2 and <= 12 } && name.All(char.IsLetter);
    private void Save()
    {
        if (path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}

internal static class ForeverCharacterPackets
{
    public const uint CreateCharacterOpcode = 0x440070, CheckNameOpcode = 0x440071, RandomNameOpcode = 0x440013, DeleteOpcode = 0x4400CB, PlayerLoginOpcode = 0x440016;
    public const uint CheckNameResultOpcode = 0x47001B, RandomNameResultOpcode = 0x47001C, CreateCharOpcode = 0x4701AC, DeleteCharOpcode = 0x4701AD;
    public const uint LoginFailedOpcode = 0x4701B0, RestrictionsOpcode = 0x470019, RecentAllyReleaseOpcode = 0x470363;
    public const uint CharCreateSuccess = 24, CharCreateFailed = 26, CharCreateNameInUse = 27, CharDeleteSuccess = 67, CharDeleteFailed = 68;
    public const uint CharNameSuccess = 96, CharNameFailure = 97;
    public const byte LoginFailedNoWorld = 1;
    public const int SuperDistrict = 2;

    // Player GUID: high = HighGuid::Player(2) << 58 | realm 70 << 42; low = counter.
    public static byte[] PlayerGuid(ulong counter)
    {
        var guid = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(guid, counter);
        BinaryPrimitives.WriteUInt64LittleEndian(guid.AsSpan(8), (2UL << 58) | (70UL << 42));
        return guid;
    }
    public static ulong Counter(byte[] guid) => BinaryPrimitives.ReadUInt64LittleEndian(guid);
    public static ForeverPacketWriter Guid(this ForeverPacketWriter writer, byte[] guid)
    {
        ushort mask = 0; var present = new List<byte>();
        for (var i = 0; i < 16; i++) if (guid[i] != 0) { mask |= (ushort)(1 << i); present.Add(guid[i]); }
        return writer.U8((byte)mask).U8((byte)(mask >> 8)).Raw(present.ToArray());
    }

    public static (uint sequence, string name) ReadCheckName(byte[] payload)
    {
        var reader = new ForeverPacketReader(payload); var sequence = reader.U32();
        var nameLength = (int)reader.Bits(6); reader.Bits(3); var surnameLength = (int)reader.Bits(6);
        var name = reader.Text(nameLength); reader.Text(surnameLength);
        return (sequence, name);
    }

    public static ForeverCharacter ReadCreate(byte[] payload)
    {
        var reader = new ForeverPacketReader(payload);
        var nameLength = (int)reader.Bits(6); var template = reader.Bit(); reader.Bit(); reader.Bit(); reader.Bit(); reader.Bits(2);
        var surnameLength = (int)reader.Bits(6);
        var character = new ForeverCharacter { Race = reader.U8(), Class = reader.U8(), Sex = reader.U8() };
        var count = reader.U32();
        if (count > ForeverCharacterStore.MaxCustomizations) throw new FormatException("Too many customizations.");
        reader.I32(); reader.I32();                                  // unknown (-1), TimerunningSeasonID
        character.Name = reader.Text(nameLength); character.Surname = reader.Text(surnameLength);
        if (template) reader.U32();
        var choices = new List<(uint option, uint choice)>();
        for (var i = 0; i < count; i++) choices.Add((reader.U32(), reader.U32()));
        character.Customizations = choices.OrderBy(c => c.option).SelectMany(c => new[] { c.option, c.choice }).ToArray();
        if (character.Name.Length > 0) character.Name = char.ToUpperInvariant(character.Name[0]) + character.Name[1..].ToLowerInvariant();
        (character.Map, character.Zone, character.X, character.Y, character.Z) = ForeverWorldPackets.StartPosition(character.Race);
        character.CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return character;
    }

    public static byte[] CheckNameResult(uint sequence, uint result) => new ForeverPacketWriter().U32(sequence).U32(result).ToArray();
    public static byte[] RandomNameResult(string name) { var text = Encoding.UTF8.GetBytes(name); return new ForeverPacketWriter().Bit(true).Bits((uint)text.Length, 6).Flush().Raw(text).ToArray(); }
    public static byte[] CreateChar(uint code, byte[] guid) => new ForeverPacketWriter().U32(code).Guid(guid ?? new byte[16]).ToArray();
    public static byte[] DeleteChar(uint code) => new ForeverPacketWriter().U32(code).ToArray();
    public static byte[] LoginFailed(byte code) => new ForeverPacketWriter().U8(code).ToArray();

    private static readonly string[] Syllables = { "ar", "bel", "cor", "dra", "el", "fen", "gar", "hal", "ith", "jor", "kal", "lor", "mor", "nar", "or", "pel", "quin", "ros", "syl", "tor", "ul", "vel", "wyn", "zar" };
    public static string RandomName(ForeverCharacterStore store)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var parts = Enumerable.Range(0, 2 + RandomNumberGenerator.GetInt32(2)).Select(_ => Syllables[RandomNumberGenerator.GetInt32(Syllables.Length)]);
            var name = string.Concat(parts); name = char.ToUpperInvariant(name[0]) + name[1..];
            if (ForeverCharacterStore.ValidName(name) && !store.NameTaken(name)) return name;
        }
        return "Sandbox";
    }

    // Character select entry (CharacterInfoBasic); the client lists characters as realmless regionwide entries.
    public static ForeverPacketWriter Basic(this ForeverPacketWriter writer, ForeverCharacter character, int listPosition)
    {
        var name = Encoding.UTF8.GetBytes(character.Name); var surname = Encoding.UTF8.GetBytes(character.Surname ?? "");
        writer.Guid(PlayerGuid(character.Counter)).U32(BgsRealmV2.RealmAddress).U16((ushort)listPosition)
            .U8(character.Race).U8(character.Sex).U8(character.Class).I16(0).U32((uint)(character.Customizations.Length / 2)).U8(character.Level)
            .I32(character.Map).I32(character.Zone).F32(character.X).F32(character.Y).F32(character.Z)
            .U64(0).Guid(new byte[16])                                                           // club member id, no guild
            .U32(0).U32(0).U32(0).U32(0).U8(0)                                                    // flags 1..4, can't-login reason
            .U32(0).U32(0).U32(0);                                                                // no pet
        for (var slot = 0; slot < 19; slot++) writer.U32(0).U32(0).U8(0).U8(0).U32(0).U32(0).I32(0).U8(0);
        writer.I32(49).I64(character.CreateTime).I64(character.CreateTime).I32(0)                  // save version, create/last active, last login build
            .I32(0).I32(0).I32(0).I32(0).I32(0)                                                   // personal tabard
            .U32(0).U32(0).I32(0).U32(0).U32(0).I32(SuperDistrict);                               // professions, timerunning, select screen, queue, super district
        foreach (var value in character.Customizations) writer.U32(value);
        return writer.Bits((uint)name.Length, 6).Bits((uint)surname.Length, 6).Bit(character.FirstLogin).Bit(true).Bit(false).Flush().Raw(name).Raw(surname);
    }

    public static byte[] Restrictions(IReadOnlyList<ForeverCharacter> characters)
    {
        var writer = new ForeverPacketWriter().U32((uint)characters.Count);
        foreach (var character in characters) writer.Bit(false).Bit(false).Flush().Guid(PlayerGuid(character.Counter)).U32(0).U32(10);
        return writer.ToArray();
    }

    // The 70338 client keeps a populated list pending until this (empty recent-allies) packet arrives.
    public static byte[] RecentAllyRelease() => new ForeverPacketWriter().U32(0x08ECBD10).U8(7).U32(0).ToArray();
}
