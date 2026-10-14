"""Generate ForeverUpdateFields.g.cs: create-time update-field serializers for 70338.

Mechanical translation of the WriteCreate functions in the pinned reference
(advocaite/TrinityCore 5a125dfe, GPL-2.0-or-later) UpdateFields.cpp/.h into C#
that reads every value from a UfValues bag (defaults: 0, empty, absent). The wire
order is the reference's; only the four player structures and what they nest are
emitted. Usage: python -I gen_update_fields.py
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UPD = ROOT / "tools/reference/src/server/game/Entities/Object/Updates"   # tools/Fetch-Reference.ps1
OUT = ROOT / "src/ForeverSandbox.Server/WorldServer/Network/ForeverUpdateFields.g.cs"
ROOTS = ["ObjectData", "UnitData", "PlayerData", "ActivePlayerData", "GameObjectData"]
SCALARS = {"int8": "I8", "uint8": "U8", "int16": "I16", "uint16": "U16", "int32": "I32", "uint32": "U32",
           "int64": "I64", "uint64": "U64", "float": "F32", "bool": "U8"}
CAST = {"I8": "(sbyte)", "U8": "(byte)", "I16": "(short)", "U16": "(ushort)", "I32": "(int)", "U32": "(uint)", "I64": "", "U64": "(ulong)"}
# Packet-common types the player structures embed, written with their reference defaults (empty lists).
SPECIAL = {
    "WorldPackets::MythicPlus::DungeonScoreSummary": "w.F32(0).F32(0).U32(0);",
    "WorldPackets::MythicPlus::DungeonScoreData": "w.U32(0).I32(0);",
    # Timestamp<> AvailableUntil (int64) first, then 10 int32, then Disabled / DoesNotExpire bits (PerksProgramPacketsCommon.cpp).
    "WorldPackets::PerksProgram::PerksVendorItem": "w.I64(0); for (var k = 0; k < 10; k++) w.I32(0); w.Bit(false).Bit(false).Flush();",
    "TaggedPosition<Position::XYZ>": "w.F32(v.F({p}+\".X\")).F32(v.F({p}+\".Y\")).F32(v.F({p}+\".Z\"));",
    # int32 ItemID, ItemModList (7-bit count, flush), optional ItemBonus bit (flush)
    "WorldPackets::Item::ItemInstance": "w.I32((int)v.I({p}+\".ItemID\")).Bits(0, 7).Flush().Bit(false).Flush();",
}


def split_args(text):
    parts, depth, current = [], 0, ""
    for ch in text:
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append(current.strip())
            current = ""
        else:
            current += ch
    if current.strip():
        parts.append(current.strip())
    return parts


def parse_header():
    text = (UPD / "UpdateFields.h").read_text(encoding="utf-8")
    structs = {}
    for match in re.finditer(r"^struct (\w+) : public IsUpdateFieldStructureTag[^\n]*\n\{\n(.*?)^\};", text, re.S | re.M):
        name, body = match.group(1), match.group(2)
        fields = {}
        for line in body.splitlines():
            line = line.split("//")[0].strip()
            m = re.match(r"(UpdateField|UpdateFieldArray|DynamicUpdateField|OptionalUpdateField|MapUpdateField)<(.+)> (\w+);$", line)
            if m:
                kind, args, field = m.group(1), split_args(m.group(2)), m.group(3)
                fields[field] = (kind, args[0], args[1] if kind == "MapUpdateField" else None)
                continue
            m = re.match(r"(?:std::array<(.+), (\d+)>|([\w:<>]+(?:::\w+)?)) (\w+)(?: = [^;]+)?;$", line)
            if m and not line.startswith(("using ", "struct ", "static ")):
                if m.group(1):
                    fields[m.group(4)] = ("Array", m.group(1), None)
                else:
                    fields[m.group(4)] = ("Plain", m.group(3), None)
        structs[name] = fields
    return structs


def parse_creates():
    text = (UPD / "UpdateFields.cpp").read_text(encoding="utf-8")
    bodies = {}
    for match in re.finditer(r"^void (\w+)::WriteCreate\([^)]*\) const\n\{\n(.*?)^\}", text, re.S | re.M):
        bodies[match.group(1)] = match.group(2)
    return bodies


class Gen:
    def __init__(self, structs, bodies):
        self.structs, self.bodies = structs, bodies
        self.emitted, self.queue, self.out, self.problems = set(), list(ROOTS), [], []

    def struct_of(self, type_name):
        t = type_name.replace("UF::", "").strip()
        return t if t in self.bodies else None

    def field(self, struct, name):
        return self.structs.get(struct, {}).get(name)

    def path(self, ctx, expr):
        """Map a field reference (Name, Name[i], (*Name)[i], Name->X, ViewerDependentValue<NameTag>) to a C# path."""
        expr = expr.strip()
        m = re.fullmatch(r"ViewerDependentValue<(\w+)Tag>::GetValue\(this, receiver, owner\)", expr)
        if m:
            expr = m.group(1)
        expr = ctx["aliases"].get(expr, expr)
        m = re.fullmatch(r"\(\*(\w+)\)\[(\w+)\]", expr)
        if m:
            name = ctx["aliases"].get(m.group(1), m.group(1))
            return f'p + ".{name}[" + {m.group(2)} + "]"', name
        m = re.fullmatch(r"\*?(\w+)(?:\[(\w+)\])?(?:\[(\w+)\])?(?:(?:->|\.)(\w+))?", expr)
        if not m:
            return None, None
        name = ctx["aliases"].get(m.group(1), m.group(1))
        result = f'p + ".{name}'
        for index in (m.group(2), m.group(3)):
            if index:
                result += f'[" + {index} + "]'
        if m.group(4):
            result += f'.{m.group(4)}'
        return result + '"', name

    def count(self, ctx, expr):
        m = re.fullmatch(r"\(?\*?(\w+)\)?(?:\[(\w+)\])?(?:->|\.)size\(\)", expr.strip())
        if not m:
            return None
        name = ctx["aliases"].get(m.group(1), m.group(1))
        sub = f'p + ".{name}' + (f'[" + {m.group(2)} + "]' if m.group(2) else "") + '"'
        info = self.field(ctx["struct"], name)
        if name in STRING_LOCALS or info and info[1] == "std::string":
            return f'Encoding.UTF8.GetByteCount(v.S({sub}))'
        return f'v.N({sub})'

    def value(self, ctx, cast, expr):
        method = SCALARS[cast]
        expr = expr.strip()
        if re.fullmatch(r"-?\d+(\.\d+)?f?", expr):
            return f"w.{method}({'0' if method != 'F32' else '0f'});" if float(expr.rstrip('f')) == 0 else f"w.{method}({CAST.get(method, '')}{expr});"
        count = self.count(ctx, expr)
        if count:
            return f"w.{method}({CAST.get(method, '')}{count});"
        path, _ = self.path(ctx, expr)
        if path is None:
            return None
        if method == "F32":
            return f"w.F32(v.F({path}));"
        return f"w.{method}({CAST[method]}v.I({path}));"

    def cond(self, ctx, expr):
        expr = expr.strip()
        if expr == "ClassicOpcodes::IsBuild70291OrLater()":
            return "true"  # 70338 is a 70291+ build
        m = re.fullmatch(r"fieldVisibilityFlags\.HasFlag\((.+)\)", expr)
        if m:
            flags = [f.strip().split("::")[-1] for f in m.group(1).split("|")]
            return "v.Vis(" + " | ".join(f"UfValues.{f}" for f in flags) + ")"
        m = re.fullmatch(r"(\w+)\.has_value\(\)", expr)
        if m:
            return f'v.Has(p + ".{m.group(1)}")'
        m = re.fullmatch(r"(\w+) == (\d+)", expr)
        if m:
            return f'v.I(p + ".{m.group(1)}") == {m.group(2)}'
        m = re.fullmatch(r"(\w+)", expr)
        if m:
            return f'v.I(p + ".{m.group(1)}") != 0'
        return None

    def write_field(self, ctx, expr, line):
        """data << Name; / data << *Name; - serialize by declared type."""
        name = expr.lstrip("*").strip()
        if name == "ObjectGuid::Empty":
            return "w.Guid(UfValues.EmptyGuid);"
        m = re.fullmatch(r"(\w+)(?:\[(\w+)\])?", name)
        if not m:
            return None
        info = self.field(ctx["struct"], m.group(1))
        if not info:
            return None
        sub = f'p + ".{m.group(1)}' + (f'[" + {m.group(2)} + "]' if m.group(2) else "") + '"'
        type_name = info[1]
        if type_name == "ObjectGuid":
            return f'w.Guid(v.G({sub}));'
        if type_name in SPECIAL:
            return SPECIAL[type_name].replace("{p}", sub)
        return None

    def emit_struct(self, struct):
        self.emitted.add(struct)
        ctx = {"struct": struct, "aliases": {}}
        body = self.bodies[struct]
        lines = [l.split("//")[0].rstrip() for l in body.splitlines()]
        out = [f"    // {struct}::WriteCreate", f"    public static void {struct}(ForeverPacketWriter w, UfValues v, string p)", "    {"]
        indent = 2
        for raw in lines:
            line = raw.strip()
            if not line:
                continue
            pad = "    " * indent
            if line == "{":
                out.append(pad + "{"); indent += 1; continue
            if line == "}":
                indent -= 1; out.append("    " * indent + "}"); continue
            if line == "else":
                out.append(pad + "else"); continue
            m = re.fullmatch(r"for \(uint32 (\w+) = (\d+); \1 < (.+); \+\+\1\)", line)
            if m:
                bound = m.group(3).strip()
                n = bound if bound.isdigit() else self.count(ctx, bound)
                if n is None:
                    self.problem(struct, line); n = "0"
                out.append(pad + f"for (var {m.group(1)} = {m.group(2)}; {m.group(1)} < {n}; {m.group(1)}++)"); continue
            m = re.fullmatch(r"if \((.+)\)", line)
            if m:
                c = self.cond(ctx, m.group(1))
                if c is None:
                    self.problem(struct, line); c = "false"
                out.append(pad + f"if ({c})"); continue
            m = re.fullmatch(r"ViewerDependentValue<(\w+)Tag>::value_type (\w+) = \{\};", line)
            if m:
                ctx["aliases"][m.group(2)] = m.group(1); continue
            if re.fullmatch(r"\w+ = ViewerDependentValue<\w+Tag>::GetValue\(this, receiver, owner\);", line):
                continue
            m = re.fullmatch(r"data << (u?int(?:8|16|32|64)|float)\((.+)\);", line)
            if m:
                s = self.value(ctx, m.group(1), m.group(2))
                if s is None:
                    s = MANUAL.get(line)
                if s is None:
                    self.problem(struct, line); continue
                out.append(pad + s); continue
            m = re.fullmatch(r"data << (\*?[\w:]+(?:\[\w+\])?);", line)
            if m:
                s = self.write_field(ctx, m.group(1), line) or MANUAL.get(line)
                if s is None:
                    self.problem(struct, line); continue
                out.append(pad + s); continue
            m = re.fullmatch(r"data\.WriteBit\((.+)\);", line)
            if m:
                c = self.cond(ctx, m.group(1))
                if c is None:
                    self.problem(struct, line); continue
                out.append(pad + f"w.Bit({c});"); continue
            m = re.fullmatch(r"data\.WriteBits\((.+), (\d+)\);", line)
            if m:
                expr, bits = m.group(1).strip(), m.group(2)
                plus = 0
                if expr.endswith(" + 1"):
                    expr, plus = expr[:-4], 1
                n = self.count(ctx, expr)
                if n is None:
                    path, _ = self.path(ctx, expr)
                    n = f"v.I({path})" if path else None
                if n is None:
                    self.problem(struct, line); continue
                out.append(pad + f"w.Bits((uint)({n}{' + 1' if plus else ''}), {bits});"); continue
            if line == "data.FlushBits();":
                out.append(pad + "w.Flush();"); continue
            m = re.fullmatch(r"data << WorldPackets::Sized(C?)String::Data\(\*?(\w+(?:\[\w+\])?)\);", line)
            if m:
                path, _ = self.path(ctx, m.group(2))
                if path is None:  # locals such as the PlayerData surname
                    path = f'p + ".{m.group(2)}"'
                if m.group(1):  # SizedCString: text + NUL only when non-empty (the size field still says length + 1)
                    out.append(pad + f"{{ var text = v.S({path}); if (text.Length > 0) w.Raw(Encoding.UTF8.GetBytes(text)).U8(0); }}")
                else:
                    out.append(pad + f"w.Raw(Encoding.UTF8.GetBytes(v.S({path})));")
                continue
            m = re.fullmatch(r"WriteMapFieldCreate\((\w+), data, receiver, owner\);", line)
            if m:
                out.append(pad + f'w.U32((uint)v.N(p + ".{m.group(1)}")); // map entries are not emitted (sandbox: empty)'); continue
            m = re.fullmatch(r"(\w+)(?:\[(\w+)\])?(?:\[(\w+)\])?(\.|->)WriteCreate\(data, receiver, owner\);", line)
            if m:
                name = m.group(1)
                info = self.field(struct, name)
                element = info[1] if info else ""
                inner = re.fullmatch(r"DynamicUpdateFieldBase<(.+)>", element)
                target = self.struct_of(inner.group(1) if inner else element) if info else None
                if target is None:
                    self.problem(struct, line); continue
                sub = f'p + ".{name}' + "".join(f'[" + {i} + "]' for i in (m.group(2), m.group(3)) if i) + '"'
                out.append(pad + f"{target}(w, v, {sub});")
                if target not in self.emitted and target not in self.queue:
                    self.queue.append(target)
                continue
            s = MANUAL.get(line)
            if s is not None:
                out.append(pad + s); continue
            self.problem(struct, line)
        out.append("    }")
        self.out.append("\n".join(out))

    def problem(self, struct, line):
        self.problems.append(f"{struct}: {line}")

    def run(self):
        while self.queue:
            struct = self.queue.pop(0)
            if struct not in self.emitted:
                self.emit_struct(struct)
        return self.out, self.problems


STRING_LOCALS = {"surname"}
# Lines whose C++ expressions are not plain field references; translated by hand.
MANUAL = {
    # Classic super-district block (4 x uint32 + int8), twice in PlayerData; first uint32 = ruleset (Normal = 2).
    "data << uint32(ClassicCharacterSuperDistrictID()) << uint32(0) << uint32(0) << uint32(0) << int8(0);": 'w.U32((uint)v.I(p + ".ClassicSuperDistrictID")).U32(0).U32(0).U32(0).I8(0);',
    "std::string const surname = sCharacterCache->GetCharacterSurnameByGuid(owner->GetGUID());": "// surname: value path p.surname",
    # Crafting-order items: only inside loops/optionals that are empty for sandbox characters.
    "data << *RecraftItemInfo;": 'throw new System.NotSupportedException("RecraftItemInfo");',
    "data << Enchantments[i];": 'throw new System.NotSupportedException("CraftingOrder enchantments");',
    "data << Gems[i];": 'throw new System.NotSupportedException("CraftingOrder gems");',
    "data << *OutputItemData;": 'throw new System.NotSupportedException("OutputItemData");',
    "data << float(ModCastingSpeedNeg != 0.0f ? float(ModCastingSpeedNeg) : 1.0f);": 'w.F32(v.F(p + ".ModCastingSpeedNeg") != 0f ? v.F(p + ".ModCastingSpeedNeg") : 1f);',
    "data << int32(Type == 1 ? 4 : *Type);": 'w.I32(v.I(p + ".Type") == 1 ? 4 : (int)v.I(p + ".Type"));',
}


def main():
    structs, bodies = parse_header(), parse_creates()
    methods, problems = Gen(structs, bodies).run()
    header = ("// <auto-generated> by tools/gen_update_fields.py from the pinned reference\n"
              "// advocaite/TrinityCore 5a125dfe UpdateFields.cpp/.h (GPL-2.0-or-later): create-time field order\n"
              "// for WoW Forever 1.60.1.70338. Do not edit by hand; regenerate. </auto-generated>\n"
              "using System.Text;\n\nnamespace AuthServer.Network;\n\ninternal static partial class ForeverUpdateFields\n{\n")
    OUT.write_text(header + "\n\n".join(methods) + "\n}\n", encoding="utf-8")
    print(f"structs={len(methods)} problems={len(problems)}")
    for problem in problems:
        print("  " + problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
