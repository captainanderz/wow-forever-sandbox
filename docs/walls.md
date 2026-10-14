# Impassable walls

`/walls` (or `.walls` in chat) shows where the terrain itself blocks movement.

## Which terrain is impassable

Each ADT map chunk (`MCNK`, 16 x 16 per tile, 33.33 yards wide) has flag `0x2` when the client blocks it as a whole, from its
lowest height upward. `ForeverTerrain.ImpassWalls` reads those flags from the client's own map files and creates one edge per
side of every impassable chunk around the player:

- **yellow** where the neighbour is walkable, or at the edge of the 64 x 64 tile grid;
- **red** where the neighbour is impassable too, or has no terrain at all.

Neighbours across tile seams come from the next tile. The walls are recomputed when the player enters another tile or map.

## How they are shown

| Command | Result |
| --- | --- |
| `.walls` / `.walls on` | real game objects: the game renders them, so terrain, buildings and units in front hide them |
| `.walls glass faint\|light\|medium\|strong` | wall tint (default `strong`) |
| `.walls look <yellow> <red> [scale]` | game force-field models instead of the sandbox models |
| `.walls overlay` | the launcher draws them over the game window instead |
| `.walls off` | hide them |

### Game objects

The server sends ordinary `SMSG_UPDATE_OBJECT` create blocks for generic game objects (`ForeverGameObjects.cs`):

- a stationary position and packed rotation;
- object and game-object update fields;
- GUIDs in a sandbox-only entry range.

Removals use the out-of-range list. The client forgets all objects on a far teleport, so the server forgets them too.

### The sandbox's own wall models

The client can only show models that are in its storage. `ForeverSnapshot walls install` adds the sandbox's models to the
snapshot (`scripts/Publish.ps1` does this automatically).

- **Models.** `src/ForeverSnapshot/WallModels.cs` generates small M2 (version 272) models. Each is a translucent plane from
  the chunk floor up with solid edge lines, in yellow and red for each tint.
- **Storage.** The files get FileDataIDs far above anything the client uses, and are written into a new archive. The client
  finds files through the build's TVFS manifest, so the install also writes a new TVFS root with entries for the added files
  under the enUS mount. The build and encoding configuration lines stay unchanged.
- **Check.** The install reads every file back through the same path the client uses.
- **Undo.** `walls remove` takes everything out again, and `walls status` reports what is installed.

The server offers `GameObjectDisplayInfo` rows for those models as hotfixes (display IDs from 900001). It only places them
when the snapshot's `Data\sandbox-files.json` lists them. Otherwise it falls back to force-field models.

### Overlay

`.walls overlay` makes the server write `impass-walls.json` to its state folder. The launcher (`src/ForeverSandbox/Walls.cs`)
then draws the walls with Direct3D 11 into a click-through, transparent DirectComposition window over the game. It uses the
game's own camera, reading the render view's view and projection matrices from the running client every frame. Nothing in the
game is written. The overlay draws through everything, so it also shows walls behind terrain.
