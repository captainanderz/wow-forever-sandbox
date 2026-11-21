# Forever Capture

`ForeverCapture.exe` (`src/ForeverCapture`) records the game objects the **live** WoW Forever server sends to your own game:
doors, chests, levers, signs and every other object that is not in the client's files. The result is `gameobjects.json`, one
entry per placed object with its map, position, rotation, scale, model (display ID), type and state.

**It breaks Blizzard's terms of use.** It never opens, reads or changes the game process and nothing in it tries to hide, but
the login passes through a program that is not the game. Use an account you can afford to lose.

## How it works

1. **Login relay.** The game is started with a copy of its own config whose login address is `127.0.0.1:8010`. The relay
   accepts it with the sandbox's loopback certificate and passes every byte on to the real login server over its own TLS
   connection, which is validated the normal Windows way. While relaying it reads two 32-byte values out of the realm join: the
   game's secret and the server's secret. Every world connection of that login derives its encryption key from them. No login
   traffic is written to disk. In the sandbox's own login the password goes to the web login address the server names, not
   over this connection; the relay passes the resulting login ticket along in memory. That has not been confirmed on a live
   login yet.
2. **Network recording.** With [Npcap](https://npcap.com) installed and open to every user (its "administrators only"
   option off), the tool records the world port (3724) itself, without administrator rights, and decodes as it goes: every
   3 seconds it rewrites `%LOCALAPPDATA%\ForeverCapture\live.json` with where the character is, this session's objects and
   its path. Without Npcap, Windows' own packet monitor (`pktmon`) records instead: Windows asks for administrator rights
   once at the start and once at the end, and there is no live position. Either way the game itself runs without
   administrator rights.
3. **Decode.** The world connections are picked out of the recording, decrypted with the keys from step 1, unpacked, and
   scanned for game object create blocks. The scan needs no message numbers, so a client build that only renumbers its
   messages still decodes.

## Use

```powershell
.\scripts\Build.ps1
.\artifacts\bin\ForeverCapture\release\ForeverCapture.exe check     # certificate, relay and login server reachable; starts nothing
.\artifacts\bin\ForeverCapture\release\ForeverCapture.exe record --map "<WoW Atlas site folder>"   # relay, recording, game, map window
.\artifacts\bin\ForeverCapture\release\ForeverCapture.exe map --site "<WoW Atlas site folder>"     # the map window alone
```

Log in and play as usual. The server only sends objects near your character, so walk every room of the places you want.
Close the game to finish. `--wow <folder>` names another World of Warcraft folder, `--out <folder>` another capture folder,
and `--relay-only` skips the recording (to try the login alone).

`--all-ports` records all TCP instead of only port 3724. Use it only if a capture reports fewer world connections than
expected: it grows by more than 100 MB a minute (with the packet monitor, past 16 GB the oldest packets, the key exchange
among them, are overwritten, so keep such a session under an hour).

## The map window (this PC only)

`--map <site folder>` (and the `map` command on its own) starts a small web server on `http://localhost:5298/` (`LocalMap.cs`)
and opens it in an Edge app window. It serves the WoW Atlas site's own pages and tiles **read-only from the site's folder**
and adds this tool's scripts on top; nothing is written into the site's folder, and the site, its exports, its R2 uploads
and its git history know nothing of the captures.

- **2D** (`LiveMap.js`): a "Captured" row in the map library shows every capture's objects as dots and the character's paths.
  While recording, the window (`?live=1`) follows the character onto its map, shows where it is, this session's objects and
  path, and a 200 yd ring (90% of the first capture's objects were first seen within that distance of the path).
- **3D** (`Objects3D.js`): opening a captured place in 3D draws the captured objects. The server hands the viewer the
  renderer build kept in `artifacts\viewer3d-objects` (the site's renderer plus `tools\viewer3d-objects\patch-viewer3d-objects.ps1`,
  which draws models at given world positions) and answers the objects' model files from the game's own storage
  (GameObjectDisplayInfo for the model, then its skins, textures, animations and skeleton).
  **Server objects**, a button under Scene > layers (only on a map with captured objects), shows or hides them, to tell them
  from the game files' own models; it starts on with every page. The renderer draws them only with the M2 layer on, and the
  viewer starts with it off, so the page switches M2 on once when it has objects to show (turning it off again sticks), and
  again whenever Server objects is switched on. They are drawn on every loaded terrain tile, not only within the M2 view
  distance (at 1×1 an object a few yards past a tile edge would vanish). The page asks for the list again every 5 seconds: while
  recording, the session's `gameobjects.json` is rewritten every few seconds, so new objects appear in an open 3D view.
- Not drawn: objects whose model is a creature model with a skin from the creature tables (they would draw black), objects
  whose model is a WMO, and every object of a WMO-only map (a dungeon), whose coordinates the viewer's building does not
  share yet.
- When the site's renderer changes, the kept build has to be made again from the new renderer tree with the patch, or the
  3D view may break (the 2D window does not depend on it).
- Files the game build lists but never stored on disk (the game streams them while it plays; the crank wheels' textures drew
  black without them) come from Blizzard's CDN (`GameCdn.cs`): the build's merged CDN index on disk gives archive and
  offset, one ranged GET fetches the piece, and it must hash to its encoding key. Only files of captured objects' models are
  fetched, once per map window run, kept in memory.
- No service worker runs on the map window. The site's own one answers `/__viewer3d_missing_asset/...` (how this server
  delivers the objects' model files) with its own 404, and keeps old copies of this tool's scripts. A browser that once got
  the site's `sw.js` on localhost:5298 kept it (Edge keeps a worker whose script turns 404), so `/sw.js` here is a worker
  that deletes this origin's caches, unregisters itself and reloads the pages the old one served. Tested in headless Edge:
  old worker installed, model file 404; first load after it, no worker, 200, no caches; no reload loop on later loads.

`ForeverCapture decode <capture folder>` reads a capture again, for example after a decoder fix.
`ForeverCapture stop` stops a packet-monitor recording that was left running.

## What is left on the PC

- `%LOCALAPPDATA%\ForeverCapture\<time>\`: `gameobjects.json`, `track.json` (your character's own path, read from the
  game's movement packets, to see afterwards where a capture has been), `decode-stats.json` (counts and one line per connection),
  `relay.log` (what the relay saw and when: connection events and request names, never contents), `session.json` (the
  session's secrets) and `world.fwcap` (the game's world connections, still encrypted). Secrets and capture together decrypt
  the whole session, chat included: keep the folder on this PC.
- The raw recording (`net.pcapng`, or `net.etl` from the packet monitor) holds the recorded TCP traffic: only the world
  port's, unless `--all-ports`. It is deleted once at least one world connection has been decrypted, and kept for another
  try when none could be.
- `%LOCALAPPDATA%\ForeverCapture\live.json`: the last session's live position, objects and path (the map window reads it).
- `_forever_\WTF\ForeverCapture.wtf` in the game folder: the config copy. Your normal `Config.wtf` is not changed.
- With the packet monitor, its filter list is replaced by the world port while recording and cleared afterwards.

## Limits

- Only objects the server sent are captured: what your character came near, in the state it had at that moment.
- Objects standing on a moving transport are skipped.
- A realm or ruleset switch made from inside the game is not followed; connections after it show up as "without a key".
- The field layout is the 1.60.1.70888 one. `decode-stats.json` counts sightings that did not match it exactly.

## First live capture (2026-11-16, client 1.60.1.70938)

Seven minutes in and around the City of Dalaran: 8 world connections, all on port 3724; 7 decrypted, among them all 6 instance
connections; 549 placed objects from 652 sightings, every one matching the 70888 layout exactly. The one connection without a
key was the first of two login-screen connections to the same realm: its join never reached the relay's reader. Since then
the reader collects every secret without pairing requests and answers, and writes `relay.log`.

`tests/ForeverCapture.Tests` checks the whole chain against the sandbox server's own writers.
