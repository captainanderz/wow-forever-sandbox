# Fog removal

`.fog off` in chat removes all fog. `.fog on` brings it back. Fog in the 1.60.1.70338 client has three sources, and only the
first can be reached through a game setting.

`.fog` is a server command, but the server cannot change the client's settings itself. Its reply is the signal instead: when
the SandboxTools addon sees the system message `Sandbox: fog off.` or `Sandbox: fog on.`, it changes the fog settings. The
launcher then takes care of the other two sources.

| Source | Where | Turned off by |
| --- | --- | --- |
| Volumetric fog setting | graphics flags bit 8 (`volumeFog` CVar) | the addon: `volumeFog`, `volumeFogInterior` and horizon CVars |
| Map-driven volumetric fog | graphics flags bit 9, re-derived every frame from the current map's `Map.Flags[2]` bit 15 (`0x8000`) | the launcher: clears that bit in the in-memory Map table |
| Standard scene fog | main world scene, byte `+0x71F` | the launcher: clears that byte |

## How the launcher does it

`src/ForeverSandbox/Fog.cs` (`FogMonitor`) polls the client the launcher started.

- **Trigger.** When the staged graphics flags (`0x56AF5B4`) show bit 8 clear, which is what the addon does on `.fog off`, it:
  - clears bit 15 of `Map.Flags[2]` (field 25) for the current map, in the client's Map table (database at `0x603AC60`);
  - clears the fog switch at `+0x71F` of the main world scene, found through the scene list at `0x5746910` and checked
    against the scene global `0x7A08CF8`.
- **Restore.** It records every original value. On `.fog on` it puts them back, but only if the Map table and the scene are
  still the same objects.
- **Map changes.** The current map is checked every poll, so fog stays off after a teleport or loading screen.

Addresses are relative to the client image, which is a `MEM_MAPPED` section (not `MEM_IMAGE`). They are only valid for the
pinned `WowB.exe`. The launcher refuses any other build by hash.

`"fullFogOff": false` in `sandbox.json` turns the launcher part off. `.fog off` then only changes the game settings.

## Why data only

Every write goes to committed, private read/write data of the client the launcher started, and only while that client is
paused and re-validated. Executable code is never changed. The client checks its own code. A one-byte code patch removed
the fog, but the game raised `0xC00000E5` and closed about 48 seconds later. The data-only approach ran without problems in
long tests and leaves nothing on disk.

## Known limits

- A map's fog bit can sit in a palette entry shared by several maps. Clearing it then clears it for all of them, which is
  harmless for fog-off and is restored by `.fog on`.
- `Map.Flags[2]` bit 15 may have other uses in the client besides fog. None have been observed.
