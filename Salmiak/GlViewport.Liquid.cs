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
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool LiquidMode { get; set; }

    private readonly List<(AdtFile Adt, MapChunk Chunk)> _liqSel = new();
    private float _liqSelLevel;
    private int _liqArmed;
    private float _liqPlaceLift = 1.5f;
    //The surface the next click writes, as an absolute map height. NaN means "read the terrain", so
    //the first click of a run takes its height from the ground under the cursor; from then on that
    //level is kept. Deriving it on every click is what turned a long river into one group of chunks
    //per click, each group sitting where the cursor happened to be.
    private float _liqLevel = float.NaN;
    //Re-levelling a selection: one plane, or keep each chunk's own offset and move it by a delta.
    private bool _liqFlatten = true;
    //Selection stops where the bed rises above the water, which is what kept separately placed
    //groups of one river apart. With this on, one click grabs the whole connected liquid instead.
    private bool _liqJoinLevels;
    //Chunks the surface may flow into past the brush, walking down the bed. Off by default: a click
    //stays a click, and Extend along bed walks the same rule on demand when you want the run filled.
    private int _liqFlowReach;
    //How far one Extend along bed walks. Not the slider: pressing the button means "keep following
    //this bed", and the walk already stops by itself wherever the terrain rises above the surface.
    private const int ExtendBedReach = 1024;
    private Stroke? _liqScrollStroke;
    private long _liqScrollLastMs;
    private float[] _liqOverlayVerts = [];

    public event Action? OpenLiquidPicker;

    public void ArmLiquid(int type)
    {
        _liqArmed = Math.Clamp(type, 0, 4);
        _liqSel.Clear();
        Notify?.Invoke(_liqArmed == 0 ? "Liquid placement disarmed."
            : $"Placing {WaterTypeName(_liqArmed)} - LMB fills under the brush, scroll = surface height, Esc stops.");
        ModeChanged?.Invoke(ModeLabel());
    }

    //Everything the liquid sidebar reads and writes, so the panel can never disagree with what the
    //next click will do: the level placement uses, and the options that decide how a body welds and
    //how a re-level is applied.
    public bool LiquidPlacing => _liqArmed != 0;
    public int LiquidArmedType => _liqArmed;
    public int LiquidSelectedChunks => _liqSel.Count;
    public float LiquidSelectedLevel => _liqSelLevel;
    public float LiquidLevel => _liqLevel;
    public float LiquidPlaceLift => _liqPlaceLift;
    public bool LiquidFlattenSelected => _liqFlatten;
    public bool LiquidJoinLevels => _liqJoinLevels;
    public int LiquidFlowReach => _liqFlowReach;

    public void SetLiquidLevel(double level)
    {
        _liqLevel = (float)Math.Clamp((double)level, -4000d, 4000d);
        _liqSelLevel = _liqLevel;
        Notify?.Invoke($"Liquid level set to {_liqLevel:F2} - that is the height every click writes from now on.");
        ModeChanged?.Invoke(ModeLabel());
    }

    public void SetLiquidLevelFixed(bool on)
    {
        if (!on) { _liqLevel = float.NaN; }
        else if (float.IsNaN(_liqLevel)) _liqLevel = _liqSelLevel;
        Notify?.Invoke(on ? $"Placement level pinned at {_liqLevel:F2} - later clicks write it whatever the ground there does."
            : "Placement level back to auto: the first click takes the terrain, the later ones reuse it.");
        ModeChanged?.Invoke(ModeLabel());
    }

    public void SetLiquidPlaceLift(double lift) => _liqPlaceLift = (float)Math.Clamp((double)lift, -60d, 200d);
    public void SetLiquidFlatten(bool on) => _liqFlatten = on;
    public void SetLiquidJoinLevels(bool on) => _liqJoinLevels = on;
    public void SetLiquidFlowReach(int chunks) => _liqFlowReach = Math.Clamp(chunks, 0, 512);

    //Exact absolute surface over the whole selection, and the actions the sidebar offers.
    public bool ApplyLiquidLevel(double level) => LevelSelection((float)level);
    public bool LevelLiquidToCursor()
    {
        if (_liqSel.Count == 0) { Notify?.Invoke("Level to cursor: select a liquid first - click one of its chunks."); return false; }
        if (!PickTerrain(_mouseX, _mouseY, out var hit)) { Notify?.Invoke("Level to cursor: no terrain under the cursor."); return false; }
        return LevelSelection(hit.Y);
    }

    //Selection follows one connected body of liquid. Adjacency is the river, not the height: a
    //neighbour at another level is still the same body wherever the bed under their shared boundary
    //sits under the water, so the groups a run of brush clicks left behind come back as one selection
    //and one re-level reaches all of them. Join across levels drops the bed test as well.
    private void SelectLiquidAt(int mx, int my)
    {
        _liqSel.Clear();
        _liqScrollStroke = null;
        _liqLevel = float.NaN;
        if (!PickTerrain(mx, my, out var hit) || !TryGetChunkAt(hit, out var adt0, out var chunk0, out _, out _, out _, out _) ||
            chunk0.Liquid == null)
        { ModeChanged?.Invoke(ModeLabel()); return; }

        const float cs = AdtFile.ChunkSize;
        float level = chunk0.Liquid.Heights[40];
        int wantType = chunk0.Liquid.Type;
        var seen = new HashSet<MapChunk> { chunk0 };
        var queue = new Queue<(AdtFile Adt, MapChunk Chunk)>();
        queue.Enqueue((adt0, chunk0));
        while (queue.Count > 0 && _liqSel.Count < 4096)
        {
            var (a, ch) = queue.Dequeue();
            _liqSel.Add((a, ch));
            float ccx = -ch.Position.Y + cs / 2f, ccz = -ch.Position.X + cs / 2f;
            float here = ch.Liquid?.Heights[40] ?? level;
            foreach (var (dx, dz) in new[] { (cs, 0f), (-cs, 0f), (0f, cs), (0f, -cs) })
            {
                if (!TryGetChunkAt(new Vector3(ccx + dx, 0f, ccz + dz), out var na, out var nch, out _, out _, out _, out _)) continue;
                if (nch.Liquid == null || seen.Contains(nch) || nch.Liquid.Type != wantType) continue;
                float there = nch.Liquid.Heights[40];
                //A neighbour at another height is still the same river when the bed under the shared
                //boundary sits under the water, so the groups a run of clicks left behind are selected
                //as one body and one re-level reaches all of them. A bank or a waterfall lip that
                //rises above the surface keeps them apart; Join across levels ignores the bed.
                if (!_liqJoinLevels && MathF.Abs(there - here) > 1.5f &&
                    !BoundaryWet(ch, nch, dx, dz, MathF.Max(here, there))) continue;
                seen.Add(nch);
                queue.Enqueue((na, nch));
            }
        }
        _liqSelLevel = level;
        Notify?.Invoke($"Selected {WaterTypeName(chunk0.Liquid.Type)}: {_liqSel.Count} chunk(s) at {level:F1} - scroll = height, Del removes, Esc deselects." +
                       (_liqJoinLevels ? " Every chunk of this liquid is included, banks and all."
                       : " Chunks the bed keeps dry stay separate until you tick Join across levels."));
        ModeChanged?.Invoke(ModeLabel());
    }

    private void AdjustSelectedLiquid(float delta)
    {
        if (_liqSel.Count == 0) return;
        long now = _sw.ElapsedMilliseconds;
        if (_liqScrollStroke == null || now - _liqScrollLastMs > 800)
        {
            var rec = new Stroke();
            foreach (var (a, ch) in _liqSel) { rec.Liquid[ch] = ch.Liquid; rec.Tiles.Add(a); }
            PushUndo(rec);
            _liqScrollStroke = rec;
        }
        _liqScrollLastMs = now;

        var adts = new HashSet<AdtFile>();
        bool oceanConverted = false;
        foreach (var (a, ch) in _liqSel)
        {
            if (ch.Liquid is not { } lq) continue;
            int type = lq.Type;
            if (type == 2) { type = 1; oceanConverted = true; }
            ch.Liquid = ShiftedLiquid(lq, delta, type);
            adts.Add(a);
        }
        foreach (var a in adts) { MarkEdited(a); _terrain!.RebuildWater(a); }
        _liqSelLevel += delta;
        //A placement run that already pinned a level follows the drag, so the next click keeps
        //writing the height you just moved to rather than the one you started from.
        if (!float.IsNaN(_liqLevel)) _liqLevel = _liqSelLevel;
        Notify?.Invoke(oceanConverted
            ? $"Liquid surface: {_liqSelLevel:F2} - ocean converted to water (the game draws ocean at a fixed sea level)."
            : $"Liquid surface: {_liqSelLevel:F2}");
        ModeChanged?.Invoke(ModeLabel());
    }

    private bool _liqGizmoDrag;
    private int _liqGizmoLastY;
    private const float LiqGizmoDown = 4f, LiqGizmoUp = 12f;
    //The client stores each water vertex as a height above the terrain in one byte, in eighths of a
    //yard, so the surface cannot sit more than this over the bed: deeper chunks draw lower in game.
    private const float MaxLiquidDepth = 255f / 8f;

    private Vector3 LiquidGizmoAnchor()
    {
        const float cs = AdtFile.ChunkSize;
        float sx = 0f, sz = 0f;
        foreach (var (_, ch) in _liqSel) { sx += -ch.Position.Y + cs / 2f; sz += -ch.Position.X + cs / 2f; }
        return new Vector3(sx / _liqSel.Count, _liqSelLevel, sz / _liqSel.Count);
    }

    private bool LiquidGizmoHit(int mx, int my)
    {
        if (_liqSel.Count == 0) return false;
        var a = LiquidGizmoAnchor();
        if (!WorldToScreen(a + new Vector3(0f, -LiqGizmoDown, 0f), out var s0)) return false;
        if (!WorldToScreen(a + new Vector3(0f, LiqGizmoUp, 0f), out var s1)) return false;
        return PointToSegment(new Vector2(mx, my), s0, s1) < 12f;
    }

    private void DragLiquidGizmo(int my)
    {
        int dy = _liqGizmoLastY - my;
        if (dy == 0 || _liqSel.Count == 0) return;
        _liqGizmoLastY = my;
        float dist = (LiquidGizmoAnchor() - _camera.Position).Length;
        float yardsPerPixel = 2f * dist * MathF.Tan(MathHelper.DegreesToRadians(60f) * 0.5f) / Math.Max(1, Height);
        AdjustSelectedLiquid(dy * yardsPerPixel);
    }

    private static LiquidLayer ShiftedLiquid(LiquidLayer lq, float delta, int? newType = null)
    {
        var moved = new LiquidLayer { Type = newType ?? lq.Type, MinHeight = lq.MinHeight + delta, MaxHeight = lq.MaxHeight + delta };
        for (int i = 0; i < lq.Heights.Length; i++) moved.Heights[i] = lq.Heights[i] + delta;
        for (int i = 0; i < lq.Render.Length; i++) moved.Render[i] = lq.Render[i];
        return moved;
    }

    private void PlaceLiquidAt(int mx, int my)
    {
        if (_liqArmed == 0 || !PickTerrain(mx, my, out var hit)) return;
        //The level is one value for the whole run of clicks, not a fresh terrain reading per click:
        //a river that runs away from the cursor is then still one surface, at one height.
        float level = PlacementLevel(hit);
        float r = BrushRadius;
        var rec = new Stroke();
        var adts = new HashSet<AdtFile>();
        var placed = new List<(AdtFile Adt, MapChunk Chunk)>();

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value; if (adt == null) continue;
            if (!TileOverlapsBrush(kv.Key, hit, r)) continue;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null || chunk.Liquid != null || !BrushHitsChunk(chunk, hit, r)) continue;
                rec.Liquid[chunk] = null;
                rec.Tiles.Add(adt);
                chunk.Liquid = MakeLevelled(_liqArmed, level, chunk).Layer;
                adts.Add(adt);
                placed.Add((adt, chunk));
            }
        }
        //Then let the surface walk down the bed: a river that stretches past the brush is filled by
        //the flow, not by a new click every 3 chunks.
        var flowedTo = new List<(AdtFile Adt, MapChunk Chunk)>(placed);
        int flowed = FlowLiquidInto(flowedTo, level, _liqArmed, rec, adts, _liqFlowReach);

        if (rec.Liquid.Count == 0) { Notify?.Invoke("No empty chunks under the brush (existing liquid is kept - select it to edit)."); return; }
        PushUndo(rec);
        foreach (var a in adts) { MarkEdited(a); _terrain!.RebuildWater(a); }

        //Placed chunks stay selected, so the next click grows the same body and a re-level or a
        //remove covers everything placed so far rather than only the last handful.
        var have = new HashSet<MapChunk>();
        foreach (var (_, ch) in _liqSel) have.Add(ch);
        foreach (var p in flowedTo) if (have.Add(p.Chunk)) _liqSel.Add(p);
        _liqSelLevel = _liqLevel = level;

        Notify?.Invoke(_liqArmed == 2 && MathF.Abs(level) > 1f
            ? $"Placed ocean on {placed.Count} chunk(s) at {level:F1} - note: the game draws ocean at sea level (~0); use Water for custom heights."
            : $"Placed {WaterTypeName(_liqArmed)} on {placed.Count} chunk(s) at {level:F1}" +
              (flowed > 0 ? $" + {flowed} along the bed" : "") + " - every later click keeps that height.");
        ModeChanged?.Invoke(ModeLabel());
    }

    //NaN means "read the ground under the cursor", which is how the first click of a run behaves.
    private float PlacementLevel(Vector3 hit) =>
        float.IsNaN(_liqLevel) ? hit.Y + _liqPlaceLift : _liqLevel;

    //Water flows past the brush into the chunks whose boundary with a wet neighbour sits below the
    //surface: a bank or a waterfall lip that rises above it stops the walk, so a river run gets
    //filled to the next rapid while a flat shore does not bleed sideways across the map.
    private int FlowLiquidInto(List<(AdtFile Adt, MapChunk Chunk)> seeds, float level, int type,
                               Stroke rec, HashSet<AdtFile> adts, int reach)
    {
        if (seeds.Count == 0 || rec == null || reach <= 0) return 0;
        const float cs = AdtFile.ChunkSize;
        var seen = new HashSet<MapChunk>();
        var queue = new Queue<(AdtFile Adt, MapChunk Chunk)>();
        foreach (var s in seeds) seen.Add(s.Chunk);
        foreach (var s in seeds) queue.Enqueue(s);
        int added = 0;
        while (queue.Count > 0 && added < reach)
        {
            var (a, ch) = queue.Dequeue();
            float ccx = -ch.Position.Y + cs / 2f, ccz = -ch.Position.X + cs / 2f;
            foreach (var (dx, dz) in new[] { (cs, 0f), (-cs, 0f), (0f, cs), (0f, -cs) })
            {
                if (!TryGetChunkAt(new Vector3(ccx + dx, 0f, ccz + dz), out var na, out var nch, out _, out _, out _, out _)) continue;
                if (nch.Liquid != null || seen.Contains(nch)) continue;
                if (!BoundaryWet(ch, nch, dx, dz, level)) continue;
                seen.Add(nch);
                rec.Liquid[nch] = null;
                rec.Tiles.Add(a);
                rec.Tiles.Add(na);
                nch.Liquid = MakeLevelled(type, level, nch).Layer;
                adts.Add(a);
                adts.Add(na);
                added++;
                seeds.Add((na, nch));
                queue.Enqueue((na, nch));
            }
        }
        return added;
    }

    //MCLQ works on a coarse 9x9 vertex grid per chunk, 8 yd apart, and the same grid decides where
    //the water is: a boundary lets the surface through when one of its vertices is under it on both
    //sides, which is exactly the line the water would cross.
    private bool BoundaryWet(MapChunk from, MapChunk to, float dx, float dz, float level)
    {
        bool alongX = dx != 0f;
        for (int i = 0; i < 9; i++)
        {
            int fc = alongX ? (dx > 0f ? 8 : 0) : i;
            int fr = alongX ? i : (dz > 0f ? 8 : 0);
            int tc = alongX ? (dx > 0f ? 0 : 8) : i;
            int tr = alongX ? i : (dz > 0f ? 0 : 8);
            if (level > ChunkVertexGround(from, fr, fc) && level > ChunkVertexGround(to, tr, tc)) return true;
        }
        return false;
    }

    //Ground at one of the 9x9 vertices the liquid grid uses, in the same world height the water
    //levels are stored in.
    private static float ChunkVertexGround(MapChunk ch, int row, int col) =>
        ch.Position.Z + ch.Heights[row * 17 + col];

    //Flat water at an absolute level. Tiles whose ground already sits above the surface are left
    //unrendered rather than drawing water buried in the bank, which is what keeps the shoreline on
    //the terrain line instead of on the chunk grid. TooDeep says the chunk is deeper than the
    //detail byte the client stores the surface in can carry, so in game the surface draws lower.
    private (LiquidLayer Layer, bool TooDeep) MakeLevelled(int type, float level, MapChunk ch)
    {
        var flat = new LiquidLayer { Type = type < 1 ? 1 : type, MinHeight = level, MaxHeight = level };
        bool deep = false;
        for (int i = 0; i < flat.Heights.Length; i++)
        {
            flat.Heights[i] = level;
            if (level - ChunkVertexGround(ch, i / 9, i % 9) > MaxLiquidDepth) deep = true;
        }
        for (int i = 0; i < flat.Render.Length; i++)
        {
            int row = i / 8, col = i % 8;
            var low = Math.Min(Math.Min(ChunkVertexGround(ch, row, col), ChunkVertexGround(ch, row, col + 1)),
                               Math.Min(ChunkVertexGround(ch, row + 1, col), ChunkVertexGround(ch, row + 1, col + 1)));
            flat.Render[i] = low < level + 0.05f;
        }
        return (flat, deep);
    }

    //One exact height across the whole selection. Flatten makes the body a single plane, which is
    //what turns several separately placed groups of one river into one level; with the flatten
    //option off every chunk only moves by its own delta and the height differences stay.
    private bool LevelSelection(float level)
    {
        if (_liqSel.Count == 0) { Notify?.Invoke("Level: select a liquid first - click one of its chunks."); return false; }
        var rec = new Stroke();
        var adts = new HashSet<AdtFile>();
        int capped = 0;
        bool ocean = false;
        foreach (var (a, ch) in _liqSel)
        {
            if (ch.Liquid is not { } lq) continue;
            rec.Liquid[ch] = lq;
            rec.Tiles.Add(a);
            int type = lq.Type;
            if (type == 2) { type = 1; ocean = true; }
            if (_liqFlatten)
            {
                var (flat, deep) = MakeLevelled(type, level, ch);
                ch.Liquid = flat;
                if (deep) capped++;
            }
            else ch.Liquid = ShiftedLiquid(lq, level - lq.Heights[40], type);
            adts.Add(a);
        }
        if (rec.Liquid.Count == 0) return false;
        PushUndo(rec);
        foreach (var a in adts) { MarkEdited(a); _terrain!.RebuildWater(a); }
        _liqSelLevel = _liqLevel = level;
        Notify?.Invoke((ocean ? "Ocean turned into water (the game draws ocean at a fixed sea level). " : "") +
                       (_liqFlatten ? $"Selected liquid is now one level at {level:F2}."
                                    : $"Selected liquid moved to {level:F2}, each chunk keeping its own offset.") +
                       (capped > 0 ? $"\n{capped} chunk(s) are deeper than {MaxLiquidDepth:F1} yd, where the client's own surface byte caps the water." : ""));
        ModeChanged?.Invoke(ModeLabel());
        return true;
    }

    //Walks the surface further down the bed from everything that is selected, so pressing it again
    //keeps following the same river instead of asking for another click into the bed.
    public bool ExtendLiquidBed()
    {
        if (_liqSel.Count == 0) { Notify?.Invoke("Extend: select a liquid first - click one of its chunks."); return false; }
        float level = float.IsNaN(_liqLevel) ? _liqSelLevel : _liqLevel;
        var type = _liqSel[0].Chunk.Liquid?.Type ?? 1;
        var rec = new Stroke();
        var adts = new HashSet<AdtFile>();
        var seeds = new List<(AdtFile Adt, MapChunk Chunk)>(_liqSel);
        int flowed = FlowLiquidInto(seeds, level, type, rec, adts, ExtendBedReach);
        if (flowed == 0) { Notify?.Invoke("Extend: the bed does not continue under the surface - nothing to grow."); return false; }
        PushUndo(rec);
        foreach (var a in adts) { MarkEdited(a); _terrain!.RebuildWater(a); }
        var have = new HashSet<MapChunk>();
        foreach (var (_, ch) in _liqSel) have.Add(ch);
        foreach (var p in seeds) if (have.Add(p.Chunk)) _liqSel.Add(p);
        Notify?.Invoke($"Extended the liquid {flowed} chunk(s) along the bed at {level:F2} - Ctrl+Z undoes.");
        ModeChanged?.Invoke(ModeLabel());
        return true;
    }

    private void DrawLiquidOverlay(float aspect)
    {
        const float cs = AdtFile.ChunkSize;
        var verts = new List<float>();
        void Rect(float minX, float minZ, float y, bool diag)
        {
            void Seg(float ax, float az, float bx, float bz)
            { verts.Add(ax); verts.Add(y); verts.Add(az); verts.Add(bx); verts.Add(y); verts.Add(bz); }
            Seg(minX, minZ, minX + cs, minZ); Seg(minX + cs, minZ, minX + cs, minZ + cs);
            Seg(minX + cs, minZ + cs, minX, minZ + cs); Seg(minX, minZ + cs, minX, minZ);
            if (diag) { Seg(minX, minZ, minX + cs, minZ + cs); Seg(minX + cs, minZ, minX, minZ + cs); }
        }
        void Flush(float r, float g, float b)
        {
            if (verts.Count < 6) { verts.Clear(); return; }
            if (_liqOverlayVerts.Length < verts.Count) _liqOverlayVerts = new float[verts.Count];
            verts.CopyTo(_liqOverlayVerts);
            _brush!.RenderColored(_camera, aspect, _liqOverlayVerts, verts.Count / 3, PrimitiveType.Lines, r, g, b);
            verts.Clear();
        }

        foreach (var (_, ch) in _liqSel)
        {
            if (ch.Liquid is not { } lq) continue;
            Rect(-ch.Position.Y, -ch.Position.X, lq.Heights[40] + 0.2f, diag: false);
        }
        Flush(0.25f, 1f, 0.5f);

        if (_liqArmed == 0 && _liqSel.Count > 0)
        {
            var a = LiquidGizmoAnchor();
            void Seg3(Vector3 p, Vector3 q)
            { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(q.X); verts.Add(q.Y); verts.Add(q.Z); }
            var bot = a + new Vector3(0f, -LiqGizmoDown, 0f);
            var top = a + new Vector3(0f, LiqGizmoUp, 0f);
            Seg3(bot, top);
            const float barb = 2.5f;
            foreach (var (bx, bz) in new[] { (barb, 0f), (-barb, 0f), (0f, barb), (0f, -barb) })
            {
                Seg3(top, top + new Vector3(bx, -barb, bz));
                Seg3(bot, bot + new Vector3(bx, barb, bz));
            }
            Seg3(a + new Vector3(-barb, 0f, 0f), a + new Vector3(barb, 0f, 0f));
            Seg3(a + new Vector3(0f, 0f, -barb), a + new Vector3(0f, 0f, barb));
            bool hot = _liqGizmoDrag || LiquidGizmoHit(_mouseX, _mouseY);
            Flush(1f, hot ? 1f : 0.8f, hot ? 0.3f : 0.1f);
        }

        if (_liqArmed != 0 && !CursorOverMinimap() && PickTerrain(_mouseX, _mouseY, out var hit))
        {
            float level = PlacementLevel(hit);
            float r = BrushRadius;
            foreach (var kv in _tileCache)
            {
                var adt = kv.Value; if (adt == null) continue;
                if (!TileOverlapsBrush(kv.Key, hit, r)) continue;
                for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
                for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
                {
                    var chunk = adt.Chunks[cy, cx];
                    if (chunk == null || chunk.Liquid != null || !BrushHitsChunk(chunk, hit, r)) continue;
                    Rect(-chunk.Position.Y, -chunk.Position.X, level, diag: true);
                }
            }
            var (cr, cg, cb) = _liqArmed switch
            {
                3 => (1f, 0.45f, 0.1f),
                4 => (0.4f, 0.9f, 0.2f),
                _ => (0.3f, 0.7f, 1f),
            };
            Flush(cr, cg, cb);
        }
    }

    public bool RemoveSelectedLiquid()
    {
        if (_liqSel.Count == 0) return false;
        var rec = new Stroke();
        var adts = new HashSet<AdtFile>();
        foreach (var (a, ch) in _liqSel)
        {
            if (ch.Liquid == null) continue;
            rec.Liquid[ch] = ch.Liquid;
            rec.Tiles.Add(a);
            ch.Liquid = null;
            adts.Add(a);
        }
        if (rec.Liquid.Count == 0) return false;
        PushUndo(rec);
        foreach (var a in adts) { MarkEdited(a); _terrain!.RebuildWater(a); }
        Notify?.Invoke($"Removed liquid from {rec.Liquid.Count} chunk(s).");
        _liqSel.Clear();
        _liqScrollStroke = null;
        ModeChanged?.Invoke(ModeLabel());
        return true;
    }

    private static string WaterTypeName(int t) => t switch { 2 => "ocean", 3 => "magma", 4 => "slime", _ => "water" };
}
