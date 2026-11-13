# Salmiak

**Feature:** WoW 1.12 world editor. Keys are shown in brackets.

## Modes

* **Sculpt terrain** `[Ctrl+E]`, **Texture paint** `[Ctrl+T]`, **Doodads** `[Ctrl+D]`, **WMOs** `[Ctrl+W]`, **Taxi** `[B]`, **Zones** `[Ctrl+A]`.
* NPCs and Herbs are toolbar buttons. `[Esc]` exits the current mode.

---

## Terrain `(Ctrl+E)`

* **Raise / lower:** `[LMB]` / `[Ctrl+LMB]`. Brush size `[Ctrl+scroll]`, strength and falloff `[middle-click]`.
* **Flatten to clicked height** `[Ctrl+F]`, **smooth** `[Ctrl+B]`, **noise** `[Ctrl+K]`.
* **Holes:** punch `[Ctrl+H]`, fill back `[Ctrl+LMB]`.
* **Shadows** `[Ctrl+I]` / `[Ctrl+M]`: one toolbar button, two faces. The left sidebar then swaps in (like the Doodads/WMO drawers) and holds a **Brush / Raycast** radio, the bake options, a **Bake Shadows** button and a progress bar.
  * **Raycast bake** `[Ctrl+I]`: click chunks to add them to the bake list, `[Shift+drag]` adds a block, `Shift+C` picks every chunk of the tile under the cursor, `C` resets the list. The sidebar button bakes exactly those chunks (with nothing picked it bakes the edited tiles), the bar counts chunks as they finish, and the selection survives so `[Ctrl+Z]` + retuning the sun bakes the same chunks again. While chunks are being picked nothing sculpts, so a selection click never lifts the terrain.
  * **Shadow brush** `[Ctrl+M]` (erase `[Ctrl+LMB]`): paint baked shadow by hand, e.g. ghost a doorway or brighten a seam.
  * Rays are traced against real WMO and M2 triangles, so shadows match what you see. The sidebar sets sun height/rotation, shadow length (default 250 yd ≈ 7.5 chunks, which is what lets a tall tree's crown reach the ground; WoW Viewer's own 150 clips it), the alpha cutoff (alpha ≥ threshold blocks), softness, hit limit and jitter; the defaults (`height 30°`, `rotation 34°`, `alpha 200`, `length 250 yd`) are the ones that reproduce the shadows Blizzard baked in the 1.12 client. Water is not a light blocker: lake beds and shores keep the shadow their bank casts on them, as in the client's own `MCSH` payloads. Baking walks the chunks a few at a time between frames, so the window keeps drawing and the bar keeps moving even on a whole map. The Map menu keeps the full bake dialog for whole-map bakes.
* **River / valley carve** `[Ctrl+J]`: drop points, `[Enter]` cuts the bed plus optional water.
* **Heightmap import** (Map menu): stretch a PNG over a marked rectangle.
* **Objects follow the terrain** as you sculpt `[Ctrl+Q]`.

## Texturing `(Ctrl+T)`

* **Multi-layer alpha paint** `[LMB]`, erase that texture `[Ctrl+LMB]`, strip a layer `[X]`.
* **Pick texture** `[N]`, pipette `[P]`, choose layer `[Shift+scroll]`, isolate layer `[I]`, dither `[J]`.
* **Procedural ground fill** `[Ctrl+G]`, **road tool** `[Ctrl+R]`, **slope auto-paint** `[Ctrl+P]`, **chunk-seam blend** `[Ctrl+L]`, copy / paste chunk textures `[Ctrl+C]` / `[Ctrl+V]`.

## Doodads and WMOs `(Ctrl+D / Ctrl+W)`

* **Transform gizmo:** drag the centre to move, rings to rotate, scale handle on doodads. Pixel-accurate picking on `[LMB]`.
* **Asset drawer** with search, thumbnails, and favorites `[N]`; spinning 3D preview `[Shift+click]`.
* **Armed placement:** place `[LMB]`, lift `[PgUp]` / `[PgDn]`, collision toggle `[O]`, random yaw `[R]`, align to ground `[L]`.
* **Copy / paste** `[Ctrl+C]` / `[Ctrl+V]`, delete `[Del]`, context menu `[RMB]`.
* **WMO interior editing** (Map menu): open a WMO on its own, drop furniture into its doodad sets, save back.
* **Export** any doodad M2 to OBJ plus textures (drawer right-click).

## Water `(Ctrl+U)`

* **Click a liquid** to select the whole body; re-level by `[scroll]` (coarse with `[Shift]`) or drag the height gizmo.
* **Place new water** `[Ctrl+N]`, remove `[Del]`, set the type (water, ocean, magma, slime).

## Zones and lighting `(Ctrl+A)`

* **Click a chunk** to inspect its AreaId; pick an id `[middle-click]` or run the new-zone wizard `[N]`; `[Shift+drag]` paints a rectangle. Colour-coded chunk overlay.
* **Force always-night** on a continent's light zones (Map menu).

## Taxi and transports `(B)`

* **Click a path**, drag waypoints, new route / zeppelin / boat `[N]`.
* **Draw by flying:** drop a waypoint at the camera `[MMB]`, passenger stop `[Shift+MMB]`, finish `[Enter]`. Per-waypoint stop / teleport editor on `[middle-click]`, altitude `[PgUp]` / `[PgDn]`.
* **Exports** the taxi DBCs plus flightmaster and transport SQL; a built-in checker catches the cases that crash the taxi map.

## Live server data (toolbar, needs the world DB)

* **NPCs:** spawn the real creature models for the loaded map, with correct skins, hair, and geosets. Click one to see its patrol.
* **Herbs and veins:** gathering nodes from gameobject spawns, classified by Lock.dbc.

## Maps and tiles (Map menu)

* **Build a blank map** from scratch (WDT, Map.dbc, ADTs). Add a tile at the camera to extend a map.

## Export and deploy (Project menu)

* **Export to MPQ:** rebuilds the edited ADTs, patches the WDT, regenerates minimaps, bundles Map.dbc, AreaTable.dbc, and the taxi DBCs. If a tile's only change is its shadow map, the original `.adt` bytes are patched instead of rebuilt — MCSH payloads are rewritten in place, or an MCSH is inserted where a chunk had none — and the result is re-parsed and compared against the edited tile (shadow maps and a chunk's shadow flag aside) before shipping; a tile that fails that check falls back to the full rebuild.
* **Writes** cmangos server .map grids and a placeholder mmap so new maps load. Merge MPQs.
* **Deploy:** validate, merge into the client patch slot, copy DBCs and .map to the server, optional flightmaster SQL apply, optional mangosd restart (saved profiles).
* **Project files (.wmproj)** carry tiles, zones, routes, WMO overrides, and camera. Save `[Ctrl+S]`, load `[Ctrl+O]`.

## View and misc

* **Minimap** `[M]`, **wireframe** `[F]`, **debug views** `[G]`, **doodads on / off** `[T]`, **WMOs on / off** `[Y]`.
* **Undo** `[Ctrl+Z]` / **redo** `[Ctrl+Y]`, **screenshot to clipboard** `[Ctrl+Shift+P]`, **full key sheet** `[F1]`.
* **Custom MPQ reader;** load / unload archives, and the database connection panel (File menu).
