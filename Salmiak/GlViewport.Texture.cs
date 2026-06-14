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
    public event Action<string?>? PaintTextureChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool TextureMode { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsolateLayer { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? PaintTexture { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int PaintLayer { get; set; } = 1;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float PaintStrength { get; set; } = 10f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dither { get; set; }

    private readonly Random _rng = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool GenerateMode { get; set; }

    public event Action? OpenGeneratePicker;

    public void SetGenerateConfig(string baseTex, string tex1, string tex2, string tex3,
                                  float cov1, float cov2, float cov3, float scale)
    {
        _genTex[0] = baseTex; _genTex[1] = tex1; _genTex[2] = tex2; _genTex[3] = tex3;
        _genCov[0] = Math.Clamp(cov1, 0f, 1f);
        _genCov[1] = Math.Clamp(cov2, 0f, 1f);
        _genCov[2] = Math.Clamp(cov3, 0f, 1f);
        _genScale = Math.Clamp(scale, 0.003f, 0.06f);
        Notify?.Invoke("Ground generator: click a chunk to fill it.");
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool RoadMode { get; set; }

    public event Action? OpenRoadPicker;

    public void SetRoadConfig(string road, string contour1, string contour2, float width, float contourW, float noise)
    {
        _roadTex[0] = road; _roadTex[1] = contour1; _roadTex[2] = contour2;
        _roadWidth = Math.Clamp(width, 0.1f, 0.8f);
        _roadContour = Math.Clamp(contourW, 0.05f, 0.5f);
        _roadNoise = Math.Clamp(noise, 0f, 0.4f);
        Notify?.Invoke("Road: drag to paint a road (Ctrl+scroll for width).");
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoPaintMode { get; set; }

    public event Action? OpenAutoPaintPicker;

    public void SetAutoPaintConfig(string flat, string? mid, string cliff, float lowDeg, float highDeg)
    {
        _autoTex[0] = flat; _autoTex[1] = mid; _autoTex[2] = cliff;
        _autoLowDeg = Math.Clamp(lowDeg, 5f, 75f);
        _autoHighDeg = Math.Clamp(Math.Max(highDeg, _autoLowDeg + 2f), 10f, 85f);
        Notify?.Invoke("Slope auto-paint: drag over terrain - texture follows the slope.");
    }

    private sealed class ChunkClip { public int NLayers; public string[] Paths = []; public byte[]?[] Alphas = new byte[]?[3]; }

    private ChunkClip? _chunkClip;

    public List<string> LoadedTextures()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _tileCache)
            if (kv.Value != null)
                foreach (var t in kv.Value.Textures)
                    if (!string.IsNullOrEmpty(t)) set.Add(t);
        return set.ToList();
    }

    private sealed class TexState
    {
        public int NLayers;
        public Salmiak.Core.Formats.TextureLayer[] Layers = [];
        public byte[]?[] AlphaMaps = [];
    }

    private void ApplyPaintBrush(Vector3 hit, float dt)
    {
        string texPath = PaintTexture!;
        float r = BrushRadius;
        float add = 255f * PaintStrength * dt;
        var rebuild = new HashSet<AdtFile>();
        var edited = new HashSet<AdtFile>();
        int skipped = 0;

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            (int tx, int ty) = kv.Key;
            float oX = -(32 - tx) * AdtFile.TileSize, oZ = -(32 - ty) * AdtFile.TileSize;
            if (hit.X + r < oX || hit.X - r > oX + AdtFile.TileSize ||
                hit.Z + r < oZ || hit.Z - r > oZ + AdtFile.TileSize) continue;

            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null) continue;
                if (!BrushHitsChunk(chunk, hit, r)) continue;

                if (_stroke != null)
                {
                    if (!_stroke.Textures.ContainsKey(chunk)) _stroke.Textures[chunk] = SnapshotTexState(chunk);
                    _stroke.Tiles.Add(adt);
                }

                if (PaintLayer == 0)
                {
                    PaintBaseLayer(adt, chunk, cy, cx, texPath, hit, r, add, rebuild, edited);
                    continue;
                }

                bool fresh = false;
                int mapIdx = EnsureAutoLayer(adt, chunk, texPath, ref fresh);
                if (fresh) rebuild.Add(adt);
                if (mapIdx == -2) { skipped++; continue; }
                if (mapIdx == -1)
                {
                    bool ch = false;
                    for (int up = 1; up < chunk.NLayers; up++)
                    {
                        var amap = chunk.AlphaMaps[up - 1];
                        if (amap == null) continue;
                        if (PaintAlphaTexels(chunk, amap, hit, r, -add, out int ex0, out int ey0, out int ex1, out int ey1))
                        {
                            ch = true;
                            if (!rebuild.Contains(adt) && _terrain!.TryGetChunkAlpha(adt, cy, cx, up - 1, out int gl))
                            { TextureCache.UpdateAlphaRect(gl, amap, ex0, ey0, ex1 - ex0 + 1, ey1 - ey0 + 1); _dirtyAlpha.Add(gl); }
                            else rebuild.Add(adt);
                        }
                    }
                    if (ch) edited.Add(adt);
                    continue;
                }
                chunk.AlphaMaps[mapIdx] ??= new byte[4096];
                var map = chunk.AlphaMaps[mapIdx]!;

                bool changed = PaintAlphaTexels(chunk, map, hit, r, add, out int rx0, out int ry0, out int rx1, out int ry1);
                if (changed) edited.Add(adt);
                if (changed && !rebuild.Contains(adt))
                {
                    if (_terrain!.TryGetChunkAlpha(adt, cy, cx, mapIdx, out int glId))
                    {
                        TextureCache.UpdateAlphaRect(glId, map, rx0, ry0, rx1 - rx0 + 1, ry1 - ry0 + 1);
                        _dirtyAlpha.Add(glId);
                    }
                    else
                        rebuild.Add(adt);
                }
            }
        }

        foreach (var adt in rebuild) { edited.Add(adt); _terrain!.RebuildTile(adt, _texCache); }
        foreach (var adt in edited) MarkEdited(adt);
        if (skipped > 0) Notify?.Invoke($"Paint: {skipped} chunk(s) skipped - their 4 layers hold other textures (erase one there first).");
    }

    private void ApplyEraseBrush(Vector3 hit, float dt)
    {
        string? tex = PaintTexture;
        float r = BrushRadius;
        float sub = 255f * PaintStrength * dt;
        var edited = new HashSet<AdtFile>();

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            (int tx, int ty) = kv.Key;
            float oX = -(32 - tx) * AdtFile.TileSize, oZ = -(32 - ty) * AdtFile.TileSize;
            if (hit.X + r < oX || hit.X - r > oX + AdtFile.TileSize ||
                hit.Z + r < oZ || hit.Z - r > oZ + AdtFile.TileSize) continue;

            int texIndex = tex != null
                ? adt.Textures.FindIndex(t => string.Equals(t, tex, StringComparison.OrdinalIgnoreCase))
                : -1;
            if (tex != null && texIndex < 0) continue;

            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null) continue;

                int mapIdx = -1;
                if (tex != null)
                {
                    for (int l = 1; l < chunk.NLayers; l++)
                        if (chunk.Layers[l].TextureIndex == texIndex) { mapIdx = l - 1; break; }
                }
                else if (PaintLayer >= 1 && PaintLayer < chunk.NLayers) mapIdx = PaintLayer - 1;
                if (mapIdx < 0) continue;

                var map = chunk.AlphaMaps[mapIdx];
                if (map == null) continue;
                if (!BrushHitsChunk(chunk, hit, r)) continue;

                if (_stroke != null)
                {
                    if (!_stroke.Textures.ContainsKey(chunk)) _stroke.Textures[chunk] = SnapshotTexState(chunk);
                    _stroke.Tiles.Add(adt);
                }

                if (PaintAlphaTexels(chunk, map, hit, r, -sub, out int rx0, out int ry0, out int rx1, out int ry1))
                {
                    edited.Add(adt);
                    if (_terrain!.TryGetChunkAlpha(adt, cy, cx, mapIdx, out int glId))
                    {
                        TextureCache.UpdateAlphaRect(glId, map, rx0, ry0, rx1 - rx0 + 1, ry1 - ry0 + 1);
                        _dirtyAlpha.Add(glId);
                    }
                }
            }
        }
        foreach (var adt in edited) MarkEdited(adt);
    }

    private void PaintBaseLayer(AdtFile adt, Salmiak.Core.Formats.MapChunk chunk, int cy, int cx,
                               string texPath, Vector3 hit, float r, float add,
                               HashSet<AdtFile> rebuild, HashSet<AdtFile> edited)
    {
        int texIndex = adt.Textures.FindIndex(t => string.Equals(t, texPath, StringComparison.OrdinalIgnoreCase));
        if (texIndex < 0) { adt.Textures.Add(texPath); texIndex = adt.Textures.Count - 1; }

        if (chunk.NLayers == 0)
        {
            chunk.Layers[0] = new Salmiak.Core.Formats.TextureLayer { TextureIndex = (uint)texIndex };
            chunk.NLayers = 1;
            rebuild.Add(adt);
        }
        else if (chunk.Layers[0].TextureIndex != texIndex)
        {
            chunk.Layers[0] = new Salmiak.Core.Formats.TextureLayer { TextureIndex = (uint)texIndex };
            rebuild.Add(adt);
        }

        bool changed = false;
        for (int up = 1; up < chunk.NLayers; up++)
        {
            var amap = chunk.AlphaMaps[up - 1];
            if (amap == null) continue;
            if (PaintAlphaTexels(chunk, amap, hit, r, -add, out int ex0, out int ey0, out int ex1, out int ey1))
            {
                changed = true;
                if (!rebuild.Contains(adt) && _terrain!.TryGetChunkAlpha(adt, cy, cx, up - 1, out int glId))
                {
                    TextureCache.UpdateAlphaRect(glId, amap, ex0, ey0, ex1 - ex0 + 1, ey1 - ey0 + 1);
                    _dirtyAlpha.Add(glId);
                }
                else rebuild.Add(adt);
            }
        }
        if (changed) edited.Add(adt);
    }

    private void RemoveLayerAtCursor()
    {
        if (!PickTerrain(_mouseX, _mouseY, out var hit) ||
            !TryGetChunkAt(hit, out var adt, out var chunk, out _, out _, out _, out _)) return;

        int layer = -1;
        if (!string.IsNullOrEmpty(PaintTexture))
        {
            int ti = adt.Textures.FindIndex(t => string.Equals(t, PaintTexture, StringComparison.OrdinalIgnoreCase));
            if (ti >= 0)
                for (int l = 1; l < chunk.NLayers; l++)
                    if (chunk.Layers[l].TextureIndex == ti) { layer = l; break; }
        }
        if (layer < 0 && PaintLayer >= 1 && PaintLayer < chunk.NLayers) layer = PaintLayer;
        if (layer < 1 || layer >= chunk.NLayers)
        { Notify?.Invoke("X: this chunk has no matching blend layer to remove (the base can't be removed)."); return; }

        var rec = new Stroke();
        rec.Textures[chunk] = SnapshotTexState(chunk);
        rec.Tiles.Add(adt);
        PushUndo(rec);

        int texIdx = (int)chunk.Layers[layer].TextureIndex;
        string name = texIdx >= 0 && texIdx < adt.Textures.Count
            ? System.IO.Path.GetFileNameWithoutExtension(adt.Textures[texIdx]) : $"layer {layer}";
        for (int l = layer; l < chunk.NLayers - 1; l++)
        {
            chunk.Layers[l] = chunk.Layers[l + 1];
            chunk.AlphaMaps[l - 1] = chunk.AlphaMaps[l];
        }
        chunk.AlphaMaps[chunk.NLayers - 2] = null;
        chunk.NLayers--;

        MarkEdited(adt);
        _terrain!.RebuildTile(adt, _texCache);
        Notify?.Invoke($"Removed layer '{name}' from this chunk (Ctrl+Z undoes).");
    }

    private static TexState SnapshotTexState(Salmiak.Core.Formats.MapChunk chunk)
    {
        var s = new TexState
        {
            NLayers = chunk.NLayers,
            Layers = (Salmiak.Core.Formats.TextureLayer[])chunk.Layers.Clone(),
            AlphaMaps = new byte[]?[chunk.AlphaMaps.Length],
        };
        for (int i = 0; i < chunk.AlphaMaps.Length; i++)
            s.AlphaMaps[i] = chunk.AlphaMaps[i] != null ? (byte[])chunk.AlphaMaps[i]!.Clone() : null;
        return s;
    }

    private const int MapChunkMaxLayers = 4;

    private bool PaintAlphaTexels(Salmiak.Core.Formats.MapChunk chunk, byte[] map,
                                  Vector3 hit, float radius, float add,
                                  out int rx0, out int ry0, out int rx1, out int ry1)
    {
        rx0 = ry0 = 64; rx1 = ry1 = -1;
        bool changed = false;
        float cs = AdtFile.ChunkSize;

        float sx = 63f / cs;
        int tx0 = Math.Clamp((int)MathF.Floor((hit.X - radius + chunk.Position.Y) * sx), 0, 63);
        int tx1 = Math.Clamp((int)MathF.Ceiling((hit.X + radius + chunk.Position.Y) * sx), 0, 63);
        int ty0 = Math.Clamp((int)MathF.Floor((hit.Z - radius + chunk.Position.X) * sx), 0, 63);
        int ty1 = Math.Clamp((int)MathF.Ceiling((hit.Z + radius + chunk.Position.X) * sx), 0, 63);

        for (int ty = ty0; ty <= ty1; ty++)
        for (int tx = tx0; tx <= tx1; tx++)
        {
            float gx = -(chunk.Position.Y - tx / 63f * cs);
            float gz = -(chunk.Position.X - ty / 63f * cs);
            float dx = gx - hit.X, dz = gz - hit.Z;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d >= radius) continue;
            float f = 1f - d / radius; f = f * f * (3f - 2f * f);
            float amt = add * f;
            if (Dither) amt *= (float)_rng.NextDouble();
            int idx = ty * 64 + tx;
            int v = map[idx] + (int)amt;
            if (v > 255) v = 255;
            if (v < 0) v = 0;
            if (v != map[idx])
            {
                map[idx] = (byte)v; changed = true;
                if (tx < rx0) rx0 = tx; if (tx > rx1) rx1 = tx;
                if (ty < ry0) ry0 = ty; if (ty > ry1) ry1 = ty;
            }
        }
        return changed;
    }

    private void SampleTextureAtCursor()
    {
        if (_wdt == null || !PickTerrain(_mouseX, _mouseY, out var hit)) return;
        const float ts = AdtFile.TileSize;
        const float step = AdtFile.ChunkSize / 8f;
        int tileX = (int)MathF.Floor(32f + hit.X / ts);
        int tileY = (int)MathF.Floor(32f + hit.Z / ts);
        if (!_tileCache.TryGetValue((tileX, tileY), out var adt) || adt == null) return;

        float c = (hit.X + (32 - tileX) * ts) / step;
        float r = (hit.Z + (32 - tileY) * ts) / step;
        if (c < 0 || c >= 128 || r < 0 || r >= 128) return;
        int chunkCol = (int)(c / 8), chunkRow = (int)(r / 8);
        var chunk = adt.Chunks[chunkRow, chunkCol];
        if (chunk == null) return;

        int tx = Math.Clamp((int)MathF.Round((c - chunkCol * 8) / 8f * 63f), 0, 63);
        int ty = Math.Clamp((int)MathF.Round((r - chunkRow * 8) / 8f * 63f), 0, 63);

        int picked = 0;
        for (int l = chunk.NLayers - 1; l >= 1; l--)
        {
            var map = chunk.AlphaMaps[l - 1];
            if (map != null && map[ty * 64 + tx] > 127) { picked = l; break; }
        }

        uint ti = chunk.Layers[picked].TextureIndex;
        if (ti >= adt.Textures.Count) return;
        PaintTexture = adt.Textures[(int)ti];
        PaintTextureChanged?.Invoke(PaintTexture);
    }

    private void CopyChunkTextures()
    {
        if (!PickTerrain(_mouseX, _mouseY, out var hit)) return;
        if (!TryGetChunkAt(hit, out var adt, out var chunk, out _, out _, out _, out _)) return;
        var clip = new ChunkClip { NLayers = chunk.NLayers, Paths = new string[chunk.NLayers] };
        for (int l = 0; l < chunk.NLayers; l++)
        {
            uint ti = chunk.Layers[l].TextureIndex;
            clip.Paths[l] = ti < adt.Textures.Count ? adt.Textures[(int)ti] : "";
        }
        for (int i = 0; i < 3; i++)
            clip.Alphas[i] = chunk.AlphaMaps[i] != null ? (byte[])chunk.AlphaMaps[i]!.Clone() : null;
        _chunkClip = clip;
        PaintTextureChanged?.Invoke(PaintTexture);
    }

    private void PasteChunkTextures()
    {
        if (_chunkClip is not { } clip) return;
        if (!PickTerrain(_mouseX, _mouseY, out var hit)) return;
        if (!TryGetChunkAt(hit, out var adt, out var chunk, out _, out _, out _, out _)) return;

        var rec = new Stroke();
        rec.Textures[chunk] = SnapshotTexState(chunk);
        rec.Tiles.Add(adt);
        PushUndo(rec);

        chunk.NLayers = clip.NLayers;
        for (int l = 0; l < clip.NLayers; l++)
        {
            string path = clip.Paths[l];
            int ti = adt.Textures.FindIndex(t => string.Equals(t, path, StringComparison.OrdinalIgnoreCase));
            if (ti < 0 && !string.IsNullOrEmpty(path)) { adt.Textures.Add(path); ti = adt.Textures.Count - 1; }
            chunk.Layers[l] = new Salmiak.Core.Formats.TextureLayer { TextureIndex = (uint)Math.Max(0, ti) };
        }
        for (int i = 0; i < 3; i++)
            chunk.AlphaMaps[i] = clip.Alphas[i] != null ? (byte[])clip.Alphas[i]!.Clone() : null;

        MarkEdited(adt);
        _terrain?.RebuildTile(adt, _texCache);
    }

    private void ToggleBlendMode()
    {
        if ((!EditMode && !TextureMode) || DoodadMode || WmoMode)
        {
            Notify?.Invoke("Enable terrain edit or texture mode to use the blend tool.");
            return;
        }
        _blendMode = !_blendMode;
        _blendSel.Clear();
        _compatVerts = 0;
        _areaMode = false;
        AutoPaintMode = false;
        Notify?.Invoke(_blendMode ? "Blend: click the first chunk (must share textures with the second)." : "Blend off.");
    }

    private void BlendSelectAt(int mx, int my)
    {
        if (!PickTerrain(mx, my, out var hit)) return;
        if (!TryGetChunkAt(hit, out var adt, out _, out int tileX, out int tileY, out int cCol, out int cRow)) return;

        var key = (adt, tileX, tileY, cRow, cCol);
        if (_blendSel.Contains(key)) return;
        _blendSel.Add(key);

        if (_blendSel.Count < 2)
        {
            RebuildCompatibleHighlight();
            Notify?.Invoke("Blend: green chunks share textures - click an adjacent one.");
            return;
        }

        BlendChunks(_blendSel[0], _blendSel[1]);
        _blendSel.Clear();
        RebuildCompatibleHighlight();
    }

    private void RebuildCompatibleHighlight()
    {
        _compatPts.Clear();
        _compatVerts = 0;
        if (_blendSel.Count != 1) { _compatBuf = Array.Empty<float>(); return; }

        var sel = _blendSel[0];
        var selChunk = sel.Adt.Chunks[sel.CRow, sel.CCol];
        if (selChunk == null) return;

        int selGCol = sel.TileX * AdtFile.ChunksPerSide + sel.CCol;
        int selGRow = sel.TileY * AdtFile.ChunksPerSide + sel.CRow;

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            int tileBaseCol = kv.Key.X * AdtFile.ChunksPerSide;
            int tileBaseRow = kv.Key.Y * AdtFile.ChunksPerSide;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null) continue;
                if (ReferenceEquals(adt, sel.Adt) && cy == sel.CRow && cx == sel.CCol) continue;
                if (Math.Abs((tileBaseCol + cx) - selGCol) + Math.Abs((tileBaseRow + cy) - selGRow) != 1) continue;
                if (UnionTextureCount(sel.Adt, selChunk, adt, chunk) > MapChunkMaxLayers) continue;
                AppendChunkOutlineLines(chunk, _compatPts);
            }
        }
        _compatBuf = _compatPts.ToArray();
        _compatVerts = _compatPts.Count / 3;
    }

    private void BlendChunks((AdtFile Adt, int TileX, int TileY, int CRow, int CCol) a,
                             (AdtFile Adt, int TileX, int TileY, int CRow, int CCol) b)
    {
        var ca = a.Adt.Chunks[a.CRow, a.CCol];
        var cb = b.Adt.Chunks[b.CRow, b.CCol];
        if (ca == null || cb == null) { Notify?.Invoke("Blend: chunk unavailable."); return; }

        int gColA = a.TileX * 16 + a.CCol, gRowA = a.TileY * 16 + a.CRow;
        int gColB = b.TileX * 16 + b.CCol, gRowB = b.TileY * 16 + b.CRow;
        int dCol = gColB - gColA, dRow = gRowB - gRowA;
        if (Math.Abs(dCol) + Math.Abs(dRow) != 1) { Notify?.Invoke("Blend: pick two edge-adjacent chunks."); return; }

        if (UnionTextureCount(a.Adt, ca, b.Adt, cb) > MapChunkMaxLayers)
        { Notify?.Invoke("Blend: chunks have too many combined textures (max 4)."); return; }

        var rec = new Stroke();
        rec.Textures[ca] = SnapshotTexState(ca); rec.Tiles.Add(a.Adt);
        rec.Textures[cb] = SnapshotTexState(cb); rec.Tiles.Add(b.Adt);
        PushUndo(rec);

        UnifyChunkLayers(a.Adt, ca, b.Adt, cb);

        var lo = (dCol + dRow > 0) ? a : b;
        var hi = (dCol + dRow > 0) ? b : a;
        var cLo = lo.Adt.Chunks[lo.CRow, lo.CCol]!;
        var cHi = hi.Adt.Chunks[hi.CRow, hi.CCol]!;
        bool vertical = dRow == 0;

        for (int l = 1; l < ca.NLayers; l++)
        {
            cLo.AlphaMaps[l - 1] ??= new byte[4096];
            cHi.AlphaMaps[l - 1] ??= new byte[4096];
            if (vertical) BlendSeamVertical(cLo.AlphaMaps[l - 1]!, cHi.AlphaMaps[l - 1]!);
            else BlendSeamHorizontal(cLo.AlphaMaps[l - 1]!, cHi.AlphaMaps[l - 1]!);
        }

        MakeCurrent();
        _terrain?.RebuildTile(a.Adt, _texCache);
        if (!ReferenceEquals(b.Adt, a.Adt)) _terrain?.RebuildTile(b.Adt, _texCache);
        MarkEdited(a.Adt); MarkEdited(b.Adt);
        Notify?.Invoke("Blended chunk seam. (Ctrl+Z to undo, Esc to exit)");
    }

    private static int UnionTextureCount(AdtFile a, Salmiak.Core.Formats.MapChunk ca, AdtFile b, Salmiak.Core.Formats.MapChunk cb)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int l = 0; l < ca.NLayers; l++) { var t = TexPath(a, ca, l); if (t.Length > 0) set.Add(t); }
        for (int l = 0; l < cb.NLayers; l++) { var t = TexPath(b, cb, l); if (t.Length > 0) set.Add(t); }
        return set.Count;
    }

    private static string TexPath(AdtFile adt, Salmiak.Core.Formats.MapChunk chunk, int l)
    {
        uint ti = chunk.Layers[l].TextureIndex;
        return ti < adt.Textures.Count ? adt.Textures[(int)ti] : "";
    }

    private void UnifyChunkLayers(AdtFile adtA, Salmiak.Core.Formats.MapChunk ca,
                                  AdtFile adtB, Salmiak.Core.Formats.MapChunk cb)
    {
        var union = new List<string>();
        void Add(string t) { if (t.Length > 0 && !union.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) union.Add(t); }
        for (int l = 0; l < ca.NLayers; l++) Add(TexPath(adtA, ca, l));
        for (int l = 0; l < cb.NLayers; l++) Add(TexPath(adtB, cb, l));
        if (union.Count == 0) return;
        ApplyUnion(adtA, ca, union);
        ApplyUnion(adtB, cb, union);
    }

    private void ApplyUnion(AdtFile adt, Salmiak.Core.Formats.MapChunk chunk, List<string> union)
    {
        string origBase = TexPath(adt, chunk, 0);
        var origAlpha = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        for (int l = 0; l < chunk.NLayers; l++) origAlpha[TexPath(adt, chunk, l)] = l == 0 ? null : chunk.AlphaMaps[l - 1];

        int n = Math.Min(union.Count, MapChunkMaxLayers);
        string unifiedBase = union[0];
        var newLayers = new Salmiak.Core.Formats.TextureLayer[MapChunkMaxLayers];
        var newAlpha = new byte[]?[3];
        for (int i = 0; i < n; i++)
        {
            string t = union[i];
            newLayers[i] = new Salmiak.Core.Formats.TextureLayer { TextureIndex = (uint)ResolveTex(adt, t) };
            if (i == 0) continue;

            var map = new byte[4096];
            if (string.Equals(t, origBase, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(origBase, unifiedBase, StringComparison.OrdinalIgnoreCase))
            {
                for (int k = 0; k < map.Length; k++) map[k] = 255;
            }
            else if (origAlpha.TryGetValue(t, out var oa) && oa != null)
            {
                map = (byte[])oa.Clone();
            }
            newAlpha[i - 1] = map;
        }

        chunk.NLayers = n;
        for (int i = 0; i < n; i++) chunk.Layers[i] = newLayers[i];
        for (int i = 0; i < 3; i++) chunk.AlphaMaps[i] = i < n - 1 ? newAlpha[i] : null;
    }

    private float SeamWeight(int d)
    {
        float t = (float)d / _blendBand;
        return _blendAggressive ? 1f - Smoothstep(0.5f, 1f, t) : 1f - t;
    }

    private void BlendSeamVertical(byte[] lo, byte[] hi)
    {
        for (int r = 0; r < 64; r++)
        for (int d = 0; d < _blendBand; d++)
        {
            int li = r * 64 + (63 - d), hidx = r * 64 + d;
            float a = lo[li], bb = hi[hidx], avg = (a + bb) * 0.5f, f = SeamWeight(d);
            lo[li] = (byte)MathF.Round(a + (avg - a) * f);
            hi[hidx] = (byte)MathF.Round(bb + (avg - bb) * f);
        }
    }

    private void BlendSeamHorizontal(byte[] lo, byte[] hi)
    {
        for (int c = 0; c < 64; c++)
        for (int d = 0; d < _blendBand; d++)
        {
            int li = (63 - d) * 64 + c, hidx = d * 64 + c;
            float a = lo[li], bb = hi[hidx], avg = (a + bb) * 0.5f, f = SeamWeight(d);
            lo[li] = (byte)MathF.Round(a + (avg - a) * f);
            hi[hidx] = (byte)MathF.Round(bb + (avg - bb) * f);
        }
    }
}
