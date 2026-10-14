"""Build data/hotfixes.json: the TactKey hotfixes the 70338 client waits for.

Facts only (key bytes, push/unique/record IDs) are read from the pinned, hash-verified
reference SQL under tools/reference (tools/Fetch-Reference.ps1; advocaite/TrinityCore 5a125dfe):
publicly known keys (hotfixes_classic_tact_keys), the client-data sync overrides
(hotfixes_classic_sync, build 70009) and three keys from official 70124 sniffs
(hotfixes_forever_sniff). Usage: python -I extract_reference_hotfixes.py [output]
"""
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SQL = ROOT / "tools" / "reference" / "sql" / "custom" / "hotfixes"
SOURCES = ["2026_09_28_01_hotfixes_classic_tact_keys.sql", "2026_09_29_00_hotfixes_classic_sync.sql",
           "2026_10_01_02_hotfixes_forever_sniff.sql"]
TACT_KEY_TABLE = 3744420815  # 0xDF2F53CF
NUMBER_TUPLE = re.compile(r"\(\s*(-?\d+(?:\s*,\s*-?\d+)*)\s*\)")


def tuples(text):
    for match in NUMBER_TUPLE.finditer(text):
        yield [int(value) for value in match.group(1).split(",")]


def main(output):
    keys, pushes, provenance = {}, {}, []
    for name in SOURCES:
        path = SQL / name
        data = path.read_bytes()
        provenance.append({"file": name, "sha256": hashlib.sha256(data).hexdigest()})
        text = data.decode("utf-8")
        for statement in re.split(r";\s*\n", text):
            lowered = statement.lower()
            if "into `tact_key`" in lowered or "into tact_key" in lowered:
                for row in tuples(statement):
                    if len(row) == 18 and all(0 <= value <= 255 for value in row[1:17]):
                        record, build = row[0], row[17]
                        if record not in keys or build >= keys[record][1]:
                            keys[record] = (bytes(row[1:17]), build)
            elif "into `hotfix_data`" in lowered or "into hotfix_data" in lowered:
                for row in tuples(statement):
                    if len(row) == 6 and row[2] == TACT_KEY_TABLE:
                        push, unique, _, record, status, _ = row
                        pushes[(push, record)] = (unique, status)
    records = []
    for (push, record), (unique, status) in sorted(pushes.items()):
        if record not in keys:
            raise ValueError(f"TactKey hotfix {push}/{record} has no key bytes")
        records.append({"push": push, "unique": unique, "table": TACT_KEY_TABLE, "record": record,
                        "status": status, "data": keys[record][0].hex().upper()})
    document = {"description": "TactKey hotfixes for WoW Forever 1.60.1.70338 (sandbox server data).",
                "reference": "https://github.com/advocaite/TrinityCore 5a125dfe7031b8abc2a0e32c487a9351577aff0d",
                "sources": provenance, "records": records}
    Path(output).parent.mkdir(parents=True, exist_ok=True)
    Path(output).write_text(json.dumps(document, indent=1), encoding="utf-8")
    print(json.dumps({"records": len(records), "keys": len(keys), "output": str(output)}))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ROOT / "data" / "hotfixes.json")
