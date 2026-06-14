using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;
using Salmiak.Rendering;

namespace Salmiak;

public sealed partial class GlViewport
{
    public event Action<string>? Notify;

    private bool _screenshotPending;

    public event Action? HelpToggled;

    public sealed record HudState(
        bool Loading,
        string Banner, System.Windows.Media.Color BannerColor,
        string BannerSub, System.Windows.Media.Color BannerSubColor,
        string LayerInfo,
        string? PaintTexturePath, string PaintName, string PaintSub,
        string Coords,
        string Help,
        string Stats);

    public event Action<HudState>? HudChanged;

    private HudState? _lastHud;

    private static System.Windows.Media.Color HudColor(float r, float g, float b) =>
        System.Windows.Media.Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));

    private void DrawOverlay()
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;

        string banner = "";
        var bannerColor = HudColor(1f, 1f, 1f);
        string bannerSub = "";
        var bannerSubColor = bannerColor;

        if (FlightMode && _selectedPath != null)
        {
            banner = _flightDrawing
                ? $"Drawing:  {_selectedPath.Name}   ({_flightDrawMsg})   ·   MMB = waypoint at camera   ·   Enter = finish"
                : $"Taxi path:  {_selectedPath.Name}" + (_selectedNode >= 0 ? $"   [waypoint {_selectedNode + 1}/{_selectedPath.Points.Count}]" : "");
            bannerColor = HudColor(1f, 0.9f, 0.2f);

            if (_flightDrawing && _selectedPath.IsNew &&
                _selectedPath.MountHorde == 0 && _selectedPath.MountAlliance == 0)
            {
                UpdateDockDistance();
                if (float.IsNaN(_dockDist))
                { bannerSub = $"no building within {DockScanRange:F0} yd"; bannerSubColor = HudColor(0.7f, 0.7f, 0.7f); }
                else if (_dockDist < DockGoodMin)
                { bannerSub = $"dock distance: {_dockDist:F1} yd - too close"; bannerSubColor = HudColor(1f, 0.3f, 0.25f); }
                else if (_dockDist > DockGoodMax)
                { bannerSub = $"dock distance: {_dockDist:F1} yd - too far"; bannerSubColor = HudColor(1f, 0.3f, 0.25f); }
                else
                { bannerSub = $"dock distance: {_dockDist:F1} yd - good"; bannerSubColor = HudColor(0.35f, 1f, 0.4f); }
            }
        }
        else if (NpcMode && _npcSelected >= 0 && _npcSelected < _npcShown.Count)
        {
            var sp = _npcShown[_npcSelected];
            if (_patrolSel >= 0 && _patrolSel < _patrol.Count)
            {
                var p = _patrol[_patrolSel];
                banner = $"{sp.Name} - waypoint {p.Point} ({_patrolSel + 1}/{_patrol.Count})   " +
                         $"wait {p.WaitMs} ms   script {p.ScriptId}   ({p.X:F1}, {p.Y:F1}, {p.Z:F1})" +
                         (string.IsNullOrWhiteSpace(p.Comment) ? "" : $"   \"{p.Comment}\"");
            }
            else
                banner = $"{sp.Name}   guid {sp.Guid}   entry {sp.Entry}   {MovementTypeName(sp.MovementType)}" +
                         (_patrol.Count > 0 ? $"   ·   {_patrol.Count} waypoint(s) - click one for details" : "");
            bannerColor = HudColor(1f, 0.75f, 0.4f);
        }
        else if (HerbMode && HerbInfo() is { Length: > 0 } hi)
        {
            banner = hi;
            bannerColor = HudColor(0.55f, 1f, 0.75f);
        }
        else if (_areaMode && _zoneClickedInfo.Length > 0)
        {
            banner = _zoneClickedInfo;
            bannerColor = HudColor(0.6f, 1f, 0.6f);
        }
        else if ((DoodadMode || WmoMode) && _sel != null)
        {
            banner = _selSet.Count > 1 ? $"{_selSet.Count} doodads selected" : SelectedName();
            bannerColor = HudColor(1f, 0.95f, 0.6f);
        }

        string layerInfo = (EditMode || TextureMode) && !DoodadMode && !CursorOverMinimap()
            ? BuildLayerInfo() : "";

        string? paintPath = TextureMode && !string.IsNullOrEmpty(PaintTexture) ? PaintTexture : null;
        string paintName = paintPath != null ? System.IO.Path.GetFileNameWithoutExtension(paintPath) : "";
        string paintSub = paintPath != null
            ? (PaintLayer == 0 ? "base layer (replaces)" : "blend (auto layer)") : "";

        string coords = CursorOverMinimap() ? "" : BuildCoordsText();
        string stats = $"{_fps:F0} fps · {_frameMsAvg:F1} ms";

        var hud = new HudState(_loading, banner, bannerColor, bannerSub, bannerSubColor,
            layerInfo, paintPath, paintName, paintSub, coords, BuildHelpText(), stats);
        if (hud != _lastHud)
        {
            _lastHud = hud;
            HudChanged?.Invoke(hud);
        }

        DrawMinimap(w, h);
    }

    private void CaptureScreenshot()
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;
        var px = new byte[w * h * 3];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, w, h, OpenTK.Graphics.OpenGL4.PixelFormat.Rgb, PixelType.UnsignedByte, px);

        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var row = new byte[w * 3];
            for (int y = 0; y < h; y++)
            {
                int src = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    row[x * 3] = px[src + x * 3 + 2];
                    row[x * 3 + 1] = px[src + x * 3 + 1];
                    row[x * 3 + 2] = px[src + x * 3];
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w * 3);
            }
            bmp.UnlockBits(data);
            Clipboard.SetImage(bmp);
            Notify?.Invoke($"Screenshot copied to clipboard ({w}×{h}).");
        }
        catch (Exception ex) { Notify?.Invoke($"Screenshot failed: {ex.Message}"); }
    }

    private string BuildCoordsText()
    {
        if (_text == null || !PickTerrain(_mouseX, _mouseY, out var hit)) return "";
        float wx = -hit.Z, wy = -hit.X, wz = hit.Y;
        const float ts = AdtFile.TileSize, step = AdtFile.ChunkSize / 8f;
        int tileX = (int)MathF.Floor(32f + hit.X / ts);
        int tileY = (int)MathF.Floor(32f + hit.Z / ts);
        float c = (hit.X + (32 - tileX) * ts) / step;
        float r = (hit.Z + (32 - tileY) * ts) / step;
        int chunkCol = (int)(c / 8), chunkRow = (int)(r / 8);
        return $"X {wx:F0}   Y {wy:F0}   Z {wz:F0}      Tile {tileX},{tileY}   Chunk {chunkRow},{chunkCol}";
    }

    private string SelectedName()
    {
        if (_sel == null) return "";
        var s = _sel.Value;
        if (s.IsWmo)
            return s.Index >= 0 && s.Index < s.Adt.Wmos.Count
                ? "Selected: " + System.IO.Path.GetFileNameWithoutExtension(s.Adt.Wmos[s.Index].ModelPath) : "";
        return s.Index >= 0 && s.Index < s.Adt.Doodads.Count
            ? "Selected: " + System.IO.Path.GetFileNameWithoutExtension(s.Adt.Doodads[s.Index].ModelPath) : "";
    }

    private string BuildHelpText()
    {
        if (FlightMode)
        {
            if (_flightDrawing && _selectedPath != null)
            {
                bool transport = _selectedPath.MountHorde == 0 && _selectedPath.MountAlliance == 0 && _selectedPath.IsNew;
                string kind = _selectedPath.TransportKind.Length > 0 ? _selectedPath.TransportKind : "route";
                if (transport)
                    return $"Drawing {kind}: {_selectedPath.Name}   [{_selectedPath.Points.Count} waypoint(s), {_flightDrawMsg}]\n" +
                           "MMB = waypoint   ·   Shift+MMB = passenger STOP" +
                           (DrawingBoat ? " (boats ride sea level - camera height ignored)" : "   ·   PgUp/PgDn altitude") +
                           "   ·   Del remove   ·   Enter = close loop + finish";
                return $"Drawing new route: {_selectedPath.Name}   [{_selectedPath.Points.Count} waypoint(s), {_flightDrawMsg}]\n" +
                       "fly the route, MMB = drop waypoint AT CAMERA   ·   drag a waypoint = move   ·   PgUp/PgDn altitude   ·   Del remove   ·   Enter finish";
            }
            return (_selectedPath == null
                       ? "Taxi mode (taxi paths, zeppelins, boats) - click a path to select it"
                       : $"Editing: {_selectedPath.Name}   ·   drag a waypoint   ·   click terrain = add   ·   middle-click wp = stop/teleport   ·   Del = remove   ·   PgUp/PgDn = altitude") + "\n" +
                   "N new taxi path / zeppelin / boat   ·   Ctrl+Z undo / Ctrl+Y redo   ·   Esc deselect / exit   ·   RMB look   ·   B exit";
        }
        if (_hmPlacing)
            return "Place heightmap\n" +
                   (_hmCornerA == null ? "click the FIRST corner of the rectangle" : "click the OPPOSITE corner to apply") +
                   "   ·   RMB look   ·   Esc cancel";
        if (_areaMode)
        {
            string? an = AreaName(_targetAreaId);
            string armed = _targetAreaId > 0 ? $"[painting {_targetAreaId}{(an != null ? $" - {an}" : "")}]" : "[inspect - no zone armed]";
            return $"Zone mode (Ctrl+A)   {armed}   chunks colour-coded by zone\n" +
                   "click = inspect / paint   Shift+drag = paint rectangle   middle-click = pick zone   N = NEW zone   Ctrl+Z undo   Ctrl+A / Esc exit";
        }
        if (GenerateMode)
            return "Ground generator (Ctrl+G)\n" +
                   "click a chunk to fill it with a realistic 4-texture blend   Ctrl+Z undo / Ctrl+Y redo   Esc exit";
        if (_blendMode)
            return $"Blend chunks (Ctrl+L)   [{(_blendAggressive ? "aggressive" : "feather")}, band {_blendBand}]\n" +
                   "click two adjacent chunks (≤4 combined textures)   middle-click = settings   Ctrl+Z undo / Ctrl+Y redo   Esc exit";
        if (WmoMode)
        {
            if (!string.IsNullOrEmpty(_placeWmo))
                return $"Placing WMO: {System.IO.Path.GetFileNameWithoutExtension(_placeWmo)}\n" +
                       "LMB place   PgUp/PgDn lift   O collide   Esc cancel";
            return "WMO mode\nclick select   drag center/axes move, rings rotate   Del delete   RMB copy/delete   N palette   Ctrl+W exit";
        }
        if (DoodadMode)
        {
            if (!string.IsNullOrEmpty(_placeDoodad))
                return $"Placing: {System.IO.Path.GetFileNameWithoutExtension(_placeDoodad)}" +
                       (RandomRotation ? "  [random yaw]" : "") + (AlignToNormal ? "  [align ground]" : "") + "\n" +
                       "LMB place   PgUp/PgDn lift   O collide   R random yaw   L align ground   Esc cancel";
            return "Doodad mode\nclick select   Shift+click multi-select   drag center/axes move, rings rotate, scale   Del delete   Ctrl+C/V copy/paste   RMB menu   N search drawer   Ctrl+D exit";
        }
        if (TextureMode)
        {
            string iso = IsolateLayer ? "   [isolating layer]" : "";
            if (RoadMode)
                return "Road generator (Ctrl+R)\n" +
                       "Ctrl+N set textures   drag to paint   Ctrl+scroll width   Ctrl+Z undo / Ctrl+Y redo   Esc exit";
            if (!string.IsNullOrEmpty(PaintTexture))
                return $"Texture paint  [{(PaintLayer == 0 ? "BASE layer" : "blend - layer auto per chunk")}, str {PaintStrength:F0}{(Dither ? ", dither ON" : "")}]  {System.IO.Path.GetFileNameWithoutExtension(PaintTexture)}{iso}\n" +
                       (PaintLayer == 0 ? "LMB set base + reveal it (erase upper layers)" : "LMB paint this texture   Ctrl+LMB erase it   X strip its layer from the hovered chunk") +
                       "   middle-click = strength   P pipette   Shift+scroll base/blend   Ctrl+scroll size   I isolate   J dither   N pick\n" +
                       "Ctrl+G generate   Ctrl+R road   Ctrl+P slope paint   Ctrl+L blend seam   Ctrl+C/V copy/paste   Ctrl+Z undo / Ctrl+Y redo   Ctrl+T exit";
            return "Texture mode - pick a texture to paint (its layer is found per chunk automatically)" + iso + "\n" +
                   "N pick texture   P pipette   Shift+scroll base/blend   I isolate   J dither   Ctrl+L blend seam   Ctrl+G generate   Ctrl+R road   Ctrl+T exit";
        }
        if (EditMode)
        {
            if (DeleteMode)
                return "Delete terrain / holes (Ctrl+H)\nLMB punch holes (caves)   Ctrl+LMB fill back   Ctrl+scroll brush size   Ctrl+H exit   Ctrl+Z undo / Ctrl+Y redo";
            if (FlattenMode)
                return $"Flatten height (Ctrl+F)   [str {BrushStrength:F0}]\nLMB level to first-clicked height   middle-click = strength   Ctrl+scroll brush size   Ctrl+F exit   Ctrl+Z undo / Ctrl+Y redo";
            if (SmoothMode)
                return $"Smooth / blur (Ctrl+B)   [str {BrushStrength:F0}, falloff: {BrushFalloff}]\nLMB smooth toward local average   middle-click = strength   Ctrl+scroll brush size   Ctrl+B exit   Ctrl+Z undo / Ctrl+Y redo";
            if (NoiseMode)
                return $"Noise / roughen (Ctrl+K)   [falloff: {BrushFalloff}, grain: {NoiseGrain:F0}yd]\nLMB add bumps   middle-click = tune grain   Ctrl+scroll brush size   Ctrl+K exit   Ctrl+Z undo / Ctrl+Y redo";
            if (ShadowMode)
                return $"Shadow brush (Ctrl+M)   [str {BrushStrength:F0}, falloff: {BrushFalloff}]\nLMB paint baked shadow   Ctrl+LMB erase (ghost shadows / bright seams)   middle-click = strength   Ctrl+scroll brush size   Ctrl+M exit   Ctrl+Z undo / Ctrl+Y redo";
            return "Sculpt height" + (DoodadFollow ? "   (objects follow: ON)" : "") + $"   [str {BrushStrength:F0}, falloff: {BrushFalloff}]\n" +
                   "LMB raise   Ctrl+LMB lower   middle-click = strength   Ctrl+scroll size   Ctrl+F flatten   Ctrl+H delete   Ctrl+B smooth   Ctrl+K noise   Ctrl+M shadow   Ctrl+J river\n" +
                   "Ctrl+Q objects follow   Ctrl+Z undo / Ctrl+Y redo   Ctrl+T texture mode";
        }
        if (LiquidMode)
        {
            if (_liqArmed != 0)
                return $"Liquids (Ctrl+U) - placing {WaterTypeName(_liqArmed)}   [surface: terrain + {_liqPlaceLift:F2} yd]\n" +
                       "LMB fill chunks under the brush   scroll = surface height   Ctrl+scroll brush size   Ctrl+N change liquid   Esc stop placing";
            if (_liqSel.Count > 0)
                return $"Liquids (Ctrl+U) - {_liqSel.Count} chunk(s) selected @ {_liqSelLevel:F2}\n" +
                       "drag the arrow handle or scroll = raise/lower surface (Shift = coarse)   Del remove liquid   Ctrl+N place new   Esc deselect   Ctrl+Z undo / Ctrl+Y redo";
            return "Liquids mode (Ctrl+U)\n" +
                   "click a liquid to select its whole body   Ctrl+N / middle-click pick a liquid to place   Ctrl+U exit";
        }
        if (NpcMode)
            return $"NPC view - {_npcCount} creature spawn(s) from the world database\n" +
                   "click an NPC = select + show its patrol   ·   click a waypoint = details   ·   Esc = step out / exit   ·   RMB look   WASD move";
        if (HerbMode)
            return $"Herb/Vein view - {_herbShown.Count} gathering node(s) from the world database\n" +
                   "click a node = select + details (others dim)   ·   Esc = deselect / exit   ·   RMB look   WASD move";
        return "WASD move   RMB look   SCROLL speed   F wire   G debug   T doodads   Y WMOs   B flight paths   Ctrl+E edit   Ctrl+T texture   Ctrl+D doodads   Ctrl+W WMOs";
    }

    private string BuildLayerInfo()
    {
        if (_wdt == null || !PickTerrain(_mouseX, _mouseY, out var hit)) return "";
        if (!TryGetChunkAt(hit, out var adt, out var chunk, out int tileX, out int tileY, out int cCol, out int cRow))
            return "";
        var sb = new System.Text.StringBuilder();
        sb.Append($"Tile {tileX},{tileY}  chunk {cRow},{cCol}\n");
        sb.Append($"Layers: {chunk.NLayers}");
        bool painting = !string.IsNullOrEmpty(PaintTexture);
        for (int l = chunk.NLayers - 1; l >= 0; l--)
        {
            uint ti = chunk.Layers[l].TextureIndex;
            string name = ti < adt.Textures.Count
                ? System.IO.Path.GetFileNameWithoutExtension(adt.Textures[(int)ti]) : "?";
            string mark = painting && l == PaintLayer ? ">" : " ";
            sb.Append($"\n {mark}L{l}: {name}{(l == 0 ? " (base)" : "")}");
        }
        return sb.ToString();
    }

    private void AppendChunkOutlineLines(Salmiak.Core.Formats.MapChunk chunk, List<float> dst)
    {
        const float cs = AdtFile.ChunkSize;
        float minX = -chunk.Position.Y, maxX = minX + cs;
        float minZ = -chunk.Position.X, maxZ = minZ + cs;
        const int per = 4;
        Span<float> px = stackalloc float[per * 4];
        Span<float> pz = stackalloc float[per * 4];
        int n = 0;
        for (int i = 0; i < per; i++) { px[n] = minX + cs * i / per; pz[n] = minZ; n++; }
        for (int i = 0; i < per; i++) { px[n] = maxX; pz[n] = minZ + cs * i / per; n++; }
        for (int i = 0; i < per; i++) { px[n] = maxX - cs * i / per; pz[n] = maxZ; n++; }
        for (int i = 0; i < per; i++) { px[n] = minX; pz[n] = maxZ - cs * i / per; n++; }

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            AddOutlinePt(dst, px[i], pz[i], chunk.Position.Z);
            AddOutlinePt(dst, px[j], pz[j], chunk.Position.Z);
        }
    }

    private void AddOutlinePt(List<float> dst, float x, float z, float fallbackY)
    {
        float y = HeightAtGL(x, z);
        if (float.IsNaN(y)) y = fallbackY;
        dst.Add(x); dst.Add(y + 0.3f); dst.Add(z);
    }

    private int BuildChunkOutline(Salmiak.Core.Formats.MapChunk chunk)
    {
        const float cs = AdtFile.ChunkSize;
        float minX = -chunk.Position.Y, maxX = minX + cs;
        float minZ = -chunk.Position.X, maxZ = minZ + cs;
        const int per = 9;
        int n = 0;
        void Put(float x, float z)
        {
            float y = HeightAtGL(x, z); if (float.IsNaN(y)) y = chunk.Position.Z;
            _outline[n * 3] = x; _outline[n * 3 + 1] = y + 0.4f; _outline[n * 3 + 2] = z; n++;
        }
        for (int i = 0; i < per; i++) Put(minX + cs * i / per, minZ);
        for (int i = 0; i < per; i++) Put(maxX, minZ + cs * i / per);
        for (int i = 0; i < per; i++) Put(maxX - cs * i / per, maxZ);
        for (int i = 0; i < per; i++) Put(minX, maxZ - cs * i / per);
        return n;
    }
}
