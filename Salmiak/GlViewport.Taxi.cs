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
    public bool FlightMode { get; set; }
    public event Action<bool>? FlightPathsChanged;
    private float[] _flightVerts = Array.Empty<float>();
    private int _flightVertCount;
    private int _flightBuiltContinent = -2;
    private Salmiak.Core.Formats.FlightPaths? _flightData;
    private Salmiak.Core.Formats.FlightPaths.Path? _selectedPath;
    private int _selectedNode = -1;
    private bool _draggingNode;
    private float[] _selPathVerts = Array.Empty<float>();
    private int _selPathVertCount;
    private float[] _wpVerts = Array.Empty<float>();
    private int _wpVertCount;
    private float[] _stopVerts = Array.Empty<float>();
    private int _stopVertCount;
    private float[] _selNodeVerts = new float[6 * 3];
    private int _selNodeVertCount;
    private readonly List<FlightSnapshot> _flightUndo = new();
    private readonly List<FlightSnapshot> _flightRedo = new();

    private sealed record FlightSnapshot(
        List<Salmiak.Core.Formats.FlightPaths.Path> Paths,
        List<Salmiak.Core.Formats.FlightPaths.Node> Nodes);
    private bool _prevWpHeight;
    private bool _dragUndoTaken;

    private bool _flightDrawing;
    private string _flightDrawMsg = "";
    private bool _prevFpEnter;

    public event Action? OpenFlightPathWizard;

    private static int ContinentOf(string? map) => map?.ToLowerInvariant() switch
    { "azeroth" => 0, "kalimdor" => 1, _ => -1 };

    public bool ToggleFlightPaths()
    {
        if (FlightMode) { SetPrimaryMode(EditorMode.None); return false; }
        if (ContinentOf(_mapName) < 0)
        {
            Notify?.Invoke("Taxi mode: load Azeroth or Kalimdor first.");
            FlightPathsChanged?.Invoke(false);
            return false;
        }
        SetPrimaryMode(EditorMode.FlightPath);
        return FlightMode;
    }

    private void EnterFlightMode()
    {
        int continent = ContinentOf(_mapName);
        if (continent < 0 || _mpq == null) return;
        if (_flightData == null || _flightBuiltContinent != continent)
        {
            try { _flightData = Salmiak.Core.Formats.FlightPaths.Load(p => _mpq.OpenFile(p)); }
            catch (Exception ex) { Notify?.Invoke($"Flight paths: {ex.Message}"); _flightData = null; return; }
            _flightBuiltContinent = continent;
            _flightUndo.Clear(); _flightRedo.Clear();
        }
        ApplyPendingFlightEdits();
        RebuildFlightGeometry();
        int paths = 0; foreach (var p in _flightData.Paths) if (p.Continent == continent) paths++;
        Notify?.Invoke($"Taxi mode - {paths} paths (arrows show direction). Click a path; drag waypoints; N = new flight path / zeppelin / boat.");
    }

    private MapEditFile.FlightEdits? _pendingFlightEdits;

    private void ApplyPendingFlightEdits()
    {
        if (_flightData == null || _pendingFlightEdits == null) return;
        var pend = _pendingFlightEdits;
        _pendingFlightEdits = null;

        int applied = 0;
        foreach (var n in pend.Nodes)
            if (!_flightData.Nodes.Exists(q => q.Id == n.Id))
            { _flightData.Nodes.Add(n); applied++; }

        foreach (var p in pend.Paths)
        {
            var existing = _flightData.Paths.Find(q => q.Id == p.Id);
            if (p.IsNew)
            {
                if (existing == null) { _flightData.Paths.Add(p); applied++; }
            }
            else if (existing != null)
            {
                existing.Points.Clear();
                existing.Points.AddRange(p.Points);
                existing.Dirty = true;
                applied++;
            }
        }
        if (applied > 0)
        {
            RebuildFlightGeometry();
            Notify?.Invoke($"Restored {applied} taxi/transport edit(s) from the project.");
        }
    }

    private void RebuildFlightGeometry()
    {
        _flightVertCount = 0;
        if (_flightData == null) return;
        int continent = ContinentOf(_mapName);
        var verts = new List<float>();
        foreach (var path in _flightData.Paths)
        {
            if (path.Continent != continent) continue;
            for (int i = 1; i < path.Points.Count; i++)
            {
                var a = WorldToGl(path.Points[i - 1]); var b = WorldToGl(path.Points[i]);
                verts.Add(a.X); verts.Add(a.Y); verts.Add(a.Z);
                verts.Add(b.X); verts.Add(b.Y); verts.Add(b.Z);
                AppendArrow(verts, a, b, FlightArrow);
            }
        }
        foreach (var n in _flightData.Nodes)
        {
            if (n.MapId != continent) continue;
            var g = WorldToGl((n.X, n.Y, n.Z));
            verts.Add(g.X); verts.Add(g.Y); verts.Add(g.Z);
            verts.Add(g.X); verts.Add(g.Y + 60f); verts.Add(g.Z);
        }
        _flightVerts = verts.ToArray();
        _flightVertCount = _flightVerts.Length / 3;
    }

    private void InvalidateFlightPaths()
    {
        _flightBuiltContinent = -2;
        _flightVertCount = 0;
        _flightData = null;
        _flightUndo.Clear(); _flightRedo.Clear();
        ClearFlightSelection();
        if (FlightMode) { FlightMode = false; FlightPathsChanged?.Invoke(false); ModeChanged?.Invoke(ModeLabel()); }
    }

    private void ClearFlightSelection()
    {
        _selectedPath = null;
        _selectedNode = -1;
        _draggingNode = false;
        _flightDrawing = false;
        _flightDrawMsg = "";
        _selPathVertCount = 0;
        _wpVertCount = 0;
        _stopVertCount = 0;
        _selNodeVertCount = 0;
    }

    private bool TryPickFlightPath(int mx, int my, out Salmiak.Core.Formats.FlightPaths.Path? hit)
    {
        hit = null;
        if (_flightData == null) return false;
        int continent = ContinentOf(_mapName);
        if (continent < 0) return false;
        var click = new Vector2(mx, my);
        float best = 10f;
        foreach (var path in _flightData.Paths)
        {
            if (path.Continent != continent) continue;
            for (int i = 1; i < path.Points.Count; i++)
            {
                if (!WorldToScreen(WorldToGl(path.Points[i - 1]), out var sa)) continue;
                if (!WorldToScreen(WorldToGl(path.Points[i]), out var sb)) continue;
                float d = PointToSegment(click, sa, sb);
                if (d < best) { best = d; hit = path; }
            }
        }
        return hit != null;
    }

    private bool TryPickWaypoint(int mx, int my, out int node)
    {
        node = -1;
        if (_selectedPath == null) return false;
        var click = new Vector2(mx, my);
        float best = 12f;
        for (int i = 0; i < _selectedPath.Points.Count; i++)
        {
            if (!WorldToScreen(WorldToGl(_selectedPath.Points[i]), out var sp)) continue;
            float d = (sp - click).Length;
            if (d < best) { best = d; node = i; }
        }
        return node >= 0;
    }

    private void SelectFlightPath(Salmiak.Core.Formats.FlightPaths.Path path)
    {
        _selectedPath = path;
        _selectedNode = -1;
        RefreshFlightSelection();
        string kind = path.TransportKind.Length > 0 ? path.TransportKind
            : path.FromNode == 0 && path.ToNode == 0 ? "transport" : "taxi path";
        Notify?.Invoke($"{char.ToUpperInvariant(kind[0])}{kind[1..]}: {path.Name}  ({path.Points.Count} waypoints)");
    }

    private void RefreshFlightSelection()
    {
        _selPathVertCount = 0; _wpVertCount = 0; _stopVertCount = 0; _selNodeVertCount = 0;
        if (_selectedPath == null) return;
        var pts = _selectedPath.Points;

        var line = new List<float>();
        for (int i = 1; i < pts.Count; i++)
        {
            var a = WorldToGl(pts[i - 1]); var b = WorldToGl(pts[i]);
            line.Add(a.X); line.Add(a.Y); line.Add(a.Z);
            line.Add(b.X); line.Add(b.Y); line.Add(b.Z);
            AppendArrow(line, a, b, FlightArrow);
        }
        _selPathVerts = line.ToArray();
        _selPathVertCount = _selPathVerts.Length / 3;

        var wp = new List<float>();
        var st = new List<float>();
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].Flags == 2) AppendCross(st, WorldToGl(pts[i]), 10f);
            else AppendCross(wp, WorldToGl(pts[i]), 6f);
        }
        _wpVerts = wp.ToArray();
        _wpVertCount = _wpVerts.Length / 3;
        _stopVerts = st.ToArray();
        _stopVertCount = _stopVerts.Length / 3;

        if (_selectedNode >= 0 && _selectedNode < pts.Count)
        {
            var sn = new List<float>();
            AppendCross(sn, WorldToGl(pts[_selectedNode]), 16f);
            _selNodeVerts = sn.ToArray();
            _selNodeVertCount = _selNodeVerts.Length / 3;
        }
    }

    private static void AppendCross(List<float> dst, Vector3 c, float r)
    {
        dst.Add(c.X - r); dst.Add(c.Y); dst.Add(c.Z); dst.Add(c.X + r); dst.Add(c.Y); dst.Add(c.Z);
        dst.Add(c.X); dst.Add(c.Y - r); dst.Add(c.Z); dst.Add(c.X); dst.Add(c.Y + r); dst.Add(c.Z);
        dst.Add(c.X); dst.Add(c.Y); dst.Add(c.Z - r); dst.Add(c.X); dst.Add(c.Y); dst.Add(c.Z + r);
    }

    private const float FlightArrow = 22f;

    private static void AppendArrow(List<float> dst, Vector3 a, Vector3 b, float size)
    {
        var dir = b - a;
        float len = dir.Length;
        if (len < 1e-3f) return;
        dir /= len;
        var right = Vector3.Cross(dir, Vector3.UnitY);
        float rl = right.Length;
        right = rl > 1e-3f ? right / rl : Vector3.UnitX;
        float s = MathF.Min(size, len * 0.4f);
        var mid = (a + b) * 0.5f;
        var tip = mid + dir * (s * 0.5f);
        var bk = mid - dir * (s * 0.5f);
        var b1 = bk + right * (s * 0.5f);
        var b2 = bk - right * (s * 0.5f);
        dst.Add(b1.X); dst.Add(b1.Y); dst.Add(b1.Z); dst.Add(tip.X); dst.Add(tip.Y); dst.Add(tip.Z);
        dst.Add(b2.X); dst.Add(b2.Y); dst.Add(b2.Z); dst.Add(tip.X); dst.Add(tip.Y); dst.Add(tip.Z);
    }

    private void FlightModeClick(int mx, int my)
    {
        if (_selectedPath != null && TryPickWaypoint(mx, my, out int wi))
        {
            _selectedNode = wi; _draggingNode = true; _dragUndoTaken = false;
            RefreshFlightSelection();
            return;
        }
        if (!_flightDrawing && TryPickFlightPath(mx, my, out var p) && p != null) { SelectFlightPath(p); return; }
        if (_selectedPath != null && PickTerrain(mx, my, out var hit)) { AddWaypoint(hit); return; }
        if (!_flightDrawing) ClearFlightSelection();
    }

    private void AddWaypoint(Vector3 hit)
    {
        if (_selectedPath == null) return;
        PushFlightUndo();
        var pts = _selectedPath.Points;
        float worldZ;
        if (DrawingBoat) worldZ = BoatDeckZ;
        else if (pts.Count == 0) worldZ = hit.Y + 50f;
        else if (_selectedNode >= 0 && _selectedNode < pts.Count) worldZ = pts[_selectedNode].Z;
        else worldZ = pts[^1].Z;
        var w = GlToWorld(hit);
        var wp = new Salmiak.Core.Formats.FlightPaths.Waypoint(w.X, w.Y, worldZ, ContinentOf(_mapName));
        int insertAt = (_selectedNode >= 0 && _selectedNode < pts.Count) ? _selectedNode + 1 : pts.Count;
        pts.Insert(insertAt, wp);
        _selectedNode = insertAt;
        _selectedPath.Dirty = true;
        RebuildFlightGeometry(); RefreshFlightSelection();
        Notify?.Invoke($"Waypoint added ({pts.Count} total).");
    }

    private void MoveSelectedNode(int mx, int my)
    {
        if (_selectedPath == null || _selectedNode < 0 || _selectedNode >= _selectedPath.Points.Count) return;
        if (!PickTerrain(mx, my, out var hit)) return;
        if (!_dragUndoTaken) { PushFlightUndo(); _dragUndoTaken = true; }
        var w = GlToWorld(hit);
        var wp = _selectedPath.Points[_selectedNode];
        wp.X = w.X; wp.Y = w.Y;
        _selectedPath.Dirty = true;
        RebuildFlightGeometry(); RefreshFlightSelection();
    }

    private void AdjustNodeHeight(float dz)
    {
        if (_selectedPath == null || _selectedNode < 0 || _selectedNode >= _selectedPath.Points.Count) return;
        if (DrawingBoat) { Notify?.Invoke("Boat paths ride at sea level - altitude is fixed."); return; }
        _selectedPath.Points[_selectedNode].Z += dz;
        _selectedPath.Dirty = true;
        RebuildFlightGeometry(); RefreshFlightSelection();
    }

    private void DeleteFlightSelection()
    {
        if (_selectedPath == null) return;
        PushFlightUndo();
        if (_selectedNode >= 0 && _selectedNode < _selectedPath.Points.Count)
        {
            _selectedPath.Points.RemoveAt(_selectedNode);
            _selectedPath.Dirty = true;
            if (_selectedPath.Points.Count == 0)
            { _flightData?.Paths.Remove(_selectedPath); ClearFlightSelection(); Notify?.Invoke("Path emptied and removed."); }
            else
            { _selectedNode = Math.Min(_selectedNode, _selectedPath.Points.Count - 1); RefreshFlightSelection(); Notify?.Invoke("Waypoint deleted."); }
        }
        else
        {
            _flightData?.Paths.Remove(_selectedPath);
            ClearFlightSelection();
            Notify?.Invoke("Path deleted.");
        }
        RebuildFlightGeometry();
    }

    public void StartFlightPathDraw(string name, uint cost, uint mountHorde, uint mountAlliance,
                                    bool twoWay = true, string returnName = "", string transportKind = "")
    {
        if (_flightData == null) return;
        int continent = ContinentOf(_mapName);
        if (continent < 0) return;
        PushFlightUndo();
        uint id = 1;
        foreach (var p in _flightData.Paths) if (p.Id >= id) id = p.Id + 1;
        bool transport = transportKind.Length > 0;
        var path = new Salmiak.Core.Formats.FlightPaths.Path
        {
            Id = id, Continent = continent, IsNew = true, Dirty = true,
            Name = string.IsNullOrWhiteSpace(name) ? $"New path {id}" : name.Trim(),
            CustomName = name.Trim(), Cost = transport ? 0 : cost,
            MountHorde = transport ? 0 : mountHorde, MountAlliance = transport ? 0 : mountAlliance,
            TwoWay = !transport && twoWay, ReverseName = returnName.Trim(),
            TransportKind = transportKind,
        };
        _flightData.Paths.Add(path);
        _selectedPath = path; _selectedNode = -1;
        _flightDrawing = true;
        _flightDrawMsg = "no waypoints yet";
        RefreshFlightSelection();
        Notify?.Invoke($"Drawing '{path.Name}' - fly the route and middle-click to drop waypoints at the camera; Enter finishes.");
    }

    private const float BoatDeckZ = 0f;

    private const float DockGoodMin = 10f, DockGoodMax = 20f;
    private const float DockScanRange = 500f;

    private readonly Dictionary<string, (Vector3[] Verts, Vector3 Min, Vector3 Max)?> _wmoVertCache =
        new(StringComparer.OrdinalIgnoreCase);
    private float _dockDist = float.NaN;
    private Vector3 _dockDistAt = new(float.MaxValue, 0, 0);

    private (Vector3[] Verts, Vector3 Min, Vector3 Max)? GetWmoVertCloud(string path)
    {
        if (_wmoVertCache.TryGetValue(path, out var hit)) return hit;
        if (_mpq == null) return _wmoVertCache[path] = null;
        try
        {
            Salmiak.Core.Formats.WmoFile.Root root;
            using (var s = _mpq.OpenFile(path)) root = Salmiak.Core.Formats.WmoFile.ParseRoot(s);
            var verts = new List<Vector3>();
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            string baseName = path[..^4];
            for (int g = 0; g < root.GroupCount; g++)
            {
                Salmiak.Core.Formats.WmoFile.Group? grp;
                try
                {
                    using var gs = _mpq.OpenFile($"{baseName}_{g:D3}.wmo");
                    grp = Salmiak.Core.Formats.WmoFile.ParseGroup(gs, root);
                }
                catch { continue; }
                if (grp == null) continue;
                for (int v = 0; v + 2 < grp.Vertices.Length; v += 12)
                {
                    var p = new Vector3(grp.Vertices[v], grp.Vertices[v + 1], grp.Vertices[v + 2]);
                    verts.Add(p);
                    mn = Vector3.ComponentMin(mn, p);
                    mx = Vector3.ComponentMax(mx, p);
                }
            }
            if (verts.Count == 0) return _wmoVertCache[path] = null;
            const int cap = 20000;
            Vector3[] arr;
            if (verts.Count <= cap) arr = verts.ToArray();
            else
            {
                int step = (verts.Count + cap - 1) / cap;
                arr = new Vector3[(verts.Count + step - 1) / step];
                for (int i = 0, j = 0; i < verts.Count; i += step, j++) arr[j] = verts[i];
            }
            return _wmoVertCache[path] = (arr, mn, mx);
        }
        catch { return _wmoVertCache[path] = null; }
    }

    private void UpdateDockDistance()
    {
        var cam = _camera.Position;
        if ((cam - _dockDistAt).LengthSquared < 4f) return;
        _dockDistAt = cam;
        var pt = cam;
        if (DrawingBoat) pt.Y = BoatDeckZ;

        float best2 = float.MaxValue;
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            foreach (var w in adt.Wmos)
            {
                if (string.IsNullOrEmpty(w.ModelPath)) continue;
                var xf = Salmiak.Rendering.WmoRenderer.BuildTransform(w);
                var originGl = new Vector3(xf.M41, xf.M42, xf.M43);
                if ((originGl - pt).LengthSquared > DockScanRange * DockScanRange) continue;
                var model = GetWmoVertCloud(w.ModelPath);
                if (model == null) continue;

                var inv = xf.Inverted();
                var lp4 = Vector4.TransformRow(new Vector4(pt, 1f), inv);
                var lp = lp4.Xyz / lp4.W;
                var cl = Vector3.Clamp(lp, model.Value.Min, model.Value.Max);
                if ((cl - lp).LengthSquared >= best2) continue;
                foreach (var v in model.Value.Verts)
                {
                    float d2 = (v - lp).LengthSquared;
                    if (d2 < best2) best2 = d2;
                }
            }
        }
        _dockDist = best2 == float.MaxValue ? float.NaN : MathF.Sqrt(best2);
    }
    private const uint StopDelay = 60;

    private bool DrawingBoat => _selectedPath?.TransportKind == "boat";

    private void AddWaypointAtCamera(bool stop = false)
    {
        if (_selectedPath == null) { _flightDrawing = false; return; }
        PushFlightUndo();
        var pts = _selectedPath.Points;
        var w = GlToWorld(_camera.Position);
        float z = DrawingBoat ? BoatDeckZ : w.Z;
        var wp = new Salmiak.Core.Formats.FlightPaths.Waypoint(w.X, w.Y, z, ContinentOf(_mapName),
            flags: stop ? 2u : 0u, delay: stop ? StopDelay : 0u);
        int insertAt = (_selectedNode >= 0 && _selectedNode < pts.Count) ? _selectedNode + 1 : pts.Count;
        pts.Insert(insertAt, wp);
        _selectedNode = insertAt;
        _selectedPath.Dirty = true;
        RebuildFlightGeometry(); RefreshFlightSelection();
        string what = stop ? "STOP" : "waypoint";
        _flightDrawMsg = $"{what} {insertAt + 1} added";
        Notify?.Invoke($"{what} {insertAt + 1} added at camera ({pts.Count} total)" +
                       (stop ? $" - boat pauses {StopDelay}s here." : " - Enter to finish."));
    }

    private void FinishFlightDraw()
    {
        _flightDrawing = false;
        _flightDrawMsg = "";
        if (_selectedPath == null) return;
        if (_selectedPath.Points.Count == 0)
        {
            _flightData?.Paths.Remove(_selectedPath);
            ClearFlightSelection();
            RebuildFlightGeometry();
            Notify?.Invoke("Empty path discarded.");
            return;
        }

        var p = _selectedPath;

        if (p.IsNew && p.MountHorde == 0 && p.MountAlliance == 0)
        {
            string closed = "";
            if (p.Points.Count >= 3)
            {
                PushFlightUndo();
                var first = p.Points[0]; var last = p.Points[^1];
                p.Points.Insert(0, new Salmiak.Core.Formats.FlightPaths.Waypoint(last.X, last.Y, last.Z, last.MapId));
                p.Points.Add(new Salmiak.Core.Formats.FlightPaths.Waypoint(first.X, first.Y, first.Z, first.MapId));
                RebuildFlightGeometry(); RefreshFlightSelection();
                closed = " Loop auto-closed (lead-in/out points added - the server skips the first and last).";
            }
            string warn = p.Points.Count < 3
                ? "  a transport path needs at least 3 waypoints (the server drops the first and " +
                  "last as spline lead-in and crashes on an empty result)." : "";
            Notify?.Invoke($"Transport path '{p.Name}' finished - {p.Points.Count} waypoint(s).{closed}{warn} " +
                           "Shift+MMB while drawing (or middle-click a waypoint) = passenger stop. " +
                           "Deploy, then apply deploy-sql\\transports.sql and restart mangosd.");
            return;
        }

        if (p.Points.Count >= 2 && _flightData != null && p.IsNew)
        {
            PushFlightUndo();
            string baseName = string.IsNullOrWhiteSpace(p.CustomName) ? p.Name : p.CustomName;
            var a = p.Points[0]; var b = p.Points[^1];
            p.FromNode = _flightData.EnsureNodeAt(p.Continent, a.X, a.Y, a.Z, baseName,
                p.MountHorde, p.MountAlliance, out bool newFrom);
            p.ToNode = _flightData.EnsureNodeAt(p.Continent, b.X, b.Y, b.Z, baseName,
                p.MountHorde, p.MountAlliance, out bool newTo);
            RebuildFlightGeometry();

            string ret = "";
            if (p.TwoWay && p.FromNode != p.ToNode)
            {
                uint rid = 1;
                foreach (var q in _flightData.Paths) if (q.Id >= rid) rid = q.Id + 1;
                string rname = string.IsNullOrWhiteSpace(p.ReverseName) ? $"{baseName} Return" : p.ReverseName;
                var rp = new Salmiak.Core.Formats.FlightPaths.Path
                {
                    Id = rid, Continent = p.Continent, IsNew = true, Dirty = true,
                    Name = rname, CustomName = rname, Cost = p.Cost,
                    MountHorde = p.MountHorde, MountAlliance = p.MountAlliance,
                    FromNode = p.ToNode, ToNode = p.FromNode,
                    TwoWay = false,
                };
                for (int i = p.Points.Count - 1; i >= 0; i--)
                {
                    var w = p.Points[i];
                    rp.Points.Add(new Salmiak.Core.Formats.FlightPaths.Waypoint(w.X, w.Y, w.Z, w.MapId));
                }
                _flightData.Paths.Add(rp);
                RebuildFlightGeometry();
                ret = $" + return route '{rname}' (path {rid})";
            }

            string warn = Math.Max(p.FromNode, p.ToNode) > 256
                ? "  node id > 256 exceeds the 1.12 taxi mask, so it won't work in-game." : "";
            if (!_flightData.RouteConnectsToNetwork(p))
                warn += "  Island route, not connected to the existing taxi network; the 1.12 client " +
                        "crashes on unreachable taxi-map nodes. Start or end at an existing flight point.";
            Notify?.Invoke($"Route '{p.Name}' finished{ret} - {p.Points.Count} waypoint(s), " +
                           $"node {p.FromNode} ({(newFrom ? "new" : "existing")}) to node {p.ToNode} ({(newTo ? "new" : "existing")}), " +
                           $"cost {p.Cost}c.{warn} Use Project, Deploy to publish it.");
            return;
        }
        Notify?.Invoke($"Path '{p.Name}' finished - {p.Points.Count} waypoint(s). " +
                       "It stays selected for fine-tuning; use Project, Deploy to publish it.");
    }

    private void PushFlightUndo()
    {
        if (_flightData == null) return;
        _flightUndo.Add(TakeFlightSnapshot());
        if (_flightUndo.Count > MaxUndo) _flightUndo.RemoveAt(0);
        _flightRedo.Clear();
    }

    private FlightSnapshot TakeFlightSnapshot()
    {
        var paths = new List<Salmiak.Core.Formats.FlightPaths.Path>(_flightData!.Paths.Count);
        foreach (var p in _flightData.Paths) paths.Add(ClonePath(p));
        var nodes = new List<Salmiak.Core.Formats.FlightPaths.Node>(_flightData.Nodes.Count);
        foreach (var n in _flightData.Nodes) nodes.Add(CloneNode(n));
        return new FlightSnapshot(paths, nodes);
    }

    private static Salmiak.Core.Formats.FlightPaths.Node CloneNode(Salmiak.Core.Formats.FlightPaths.Node n) => new()
    {
        Id = n.Id, MapId = n.MapId, X = n.X, Y = n.Y, Z = n.Z, Name = n.Name,
        MountHorde = n.MountHorde, MountAlliance = n.MountAlliance, IsNew = n.IsNew,
    };

    private static Salmiak.Core.Formats.FlightPaths.Path ClonePath(Salmiak.Core.Formats.FlightPaths.Path p)
    {
        var c = new Salmiak.Core.Formats.FlightPaths.Path
        {
            Id = p.Id, Continent = p.Continent, Name = p.Name, Dirty = p.Dirty, IsNew = p.IsNew,
            CustomName = p.CustomName, Cost = p.Cost, MountHorde = p.MountHorde, MountAlliance = p.MountAlliance,
            FromNode = p.FromNode, ToNode = p.ToNode, TwoWay = p.TwoWay, ReverseName = p.ReverseName,
            TransportKind = p.TransportKind,
        };
        foreach (var w in p.Points)
            c.Points.Add(new Salmiak.Core.Formats.FlightPaths.Waypoint(w.X, w.Y, w.Z, w.MapId, w.Flags, w.Delay));
        return c;
    }

    private void FlightUndo() => ApplyFlightSnapshot(_flightUndo, _flightRedo, "Undo");
    private void FlightRedo() => ApplyFlightSnapshot(_flightRedo, _flightUndo, "Redo");

    private void ApplyFlightSnapshot(List<FlightSnapshot> source, List<FlightSnapshot> dest, string label)
    {
        if (_flightData == null || source.Count == 0)
        { Notify?.Invoke($"Flight paths: nothing to {label.ToLowerInvariant()}."); return; }

        var snap = source[^1];
        source.RemoveAt(source.Count - 1);

        dest.Add(TakeFlightSnapshot());
        if (dest.Count > MaxUndo) dest.RemoveAt(0);

        bool wasDrawing = _flightDrawing;
        uint drawId = _selectedPath?.Id ?? 0;

        _flightData.Paths.Clear();
        _flightData.Paths.AddRange(snap.Paths);
        _flightData.Nodes.Clear();
        _flightData.Nodes.AddRange(snap.Nodes);
        ClearFlightSelection();

        if (wasDrawing)
        {
            var cont = _flightData.Paths.Find(q => q.Id == drawId && q.IsNew);
            if (cont != null)
            {
                _selectedPath = cont;
                _selectedNode = -1;
                _flightDrawing = true;
                _flightDrawMsg = $"{cont.Points.Count} waypoint(s) after {label.ToLowerInvariant()}";
            }
        }
        RebuildFlightGeometry();
        RefreshFlightSelection();
        Notify?.Invoke(_flightDrawing
            ? $"{label} - still drawing '{_selectedPath!.Name}' ({_selectedPath.Points.Count} waypoint(s); MMB adds, Enter finishes)."
            : $"{label} (taxi-path edit).");
    }
}
