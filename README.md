# Forever Sandbox

A local, single-player sandbox for the **World of Warcraft "Forever" beta client, build 1.60.1.70338**. It is a standalone
server plus a launcher that starts your own copy of that client against it. Everything runs on your machine, and nothing
talks to Blizzard's game servers.

**Not affiliated with or endorsed by Blizzard Entertainment.** World of Warcraft is a trademark of Blizzard Entertainment, Inc.
This repository contains no Blizzard client files. You bring your own Battle.net install of the beta, and the snapshot tool
copies that exact build into a frozen local folder that Battle.net never updates.

## Features

- Login (Battle.net-style SRP over local TLS), character list and creation, world entry, movement, logout and relog.
- Characters, account data (key bindings, macros, UI layout) and saved locations persist between runs.
- Sandbox chat commands, listed by `.help` in game.
- **No fog:** `/fog off` removes all fog, including the map and scene fog no game setting reaches. See
  [docs/fog.md](docs/fog.md).
- **Impassable walls:** `/walls` shows the terrain the client treats as impassable, as translucent yellow/red walls in the
  world. See [docs/walls.md](docs/walls.md).

### Commands

| Command | Effect |
| --- | --- |
| `.fly [on\|off]` | flying |
| `.speed <n>`, `.flyspeed <n>` | run/swim/fly speed multiplier, flight-only multiplier |
| `.tele <name>` / `.tele <x> <y> <z> [o] [map]` | teleport to a named place or coordinates |
| `.loc [name]` | show your position, or save it as a `.tele` name |
| `.blink [yards]`, `.slowfall [on\|off]` | jump forward (default 20 yards), slow fall |
| `.walls [on\|off\|overlay]`, `.walls glass faint\|light\|medium\|strong` | impassable terrain walls and their tint |
| `.gob <display> [scale]`, `.gob clear` | place a test game object model |
| `/fog off\|on`, `/walls ...` | fog off / back on, walls (SandboxTools addon; `/walls` takes the `.walls` options) |

## Requirements

- Windows 10/11 x64.
- The Battle.net install of the WoW Forever beta at exactly **1.60.1.70338**. The launcher checks the executables' hashes.
- To build: .NET SDK 8.0.301 (pinned in `global.json`). The published product is self-contained.

## Getting started

```powershell
# 1. Build the product folder (launcher, server, tools, data)
.\scripts\Publish.ps1 -Destination "D:\Forever Sandbox"

# 2. Once: freeze your own client install into it
& "D:\Forever Sandbox\ForeverSandbox.exe" snapshot create --from "C:\Blizzard Games\World of Warcraft" --to "D:\Forever Sandbox\client"

# 3. Publish again, so the addon and the wall models go into the snapshot
.\scripts\Publish.ps1 -Destination "D:\Forever Sandbox"
```

Then start `ForeverSandbox.exe` and log in with **admin / admin**.

On first run the launcher creates local login certificates, and Windows asks you to trust a localhost-only root. Entering
the world also needs one opt-in change in the running game's memory: the server's public key goes into the client's key
table. The launcher asks once. No game code is patched, and nothing on disk outside the product folder changes.
[docs/architecture.md](docs/architecture.md) explains each step.

`ForeverSandbox snapshot walls remove --to <product>\client` takes the sandbox's added files out of the snapshot again.

## Development

```powershell
.\scripts\Build.ps1                         # build every project (Release, x64) into artifacts\
.\scripts\Test.ps1 -Client <snapshot>       # protocol and data tests; -Client also checks a real client snapshot
.\scripts\Publish.ps1 -Destination <folder> # build the product folder (default: .\publish)
```

NuGet restores are locked (`packages.lock.json`). Pass `-UpdateLock` to `Build.ps1` or `Test.ps1` after changing package
references.

`ForeverUpdateFields.g.cs` and `data/hotfixes.json` are generated from a pinned TrinityCore reference. To regenerate them, run
`tools/Fetch-Reference.ps1`, then `python -I tools/gen_update_fields.py` or `python -I tools/extract_reference_hotfixes.py`.

## Repository layout

```text
src/
  ForeverSandbox/            launcher (ForeverSandbox.exe): certificates, server and client start, realm key, fog, wall overlay
  ForeverSandbox.Server/     server: Battle.net login, realm list, 1.60.1.70338 world protocol (WorldServer/Network/Forever*.cs)
  ForeverSandbox.Framework/  shared protocol, crypto and Battle.net protobuf types
  ForeverSnapshot/           client snapshot tool and the wall-model installer
  ForeverCertificates/       local loopback certificate tool
tests/ForeverSandbox.Tests/  protocol and data tests
addons/SandboxTools/         in-game addon (/fog, /walls)
data/hotfixes.json           shipped hotfix data (TACT keys)
docs/                        how it works
scripts/                     Build, Test, Publish
tools/                       generators for the files derived from the reference
```

## License

The project is MIT licensed ([LICENSE](LICENSE)). Bundled third-party code keeps its own license
([THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).
