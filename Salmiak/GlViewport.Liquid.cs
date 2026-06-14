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
    }

    private void SelectLiquidAt(int mx, int my)
    {
        _liqSel.Clear();
        _liqScrollStroke = null;
        if (!PickTerrain(mx, my, out var hit) || !TryGetChunkAt(hit, out var adt0, out var chunk0, out _, out _, out _, out _) ||
            chunk0.Liquid == null)
        { ModeChanged?.Invoke(ModeLabel()); return; }

        const float cs = AdtFile.ChunkSize;
        float level = chunk0.Liquid.Heights[40];
        var seen = new HashSet<MapChunk> { chunk0 };
        var queue = new Queue<(AdtFile Adt, MapChunk Chunk)>();
        queue.Enqueue((adt0, chunk0));
        while (queue.Count > 0 && _liqSel.Count < 4096)
        {
            var (a, ch) = queue.Dequeue();
            _liqSel.Add((a, ch));
            float ccx = -ch.Position.Y + cs / 2f, ccz = -ch.Position.X + cs / 2f;
            foreach (var (dx, dz) in new[] { (cs, 0f), (-cs, 0f), (0f, cs), (0f, -cs) })
            {
                if (!TryGetChunkAt(new Vector3(ccx + dx, 0f, ccz + dz), out var na, out var nch, out _, out _, out _, out _)) continue;
                if (nch.Liquid == null || seen.Contains(nch)) continue;
                if (MathF.Abs(nch.Liquid.Heights[40] - level) > 1.5f) continue;
                seen.Add(nch);
                queue.Enqueue((na, nch));
            }
        }
        _liqSelLevel = level;
        Notify?.Invoke($"Selected {WaterTypeName(chunk0.Liquid.Type)}: {_liqSel.Count} chunk(s) at {level:F1} - scroll = height, Del removes, Esc deselects.");
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
        Notify?.Invoke(oceanConverted
            ? $"Liquid surface: {_liqSelLevel:F2} - ocean converted to water (the game draws ocean at a fixed sea level)."
            : $"Liquid surface: {_liqSelLevel:F2}");
        ModeChanged?.Invoke(ModeLabel());
    }

    private bool _liqGizmoDrag;
    private int _liqGizmoLastY;
    private const float LiqGizmoDown = 4f, LiqGizmoUp = 12f;

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
        float level = hit.Y + _liqPlaceLift;
        float r = BrushRadius;
        var rec = new Stroke();
        var adts = new HashSet<AdtFile>();

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
                chunk.Liquid = MakeWater(level, _liqArmed);
                adts.Add(adt);
            }
        }
        if (rec.Liquid.Count == 0) { Notify?.Invoke("No empty chunks under the brush (existing liquid is kept - select it to edit)."); return; }
        PushUndo(rec);
        foreach (var a in adts) { MarkEdited(a); _terrain!.RebuildWater(a); }
        Notify?.Invoke(_liqArmed == 2 && MathF.Abs(level) > 1f
            ? $"Placed ocean on {rec.Liquid.Count} chunk(s) at {level:F1} - note: the game draws ocean at sea level (~0); use Water for custom heights."
            : $"Placed {WaterTypeName(_liqArmed)} on {rec.Liquid.Count} chunk(s) at {level:F1}.");
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

        if (_liqSel.Count > 0)
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
            float level = hit.Y + _liqPlaceLift;
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

    private void RemoveSelectedLiquid()
    {
        if (_liqSel.Count == 0) return;
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
        if (rec.Liquid.Count == 0) return;
        PushUndo(rec);
        foreach (var a in adts) { MarkEdited(a); _terrain!.RebuildWater(a); }
        Notify?.Invoke($"Removed liquid from {rec.Liquid.Count} chunk(s).");
        _liqSel.Clear();
        _liqScrollStroke = null;
        ModeChanged?.Invoke(ModeLabel());
    }

    private static string WaterTypeName(int t) => t switch { 2 => "ocean", 3 => "magma", 4 => "slime", _ => "water" };

    private static LiquidLayer MakeWater(float level, int type)
    {
        var liq = new LiquidLayer { Type = type < 1 ? 1 : type, MinHeight = level, MaxHeight = level };
        for (int i = 0; i < liq.Heights.Length; i++) liq.Heights[i] = level;
        for (int i = 0; i < liq.Render.Length; i++) liq.Render[i] = true;
        return liq;
    }
}
