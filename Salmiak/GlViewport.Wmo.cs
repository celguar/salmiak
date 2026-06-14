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
    private WmoDef _gizBeforeW;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool WmoMode { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? PlaceWmo
    {
        get => _placeWmo;
        set { _placeWmo = value; _placeYaw = 0f; _placePitch = 0f; _placeRoll = 0f; _placeLift = 0f; }
    }

    private string? _placeWmo;

    public event Action? OpenWmoPalette;

    public event Action<string>? WmoArmed;

    public List<string> AvailableWmos()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _tileCache)
            if (kv.Value != null)
                foreach (var m in kv.Value.WmoModels)
                    if (!string.IsNullOrEmpty(m)) set.Add(m);
        return set.ToList();
    }

    public void LoadGlobalWmo(WdtFile wdt, string mapName)
    {
        if (!IsHandleCreated || wdt.GlobalWmoDef is not { } def) return;
        LoadWmoScene(def, mapName);
    }

    private void LoadWmoScene(WmoDef def, string mapName)
    {
        if (!IsHandleCreated) return;
        MakeCurrent();

        StopWorker();
        ClearSelection();
        _wdt = null;
        _mapName = mapName;
        InvalidateFlightPaths();
        _tileCache = new();
        _terrain?.Dispose();
        _terrain = new TerrainRenderer();
        SetLoading(false);

        var holder = new AdtFile();
        holder.Wmos.Add(def);
        try
        {
            using var ws = _mpq!.OpenFile(def.ModelPath);
            var root = WmoFile.ParseRoot(ws);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dd in root.Doodads)
                if (!string.IsNullOrEmpty(dd.Path) && seen.Add(dd.Path)) holder.DoodadModels.Add(dd.Path);
        }
        catch { }
        _wmoHolder = holder;
        ReloadWmoOnly();

        const float mapHalf = 32f * AdtFile.TileSize;
        var gl = new Vector3(def.Position.X - mapHalf, def.Position.Y, def.Position.Z - mapHalf);
        float radius = 60f;
        if (_wmos != null && _wmos.TryGetRadius(def.ModelPath, out var rr)) radius = MathF.Max(rr, 20f);
        _camera.Position = gl + new Vector3(radius * 1.1f, radius * 1.0f, radius * 1.1f);
        var dir = Vector3.Normalize(gl - _camera.Position);
        _camera.Pitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
        _camera.Yaw = MathF.Atan2(dir.Z, dir.X);
        _camera.MoveSpeed = MathF.Max(80f, radius);
        CameraSpeedChanged?.Invoke(_camera.MoveSpeed);
    }

    private bool _wmoEditMode;

    private string? _wmoEditPath;

    private byte[]? _wmoEditBaseBytes;

    private Dictionary<string, byte[]>? _wmoEditBaseGroups;

    private sealed class MapPark
    {
        public string? MapName;
        public WdtFile? Wdt;
        public TerrainRenderer? Terrain;
        public System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), AdtFile?> TileCache = new();
        public AdtFile? Holder;
        public Vector3 CamPos; public float Yaw, Pitch;
    }

    private MapPark? _preWmoEdit;

    public bool WmoEditMode => _wmoEditMode;

    public string? WmoEditPath => _wmoEditPath;

    public void EnterWmoEdit(string wmoPath)
    {
        if (_mpq == null || string.IsNullOrEmpty(wmoPath)) return;

        byte[] baseBytes;
        try
        {
            using var s = _mpq.OpenFile(wmoPath);
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            baseBytes = ms.ToArray();
        }
        catch { Notify?.Invoke($"WMO edit: can't open {wmoPath}"); return; }

        if (!_wmoEditMode)
        {
            _preWmoEdit = new MapPark
            {
                MapName = _mapName, Wdt = _wdt, Terrain = _terrain, TileCache = _tileCache,
                Holder = _wmoHolder, CamPos = _camera.Position, Yaw = _camera.Yaw, Pitch = _camera.Pitch,
            };
            _terrain = null;
        }

        _wmoEditBaseBytes = baseBytes;
        _wmoEditBaseGroups = null;
        _wmoEditMode = true;
        _wmoEditPath = wmoPath;

        int defaultSet = 0;
        try
        {
            using var rs = new System.IO.MemoryStream(_wmoEditBaseBytes);
            var root = WmoFile.ParseRoot(rs);
            int best = -1;
            for (int i = 1; i < root.DoodadSets.Count; i++)
                if (root.DoodadSets[i].Count > best) { best = root.DoodadSets[i].Count; defaultSet = i; }
        }
        catch { }

        const float mapHalf = 32f * AdtFile.TileSize;
        var def = new WmoDef
        {
            ModelPath = wmoPath,
            Position = new System.Numerics.Vector3(mapHalf, 0f, mapHalf),
            Rotation = System.Numerics.Vector3.Zero,
            UniqueId = 1, Flags = 0, DoodadSet = (ushort)defaultSet,
        };
        LoadWmoScene(def, "(WMO edit)");
        DoodadMode = true;
    }

    public List<(string Name, int Count)> WmoEditDoodadSets()
    {
        var result = new List<(string, int)>();
        if (!_wmoEditMode || _wmoEditBaseBytes == null) return result;
        try
        {
            using var rs = new System.IO.MemoryStream(_wmoEditBaseBytes);
            var root = WmoFile.ParseRoot(rs);
            for (int i = 0; i < root.DoodadSets.Count; i++)
            {
                string name = i < root.DoodadSetNames.Count && root.DoodadSetNames[i].Length > 0
                    ? root.DoodadSetNames[i] : (i == 0 ? "Global" : $"Set {i}");
                result.Add((name, root.DoodadSets[i].Count));
            }
        }
        catch { }
        return result;
    }

    public int WmoEditDoodadSet => _wmoHolder is { Wmos.Count: > 0 } ? _wmoHolder.Wmos[0].DoodadSet : 0;

    public void SetWmoEditDoodadSet(int setIndex)
    {
        if (!_wmoEditMode || _wmoHolder is not { Wmos.Count: > 0 }) return;
        var def = _wmoHolder.Wmos[0];
        def.DoodadSet = (ushort)Math.Max(0, setIndex);
        _wmoHolder.Wmos[0] = def;
        ReloadWmoOnly();
    }

    public int AddWmoEditDoodadSet(string name)
    {
        if (!_wmoEditMode || _wmoEditBaseBytes == null) return -1;
        var updated = WmoRootWriter.AddDoodadSet(_wmoEditBaseBytes, name);
        if (updated == null) return -1;
        _wmoEditBaseBytes = updated;

        int newIndex = 0;
        try { using var rs = new System.IO.MemoryStream(updated); newIndex = Math.Max(0, WmoFile.ParseRoot(rs).DoodadSets.Count - 1); }
        catch { }
        SetWmoEditDoodadSet(newIndex);
        return newIndex;
    }

    public int SaveWmoEdit()
    {
        if (!_wmoEditMode || _wmoEditPath == null || _wmoEditBaseBytes == null || _mpq == null) return 0;
        if (_wmoHolder is not { Wmos.Count: > 0 }) return 0;
        MakeCurrent();
        var def = _wmoHolder.Wmos[0];

        var wmoInv = Salmiak.Rendering.WmoRenderer.BuildTransform(def).Inverted();
        var placements = new List<WmoRootWriter.DoodadPlacement>(_wmoHolder.Doodads.Count);
        foreach (var d in _wmoHolder.Doodads)
        {
            if (string.IsNullOrEmpty(d.ModelPath)) continue;
            var local = Salmiak.Rendering.DoodadRenderer.BuildTransform(d) * wmoInv;
            var pos = local.ExtractTranslation();
            var rot = local.ExtractRotation();
            var scl = local.ExtractScale();
            placements.Add(new WmoRootWriter.DoodadPlacement(
                d.ModelPath, pos.X, pos.Y, pos.Z, rot.X, rot.Y, rot.Z, rot.W, (scl.X + scl.Y + scl.Z) / 3f));
        }

        int insertAt = 0;
        var rebuilt = placements.Count > 0
            ? WmoRootWriter.AddDoodads(_wmoEditBaseBytes, placements, def.DoodadSet, out insertAt)
            : _wmoEditBaseBytes;
        if (rebuilt == null) return 0;
        _mpq.SetFileOverride(_wmoEditPath, rebuilt);
        if (placements.Count > 0) PatchWmoGroupRefs(placements, insertAt);
        _wmos?.Invalidate(_wmoEditPath);
        return placements.Count;
    }

    private void PatchWmoGroupRefs(List<WmoRootWriter.DoodadPlacement> placements, int insertAt)
    {
        if (_wmoEditPath == null || _wmoEditBaseBytes == null || _mpq == null) return;

        int groupCount;
        try { using var rs = new System.IO.MemoryStream(_wmoEditBaseBytes); groupCount = WmoFile.ParseRoot(rs).GroupCount; }
        catch { return; }
        if (groupCount <= 0) return;

        if (_wmoEditBaseGroups == null)
        {
            _wmoEditBaseGroups = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string baseName = _wmoEditPath[..^4];
            for (int g = 0; g < groupCount; g++)
            {
                string gp = $"{baseName}_{g:000}.wmo";
                try
                {
                    using var s = _mpq.OpenFile(gp);
                    using var ms = new System.IO.MemoryStream();
                    s.CopyTo(ms);
                    _wmoEditBaseGroups[gp] = ms.ToArray();
                }
                catch { }
            }
        }

        var groups = new List<(string Path, byte[] Bytes, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>();
        foreach (var (gp, bytes) in _wmoEditBaseGroups)
        {
            var info = WmoGroupWriter.ReadInfo(bytes);
            if (info is { } gi) groups.Add((gp, bytes, gi.BoxMin, gi.BoxMax));
        }
        if (groups.Count == 0) return;

        var refsPerGroup = new Dictionary<string, List<ushort>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < placements.Count; i++)
        {
            var p = new System.Numerics.Vector3(placements[i].PosX, placements[i].PosY, placements[i].PosZ);
            string? bestPath = null; float bestVol = float.MaxValue;
            foreach (var (gp, _, mn, mx) in groups)
            {
                if (p.X < mn.X || p.Y < mn.Y || p.Z < mn.Z || p.X > mx.X || p.Y > mx.Y || p.Z > mx.Z) continue;
                float vol = MathF.Max(mx.X - mn.X, 0.01f) * MathF.Max(mx.Y - mn.Y, 0.01f) * MathF.Max(mx.Z - mn.Z, 0.01f);
                if (vol < bestVol) { bestVol = vol; bestPath = gp; }
            }
            if (bestPath == null)
            {
                float bestD = float.MaxValue;
                foreach (var (gp, _, mn, mx) in groups)
                {
                    float d = System.Numerics.Vector3.DistanceSquared(p, (mn + mx) * 0.5f);
                    if (d < bestD) { bestD = d; bestPath = gp; }
                }
            }
            if (bestPath == null) continue;
            if (!refsPerGroup.TryGetValue(bestPath, out var list)) { list = new(); refsPerGroup[bestPath] = list; }
            list.Add((ushort)(insertAt + i));
        }

        foreach (var (gp, bytes, _, _) in groups)
        {
            refsPerGroup.TryGetValue(gp, out var adds);
            var patched = WmoGroupWriter.PatchDoodadRefs(bytes, insertAt, placements.Count, adds ?? (IReadOnlyList<ushort>)Array.Empty<ushort>());
            if (patched != null) _mpq.SetFileOverride(gp, patched);
        }
    }

    public void ExitWmoEdit(bool save)
    {
        if (!_wmoEditMode) return;
        if (save) SaveWmoEdit();
        _wmoEditMode = false;
        _wmoEditPath = null;
        _wmoEditBaseBytes = null;
        _wmoEditBaseGroups = null;

        var park = _preWmoEdit;
        _preWmoEdit = null;
        if (park == null) return;

        MakeCurrent();
        ClearSelection();
        _terrain?.Dispose();
        _terrain = park.Terrain;
        _tileCache = park.TileCache;
        _wdt = park.Wdt;
        _wmoHolder = park.Holder;
        _mapName = park.MapName;
        _camera.Position = park.CamPos; _camera.Yaw = park.Yaw; _camera.Pitch = park.Pitch;
        InvalidateFlightPaths();

        if (_wdt != null)
        {
            _tilesDirty = true;
            StartWorker();
        }
        else if (_wmoHolder != null) ReloadWmoOnly();
    }

    public string? CopyWmo(string sourcePath, string newLeafName)
    {
        if (_mpq == null || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(newLeafName)) return null;
        newLeafName = System.IO.Path.GetFileNameWithoutExtension(newLeafName.Trim());
        if (newLeafName.Length == 0) return null;

        string dir = System.IO.Path.GetDirectoryName(sourcePath) ?? "";
        string srcBase = sourcePath[..^4];
        string newPath = dir.Length > 0 ? $@"{dir}\{newLeafName}.wmo" : $"{newLeafName}.wmo";
        string newBase = newPath[..^4];

        byte[] ReadAll(string p) { using var s = _mpq.OpenFile(p); using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
        try
        {
            byte[] rootBytes = ReadAll(sourcePath);
            int groupCount;
            using (var rs = new System.IO.MemoryStream(rootBytes)) groupCount = WmoFile.ParseRoot(rs).GroupCount;
            _mpq.SetFileOverride(newPath, rootBytes);
            for (int g = 0; g < groupCount; g++)
            {
                string sg = $"{srcBase}_{g:000}.wmo";
                if (!_mpq.FileExists(sg)) continue;
                _mpq.SetFileOverride($"{newBase}_{g:000}.wmo", ReadAll(sg));
            }
            return newPath;
        }
        catch { Notify?.Invoke($"Copy WMO failed for {sourcePath}"); return null; }
    }

    private void ReloadWmoOnly()
    {
        if (_wmoHolder == null || _texCache == null) return;
        MakeCurrent();
        var list = new List<AdtFile> { _wmoHolder };
        _doodads?.Load(list, _texCache);
        _wmos?.Load(list, _texCache);
    }

    public byte[]? RenderWmoThumbnail(string path, int size, float yawDeg = 0f)
    {
        if (_wmos == null || !IsHandleCreated) return null;
        MakeCurrent();
        var px = _wmos.RenderThumbnail(path, size, yawDeg);
        GL.Viewport(0, 0, Width, Height);
        return px;
    }

    private WmoDef BuildWmoDef(Vector3 hit)
    {
        const float mapHalf = 32f * AdtFile.TileSize;
        var (pitch, roll) = AlignToNormal ? TiltToNormal(hit.X, hit.Z, _placeYaw) : (_placePitch, _placeRoll);
        return new WmoDef
        {
            ModelPath = _placeWmo!,
            Position  = new System.Numerics.Vector3(hit.X + mapHalf, hit.Y + _placeLift, hit.Z + mapHalf),
            Rotation  = new System.Numerics.Vector3(pitch, _placeYaw, roll),
            UniqueId  = unchecked((uint)Environment.TickCount + (uint)_placeCounter++),
            Flags     = 0,
            DoodadSet = (ushort)BusiestDoodadSet(_placeWmo!),
        };
    }

    private int BusiestDoodadSet(string modelPath)
    {
        var sets = WmoModelDoodadSets(modelPath);
        int best = 0, bestCount = -1;
        for (int i = 1; i < sets.Count; i++)
            if (sets[i].Count > bestCount) { bestCount = sets[i].Count; best = i; }
        return best;
    }

    private void PlaceWmoAt(int mx, int my)
    {
        if (_wdt == null || !PickPlacement(mx, my, out var hit)) return;
        const float ts = AdtFile.TileSize;
        int tileX = (int)MathF.Floor(32f + hit.X / ts);
        int tileY = (int)MathF.Floor(32f + hit.Z / ts);
        if (!_tileCache.TryGetValue((tileX, tileY), out var adt) || adt == null) return;

        var def = BuildWmoDef(hit);
        adt.Wmos.Add(def);
        MarkEdited(adt);
        Select(adt, adt.Wmos.Count - 1, true);

        var rec = new Stroke();
        rec.PlacedWmo.Add((adt, def));
        PushUndo(rec);

        RefreshStreaming();
    }

    private (AdtFile Adt, int Index)? WmoPickAt(int mx, int my)
    {
        if (_wmos == null) return null;
        MakeCurrent();
        float aspect = Width > 0 && Height > 0 ? (float)Width / Height : 1f;
        return _wmos.Pick(_camera, aspect, mx, my, Width, Height);
    }

    private void CopyWmo(AdtFile adt, int index)
    {
        if (index < 0 || index >= adt.Wmos.Count) return;
        var def = adt.Wmos[index];
        _placeWmo = def.ModelPath;
        _placePitch = def.Rotation.X; _placeYaw = def.Rotation.Y; _placeRoll = def.Rotation.Z; _placeLift = 0f;
        WmoArmed?.Invoke(def.ModelPath);
    }

    private void DeleteWmo(AdtFile adt, int index)
    {
        if (index < 0 || index >= adt.Wmos.Count) return;
        if (_sel is { IsWmo: true } s && s.Adt == adt) ClearSelection();
        var def = adt.Wmos[index];
        adt.Wmos.RemoveAt(index);
        MarkEdited(adt);
        var rec = new Stroke();
        rec.RemovedWmo.Add((adt, def));
        PushUndo(rec);
        RefreshStreaming();
        Notify?.Invoke($"Deleted WMO: {System.IO.Path.GetFileNameWithoutExtension(def.ModelPath)}");
    }

    private void ShowWmoContextMenu(int mx, int my)
    {
        var hit = WmoPickAt(mx, my);
        if (hit == null) return;
        var (adt, index) = hit.Value;
        var wdef = adt.Wmos[index];
        string name = System.IO.Path.GetFileNameWithoutExtension(wdef.ModelPath);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel(name) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());

        var sets = WmoModelDoodadSets(wdef.ModelPath);
        if (sets.Count > 1)
        {
            var setMenu = new ToolStripMenuItem("Furniture set");
            for (int i = 0; i < sets.Count; i++)
            {
                int si = i;
                var item = new ToolStripMenuItem($"{sets[i].Name} ({sets[i].Count})") { Checked = wdef.DoodadSet == si };
                item.Click += (_, _) => SetWmoDoodadSet(adt, index, si);
                setMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(setMenu);
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add("Copy", null, (_, _) => CopyWmo(adt, index));
        menu.Items.Add("Delete", null, (_, _) => DeleteWmo(adt, index));
        menu.Show(ScreenPoint(mx, my));
    }

    private List<(string Name, int Count)> WmoModelDoodadSets(string modelPath)
    {
        var result = new List<(string, int)>();
        if (_mpq == null || string.IsNullOrEmpty(modelPath)) return result;
        try
        {
            using var s = _mpq.OpenFile(modelPath);
            var root = WmoFile.ParseRoot(s);
            for (int i = 0; i < root.DoodadSets.Count; i++)
            {
                string nm = i < root.DoodadSetNames.Count && root.DoodadSetNames[i].Length > 0
                    ? root.DoodadSetNames[i] : (i == 0 ? "Global" : $"Set {i}");
                result.Add((nm, root.DoodadSets[i].Count));
            }
        }
        catch { }
        return result;
    }

    private void SetWmoDoodadSet(AdtFile adt, int index, int set)
    {
        if (index < 0 || index >= adt.Wmos.Count) return;
        var before = adt.Wmos[index];
        if (before.DoodadSet == set) return;
        var def = before;
        def.DoodadSet = (ushort)Math.Max(0, set);
        adt.Wmos[index] = def;
        MarkEdited(adt);
        var rec = new Stroke();
        rec.WmoXform.Add((adt, index, before));
        PushUndo(rec);
        RefreshStreaming();
    }
}
