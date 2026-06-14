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
    private bool _areaMode;
    private int _targetAreaId;
    private AreaTable? _areaTable;
    public int TargetAreaId => _targetAreaId;

    private readonly List<(float[] Verts, int Count, float R, float G, float B)> _zoneBatches = new();
    private int _zoneOverlayStamp = -1;
    private string _zoneClickedInfo = "";
    private (int Col, int Row)? _zoneRectAnchor;
    private (int Col, int Row) _zoneRectCur;

    public event Action? OpenNewZoneWizard;
    public void SetAreaId(int id)
    {
        _targetAreaId = Math.Max(0, id);
        string? name = AreaName(_targetAreaId);
        Notify?.Invoke($"Area-id paint: click a chunk to set area {_targetAreaId}{(name != null ? $" ({name})" : "")}.");
        ModeChanged?.Invoke(ModeLabel());
    }
    public event Action? OpenAreaPicker;

    private void EnsureAreaTable()
    {
        if (_areaTable != null || _mpq == null) return;
        try { _areaTable = AreaTable.Load(p => _mpq.OpenFile(p)); }
        catch (Exception ex) { Notify?.Invoke($"AreaTable.dbc: {ex.Message}"); }
    }

    public string? AreaName(int id) { EnsureAreaTable(); return _areaTable?.Name(id); }

    public System.Collections.Generic.IReadOnlyList<(int Id, string Name)> AreaList()
    { EnsureAreaTable(); return _areaTable?.Named ?? System.Array.Empty<(int, string)>(); }

    public int NewZoneCount => _areaTable?.NewZones.Count ?? 0;

    private void ToggleAreaMode()
    {
        if (DoodadMode || WmoMode) { Notify?.Invoke("Exit doodad/WMO mode to use zone mode."); return; }
        _areaMode = !_areaMode;
        _blendMode = false; GenerateMode = false; RoadMode = false; AutoPaintMode = false;
        if (_areaMode)
        {
            _targetAreaId = 0;
            _zoneClickedInfo = "";
            RebuildZoneOverlay();
            Notify?.Invoke("Zone mode - click a chunk to see its zone; middle-click to arm one for painting; N = new zone.");
            ModeChanged?.Invoke(ModeLabel());
            return;
        }
        _zoneBatches.Clear();
        _zoneRectAnchor = null;
        Notify?.Invoke("Zone mode off.");
        ModeChanged?.Invoke(ModeLabel());
    }

    private void AreaPaintAt(int mx, int my)
    {
        if (_terrain == null || _wdt == null) return;
        if (!PickTerrain(mx, my, out var hit)) return;
        if (!TryGetChunkAt(hit, out var adt, out var chunk, out _, out _, out _, out _)) return;

        if (_targetAreaId <= 0)
        {
            string? cn = AreaName(chunk.AreaId);
            _zoneClickedInfo = chunk.AreaId == 0
                ? "Zone: none (area id 0)"
                : $"Zone: {cn ?? "unknown"}  (id {chunk.AreaId})";
            Notify?.Invoke(_zoneClickedInfo + "   ·   middle-click / N to arm a zone for painting");
            return;
        }

        if (chunk.AreaId == _targetAreaId) return;
        var rec = new Stroke();
        rec.Area[chunk] = chunk.AreaId;
        PushUndo(rec);
        chunk.AreaId = _targetAreaId;
        MarkEdited(adt);
        RebuildZoneOverlay();
        string? name = AreaName(_targetAreaId);
        Notify?.Invoke($"Chunk zone set to {_targetAreaId}{(name != null ? $" ({name})" : "")}.");
    }

    private void ZoneRectPaint((int Col, int Row) a, (int Col, int Row) b)
    {
        if (_targetAreaId <= 0)
        { Notify?.Invoke("Zone rectangle: arm a zone first (middle-click picker or N = new zone)."); return; }
        int c0 = Math.Min(a.Col, b.Col), c1 = Math.Max(a.Col, b.Col);
        int r0 = Math.Min(a.Row, b.Row), r1 = Math.Max(a.Row, b.Row);

        var rec = new Stroke();
        int painted = 0;
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            var (tx, ty) = kv.Key;
            bool touched = false;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                int gc = tx * AdtFile.ChunksPerSide + cx, gr = ty * AdtFile.ChunksPerSide + cy;
                if (gc < c0 || gc > c1 || gr < r0 || gr > r1) continue;
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null || chunk.AreaId == _targetAreaId) continue;
                if (!rec.Area.ContainsKey(chunk)) rec.Area[chunk] = chunk.AreaId;
                chunk.AreaId = _targetAreaId;
                touched = true;
                painted++;
            }
            if (touched) MarkEdited(adt);
        }
        if (painted == 0) { Notify?.Invoke("Zone rectangle: no chunks changed."); return; }
        PushUndo(rec);
        RebuildZoneOverlay();
        string? name = AreaName(_targetAreaId);
        Notify?.Invoke($"Painted {painted} chunk(s) with zone {_targetAreaId}{(name != null ? $" ({name})" : "")}.");
    }

    private static (float R, float G, float B) ZoneColor(int areaId)
    {
        float h = (areaId * 0.61803398875f) % 1f;
        float x = 1f - MathF.Abs(h * 6f % 2f - 1f);
        return ((int)(h * 6f)) switch
        {
            0 => (1f, x, 0.15f), 1 => (x, 1f, 0.15f), 2 => (0.15f, 1f, x),
            3 => (0.15f, x, 1f), 4 => (x, 0.15f, 1f), _ => (1f, 0.15f, x),
        };
    }

    private void RebuildZoneOverlay()
    {
        _zoneBatches.Clear();
        _zoneOverlayStamp = _tileCache.Count;
        var byZone = new Dictionary<int, List<float>>();
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null) continue;
                if (!byZone.TryGetValue(chunk.AreaId, out var list)) byZone[chunk.AreaId] = list = new List<float>();
                AppendChunkOutlineLines(chunk, list);
            }
        }
        foreach (var kv in byZone)
        {
            var (r, g, b) = kv.Key == 0 ? (0.45f, 0.45f, 0.45f) : ZoneColor(kv.Key);
            _zoneBatches.Add((kv.Value.ToArray(), kv.Value.Count / 3, r, g, b));
        }
    }

    public int CreateZone(string name, uint music, uint ambience, uint introSound)
    {
        EnsureAreaTable();
        if (_areaTable == null) { Notify?.Invoke("New zone: AreaTable.dbc unavailable."); return 0; }
        int mapId = 0;
        try { mapId = ResolveMapId(); } catch { }
        int id = _areaTable.AddZone(name, mapId, music, ambience, introSound);
        _targetAreaId = id;
        var z = _areaTable.NewZones[^1];
        Notify?.Invoke($"Zone '{z.Name}' created (id {id}, explore flag {z.Flag}) and armed - " +
                       "click / Shift+drag chunks to paint it. Export bundles the patched AreaTable.dbc.");
        ModeChanged?.Invoke(ModeLabel());
        return id;
    }
}
