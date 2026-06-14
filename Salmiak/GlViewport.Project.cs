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
    public event Action? SaveRequested;

    public event Action? LoadRequested;

    public bool CreateBlankMap(string name, int wTiles, int hTiles, float height, string baseTexture)
    {
        if (!IsHandleCreated) return false;
        if (_mpq == null) { Notify?.Invoke("New map: set your WoW directory first (textures load from it)."); return false; }
        if (string.IsNullOrWhiteSpace(name)) { Notify?.Invoke("New map: a name is required."); return false; }

        var (wdt, tiles) = BlankMap.Build(wTiles, hTiles, height, baseTexture);

        _editedTiles.Clear();
        _editedMap = name;
        foreach (var kv in tiles) _editedTiles[kv.Key] = kv.Value;

        StreamMap(wdt, name);
        Notify?.Invoke($"New map '{name}': {tiles.Count} flat tile(s) - sculpt/paint/place, then Save Edits to keep it.");
        return true;
    }

    public System.Collections.Generic.List<string> AvailableGroundTextures() => _mpq?.ListTextures() ?? new();

    public int EditedTileCount => _editedTiles.Count;

    public bool HasExportableEdits => !_editedTiles.IsEmpty || (_mpq?.Overrides.Count ?? 0) > 0;

    private void MarkEdited(AdtFile adt)
    {
        foreach (var ch in adt.Chunks)
        {
            if (ch == null) continue;
            const float ts = AdtFile.TileSize, cs = AdtFile.ChunkSize;
            int ty = (int)MathF.Round(32f - (ch.Position.X + ch.IndexY * cs) / ts);
            int tx = (int)MathF.Round(32f - (ch.Position.Y + ch.IndexX * cs) / ts);
            _editedTiles[(tx, ty)] = adt;
            _editedMap = _mapName;
            return;
        }
    }

    public void SaveEdits(string path)
    {
        using var fs = System.IO.File.Create(path);
        var pose = new MapEditFile.CameraPose(
            new System.Numerics.Vector3(_camera.Position.X, _camera.Position.Y, _camera.Position.Z),
            _camera.Yaw, _camera.Pitch);
        MapEditFile.Save(fs, _mapName ?? "", _editedTiles, pose, _mpq?.Overrides,
            _areaTable?.NewZones, _flightData);
    }

    public string LoadEdits(string path)
    {
        using var fs = System.IO.File.OpenRead(path);
        var tiles = MapEditFile.Load(fs, out var mapName, out var camera, out var overrides,
                                     out var zones, out var flightEdits);

        if (zones.Count > 0)
        {
            EnsureAreaTable();
            if (_areaTable != null) foreach (var z in zones) _areaTable.RestoreZone(z);
        }

        if (flightEdits.Nodes.Count > 0 || flightEdits.Paths.Count > 0)
        {
            _pendingFlightEdits = flightEdits;
            ApplyPendingFlightEdits();
        }
        if (overrides.Count > 0 && IsHandleCreated) MakeCurrent();
        if (_mpq != null)
            foreach (var (ovPath, bytes) in overrides)
            {
                _mpq.SetFileOverride(ovPath, bytes);
                _wmos?.Invalidate(ovPath);
            }
        if (camera is { } pose)
        {
            _camera.Position = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            _camera.Yaw = pose.Yaw;
            _camera.Pitch = pose.Pitch;
            CameraUpdated?.Invoke(_camera.Position);
        }
        foreach (var kv in tiles)
        {
            _editedTiles[kv.Key] = kv.Value;
            _tileCache[kv.Key] = kv.Value;
            var (tx, ty) = kv.Key;
            if (_wdt != null && !_wdt.IsWmoOnly &&
                tx is >= 0 and < WdtFile.GridSize && ty is >= 0 and < WdtFile.GridSize &&
                !_wdt.TileExists[ty, tx])
            {
                _wdt.TileExists[ty, tx] = true;
                _addedTiles.Add(kv.Key);
            }
        }
        _editedMap = mapName;
        RefreshStreaming();
        return mapName;
    }

    private readonly HashSet<(int X, int Y)> _addedTiles = new();

    public void AddTileAtCamera()
    {
        if (_wdt == null || _mapName == null) { Notify?.Invoke("Add tile: load a map first."); return; }
        if (_wdt.IsWmoOnly) { Notify?.Invoke("Add tile: WMO-only maps have no terrain grid."); return; }
        var (tx, ty) = CameraTile();
        if (tx is < 0 or >= WdtFile.GridSize || ty is < 0 or >= WdtFile.GridSize)
        { Notify?.Invoke("Add tile: the camera is outside the 64×64 map grid."); return; }
        if (_wdt.TileExists[ty, tx]) { Notify?.Invoke($"Add tile: tile ({tx},{ty}) already exists."); return; }

        const float ts = AdtFile.TileSize;
        float cx = (tx - 31.5f) * ts, cz = (ty - 31.5f) * ts;
        float height = 0f, sum = 0f;
        int n = 0;
        string tex = @"Tileset\Barrens\BarrensBaseGrass.blp";
        bool texFound = false;
        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            float px = cx + dx * (ts * 0.5f + 2f), pz = cz + dz * (ts * 0.5f + 2f);
            float hY = HeightAtGL(px, pz);
            if (float.IsNaN(hY)) continue;
            sum += hY; n++;
            if (!texFound && TryGetChunkAt(new Vector3(px, hY, pz), out var nAdt, out _, out _, out _, out _, out _)
                && nAdt.Textures.Count > 0)
            { tex = nAdt.Textures[0]; texFound = true; }
        }
        if (n > 0) height = sum / n;

        var blank = BlankMap.BuildTile(tx, ty, height, tex);
        _wdt.TileExists[ty, tx] = true;
        _editedTiles[(tx, ty)] = blank;
        _tileCache[(tx, ty)] = blank;
        _addedTiles.Add((tx, ty));
        RefreshStreaming();
        Notify?.Invoke($"New tile ({tx},{ty}) created at height {height:F0} - sculpt away. " +
                       "Export bundles the new ADT and the updated WDT.");
    }

    private static bool AnyTileNear(WdtFile wdt, (int X, int Y) c, int radius)
    {
        for (int y = Math.Max(0, c.Y - radius); y <= Math.Min(WdtFile.GridSize - 1, c.Y + radius); y++)
        for (int x = Math.Max(0, c.X - radius); x <= Math.Min(WdtFile.GridSize - 1, c.X + radius); x++)
            if (wdt.TileExists[y, x]) return true;
        return false;
    }

    private static (int X, int Y) CentroidTile(WdtFile wdt)
    {
        long sx = 0, sy = 0; int n = 0;
        for (int y = 0; y < WdtFile.GridSize; y++)
        for (int x = 0; x < WdtFile.GridSize; x++)
            if (wdt.TileExists[y, x]) { sx += x; sy += y; n++; }
        if (n == 0) return (32, 32);

        int cx = (int)(sx / n), cy = (int)(sy / n);
        if (wdt.TileExists[cy, cx]) return (cx, cy);

        int best = int.MaxValue, bx = cx, by = cy;
        for (int y = 0; y < WdtFile.GridSize; y++)
        for (int x = 0; x < WdtFile.GridSize; x++)
            if (wdt.TileExists[y, x])
            {
                int d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d < best) { best = d; bx = x; by = y; }
            }
        return (bx, by);
    }
}
