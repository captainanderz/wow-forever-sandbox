// Real spell casts for the sandbox (Blink first). Layouts: pinned SpellPackets.cpp (SpellCastRequest prefix, SpellCastData,
// SpellStart / SpellGo, CastFailed, LearnedSpells), CombatLogPacketsCommon.cpp (SpellCastVisual), CombatPackets.cpp
// (PowerUpdate); flags and failure codes from Spell.h / SharedDefines.h; flow as Spell::prepare / Spell::cast (start, power
// taken, go with the remaining power, effects). Opcodes are retail numbers shifted like the rest of the session.
using System;
using System.Collections.Generic;
using System.Linq;

namespace AuthServer.Network;

internal sealed record ForeverSpell(int Id, string Name, int CooldownMs, int ManaCostPercentOfBase, float LeapDistance);

internal static class ForeverSpells
{
    public const uint CastSpellOpcode = 0x3E0176;                                  // client (reference ClassicClientOpcodes.inc)
    public const uint SpellStartOpcode = 0x69002D, SpellGoOpcode = 0x69002C, CastFailedOpcode = 0x69004A, LearnedSpellsOpcode = 0x690040;   // instance
    public const uint PowerUpdateOpcode = 0x47017E;                                // realm (retail 0x45017E, Classic index - 1, then + 1)
    public const uint CastFlagHasTrajectory = 0x2, CastFlagUnknown9 = 0x100, CastFlagPowerLeftSelf = 0x800, CastFlagFromClient = 0x40000;
    public const uint TargetFlagDestLocation = 0x40;
    public const int FailedNotKnown = 87, FailedNotReady = 91, FailedNoPower = 108, FailedUnavailable = 128;
    public const byte PowerMana = 0;

    // Classic Blink: 15 s cooldown, 21% of base mana, 20-yard leap (TARGET_DEST_CASTER_FRONT_LEAP + SPELL_EFFECT_LEAP).
    public static readonly Dictionary<int, ForeverSpell> Implemented = new() { [1953] = new ForeverSpell(1953, "Blink", 15000, 21, 20f) };

    // Class spells a fresh character has (sandbox): Mages know Blink.
    public static int[] ClassSpells(byte playerClass) => playerClass == 8 ? new[] { 1953 } : Array.Empty<int>();

    public static uint[] KnownSpells(ForeverCharacter c) =>
        ForeverPlayerObject.Languages(c.Race).Select(l => l.spell).Concat(ClassSpells(c.Class).Concat(c.LearnedSpells ?? new List<int>()).Select(s => (uint)s)).Distinct().ToArray();

    public sealed record CastRequest(byte[] CastId, int SpellId, int SpellXSpellVisualId, int ScriptVisualId);

    // CMSG_CAST_SPELL prefix: packed cast GUID, uint8 send flags, 3 x int32 misc, int32 spell, visual (2 x int32); the rest
    // (targets, trajectory, crafting) is not needed for self / caster-relative spells.
    public static CastRequest ReadCastRequest(byte[] payload)
    {
        var reader = new ForeverPacketReader(payload);
        var castId = reader.PackedGuid(); reader.U8(); reader.I32(); reader.I32(); reader.I32();
        return new CastRequest(castId, reader.I32(), reader.I32(), reader.I32());
    }

    // SpellCastData (SpellStart / SpellGo).
    public static ForeverPacketWriter CastData(ForeverPacketWriter w, byte[] caster, CastRequest request, uint flags, (float x, float y, float z)? destination,
        IReadOnlyList<byte[]> hitTargets, int? remainingMana)
    {
        var empty = UfValues.EmptyGuid;
        w.Guid(caster).Guid(caster).Guid(request.CastId).Guid(empty).I32(request.SpellId).I32(request.SpellXSpellVisualId).I32(request.ScriptVisualId)
            .U32(flags).U32(0).U32(0).U32(0);                                                       // flags, flags ex, ex2, cast time (instant)
        // SpellTargetData: flags, unit, item, housing GUIDs, bits (housing resident, src, dst, orientation, map, 7-bit name), dst.
        w.U32(destination != null ? TargetFlagDestLocation : 0).Guid(empty).Guid(empty).Guid(empty)
            .Bit(false).Bit(false).Bit(destination != null).Bit(false).Bit(false).Bits(0, 7).Flush();
        if (destination is { } d) w.Guid(empty).F32(d.x).F32(d.y).F32(d.z);
        w.U32(0).F32(0)                                                                             // missile trajectory
            .I32(0).U8(0)                                                                           // ammo display, dest loc cast index
            .I32(0).I32(0)                                                                          // immunities
            .I32(0).U32(0).Guid(empty);                                                             // heal prediction
        var powers = remainingMana != null ? 1 : 0;
        w.Bits((uint)hitTargets.Count, 16).Bits(0, 16).Bits((uint)hitTargets.Count, 16).Bits(0, 16).Bits((uint)powers, 9).Bit(false).Bits(0, 16).Flush();
        foreach (var target in hitTargets) w.Guid(target);
        foreach (var _ in hitTargets) w.U8(0);                                                     // SPELL_MISS_NONE
        if (remainingMana is { } mana) w.I8((sbyte)PowerMana).I32(mana);
        return w;
    }

    public static byte[] SpellStart(byte[] caster, CastRequest request, int mana) =>
        CastData(new ForeverPacketWriter(), caster, request, CastFlagHasTrajectory | CastFlagPowerLeftSelf | CastFlagFromClient, null, Array.Empty<byte[]>(), mana).ToArray();

    public static byte[] SpellGo(byte[] caster, CastRequest request, int mana, (float x, float y, float z)? destination) =>
        CastData(new ForeverPacketWriter(), caster, request, CastFlagUnknown9 | CastFlagPowerLeftSelf | CastFlagFromClient, destination, new[] { caster }, mana)
            .Bit(false).Flush().ToArray();                                                          // no combat log data

    public static byte[] CastFailed(CastRequest request, int reason) =>
        new ForeverPacketWriter().Guid(request.CastId).I32(request.SpellId).I32(request.SpellXSpellVisualId).I32(request.ScriptVisualId).I32(reason).I32(0).I32(0).Guid(UfValues.EmptyGuid).ToArray();

    // SMSG_POWER_UPDATE: GUID, uint32 count, (uint8 power type, int32 value).
    public static byte[] PowerUpdate(byte[] guid, byte type, int value) => new ForeverPacketWriter().Guid(guid).U32(1).U8(type).I32(value).ToArray();

    // SMSG_LEARNED_SPELLS: uint32 count, uint32 specialization, int32 min action bar slot, per spell (int32, 4 bits), 2 bits.
    public static byte[] LearnedSpells(IEnumerable<int> spells)
    {
        var list = spells.ToArray();
        var w = new ForeverPacketWriter().U32((uint)list.Length).U32(0).I32(0);
        foreach (var spell in list) w.I32(spell).Bit(false).Bit(false).Bit(false).Bit(false).Flush();
        return w.Bit(false).Bit(false).Flush().ToArray();
    }

    // Classic mana regeneration per 2-second tick outside the five-second rule (spirit 20 for sandbox characters).
    public static int ManaRegenPerTick(byte playerClass, int spirit = 20) => playerClass is 5 or 8 or 9 ? (int)(12.5f + spirit / 4f) : (int)(15f + spirit / 5f);
}
