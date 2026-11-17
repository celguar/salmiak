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
* **Shadows** `[Ctrl+I]` / `[Ctrl+M]`: one toolbar button, two faces. The left sidebar then swaps in (like the Doodads/WMO drawers) and holds a **Brush / Raycast** radio, the bake options, a **Bake Shadows** button and, right under it, the per-chunk progress bar that turns green once a bake lands.
  * **Raycast bake** `[Ctrl+I]`: click chunks to add them to the bake list, `[Shift+drag]` adds a block, `Shift+C` picks every chunk of the tile under the cursor, `C` resets the list. The sidebar button bakes exactly those chunks (with nothing picked it bakes the edited tiles), the bar counts chunks as they finish, and the selection survives so `[Ctrl+Z]` + retuning the sun bakes the same chunks again. While chunks are being picked nothing sculpts, so a selection click never lifts the terrain.
  * **Shadow brush** `[Ctrl+M]` (erase `[Ctrl+LMB]`): paint baked shadow by hand, e.g. ghost a doorway or brighten a seam.
  * Rays are traced against real WMO and M2 triangles, so shadows match what you see. The sidebar sets sun height/rotation, shadow length (default 250 yd ≈ 7.5 chunks, which is what lets a tall tree's crown reach the ground; WoW Viewer's own 150 clips it), the alpha cutoff (alpha ≥ threshold blocks), softness, hit limit and jitter; the defaults (`height 30°`, `rotation 34°`, `alpha 200`, `length 250 yd`) are the ones that reproduce the shadows Blizzard baked in the 1.12 client. Water is not a light blocker: lake beds and shores keep the shadow their bank casts on them, as in the client's own `MCSH` payloads. The click on **Bake Shadows** empties the bar and prints *Preparing models* right away, so a one-chunk bake can never be read as "the old green bar": the model gather gets a frame of its own, the label then becomes *Baking shadows* and the bar fills blue as chunks land, green when they are all in. Getting ready is not re-done for models the view already holds: the triangles and the alpha-tested BLP pixels of the WMO and M2 models in reach stay between bakes inside their own memory budgets, and each ray only walks the triangles its own path can cross rather than every triangle of every model nearby, which is what keeps a bake next to a large WMO (Ironforge, Orgrimmar, a big tree cluster) in the seconds rather than tens of seconds. The Map menu keeps the full bake dialog for whole-map bakes. Every bake re-decides all 64×64 texels of the chunks it covers, so it also clears hand-painted or stale shadow in those chunks where nothing casts any more; check *Keep existing shadows* only to grow an existing map. Texels are one 64th of a chunk wide and traced at their middle, exactly where the client samples its own 64×64 shadow texture, so a shadow runs unbroken across chunk and tile borders. A *Highlight shadows* checkbox in the sidebar paints shadowed ground dark violet while you work, which makes the outline of a bake readable on grass and dark rock; it is display only, the gamelike look stays the default and exports are unaffected.
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

* **Click a liquid** to select the whole body, as far as the bed lets water through: a neighbour at a
  different height joins the selection wherever the ground under the shared boundary sits under the
  water, so every group a run of clicks left behind comes back as one selection and one re-level
  reaches all of it, while a dam or a waterfall lip keeps its pool apart. `Join bodies across
  heights` drops that bed test. Re-level by `[scroll]` (coarse with `[Shift]`) or drag the height gizmo.
* **Place new water** `[Ctrl+N]`, remove `[Del]`, set the type (water, ocean, magma, slime).
* **The surface is one absolute height, not a fresh reading per click.** The first click takes it from the terrain under the cursor; every later click reuses it, so walking the brush along a long river keeps one level instead of leaving one group of chunks per click at the height of wherever you clicked. `[scroll]` then dials that height, `[Ctrl+scroll]` the brush.
* **The surface also walks the bed**: after the brush fills the chunks it touches, the level flows into the neighbour chunks whose boundary with the water sits below it, up to **Flow past the brush** chunks (0 = brush only). A bank or a waterfall lip that rises above the surface stops it, so a river gets filled to the next rapid without leaking sideways over a flat shore. `Extend along bed` walks the same rule further from whatever you selected, so pressing it again keeps following the river.
* **The left sidebar swaps in while the tool is active**, like the Shadows sidebar, and holds: the four liquid types plus **Place liquid**, the **level** box (the one height the next click writes, and the height a re-level goes to), **Set level**, **Level to cursor** (level the body to the shoreline you click), **Extend along bed**, **Keep each chunk's own height** (off = the selection becomes one flat plane, which is what welds separately placed groups of a river onto one level), **Join bodies across heights** (one click selects the whole connected liquid whatever heights its parts sit at, so one re-level reaches all of it) and **Flow past the brush** and **Lift above terrain** (how far a click sits over the ground when the level is still auto).

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

* **Message log**: the status bar keeps only the newest message, so several of them in a row used to flash past each other. The **Log** button in the status bar opens a panel over the viewport corner listing every message this session made, with a time stamp, newest first and scrollable; a burst of the same message collapses into one line with **×N**. Click the button again to close it (the log keeps collecting while hidden), and **Clear** empties it. Live readouts such as the streaming tile count stay on the status bar only, so they cannot bury the real messages.
* **Minimap** `[M]`, **wireframe** `[F]`, **debug views** `[G]`, **doodads on / off** `[T]`, **WMOs on / off** `[Y]`.
* **Doodad animation on / off** `[V]`.
* **Undo** `[Ctrl+Z]` / **redo** `[Ctrl+Y]`, **screenshot to clipboard** `[Ctrl+Shift+P]`, **full key sheet** `[F1]`.
* **Custom MPQ reader;** load / unload archives, and the database connection panel (File menu).
* **Loading screen:** opening the app or choosing another map spends a few seconds bringing a whole map out of the archives, and while that work is still waiting the client's own loading screen is drawn over the viewport instead of the half-built world.
