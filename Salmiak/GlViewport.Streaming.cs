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

    private MpqManager? _mpq;
    private string? _mapName;
    private WdtFile? _wdt;

    private AdtFile? _wmoHolder;
    private bool WmoOnly => _wdt == null && _wmoHolder != null;

    private System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), AdtFile?> _tileCache = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), AdtFile> _editedTiles = new();
    private string? _editedMap;

    private Thread? _worker;
    private volatile bool _workerRun;
    private volatile int _cameraTilePacked = Pack(int.MinValue >> 1, int.MinValue >> 1);
    private volatile bool _tilesDirty;
    private (int X, int Y) _builtTile = (int.MinValue, int.MinValue);
    private int _generation;
    private bool _buildPending;
    private bool _objectsDirty;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int TileBuildBudget { get; set; } = 2;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int LoadRadius { get; set; } = 3;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int PrefetchMargin { get; set; } = 2;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RecenterMargin { get; set; } = 2;

    public event Action<int>? TileCountChanged;

    public event Action<bool>? LoadingChanged;
    private volatile bool _loading;

    private void SetLoading(bool v)
    {
        if (_loading == v) return;
        _loading = v;
        LoadingChanged?.Invoke(v);
    }

    private static int Pack(int x, int y) => ((x & 0xFFFF) << 16) | (y & 0xFFFF);
    private static (int X, int Y) Unpack(int p) => ((short)(p >> 16), (short)(p & 0xFFFF));

    private (WdtFile Wdt, string Name)? _pendingStream;

    public void StreamMap(WdtFile wdt, string mapName)
    {
        if (!IsHandleCreated) { _pendingStream = (wdt, mapName); return; }
        MakeCurrent();

        StopWorker();
        _wdt = wdt;
        _wmoHolder = null;
        _mapName = mapName;
        ClearSelection();
        if (_minimapOn) EnsureMinimap();
        InvalidateFlightPaths();
        NpcMode = false; HideNpcs();
        HerbMode = false; HideHerbs();
        if (!string.Equals(_editedMap, mapName, StringComparison.OrdinalIgnoreCase))
        {
            _editedTiles.Clear();
            _addedTiles.Clear();
            _editedMap = mapName;
        }
        _tileCache = new();
        _terrain?.Dispose();
        _terrain = new TerrainRenderer();

        _camera.Position = new Vector3(662f, 445f, 3972f);
        _camera.Pitch = -MathF.PI / 2f + 0.01f;
        _camera.Yaw = -MathF.PI / 2f;

        if (!AnyTileNear(wdt, CameraTile(), LoadRadius))
        {
            var (cx, cy) = CentroidTile(wdt);
            const float ts = AdtFile.TileSize;
            _camera.Position = new Vector3((cx - 31.5f) * ts, 445f, (cy - 31.5f) * ts);
        }

        _builtTile = (int.MinValue, int.MinValue);
        var t = CameraTile();
        _cameraTilePacked = Pack(t.X, t.Y);
        _tilesDirty = false;
        SetLoading(true);
        StartWorker();
    }

    private void UpdateStreaming()
    {
        if (_wdt == null || _terrain == null) return;

        var t = CameraTile();
        _cameraTilePacked = Pack(t.X, t.Y);

        int trigger = Math.Max(1, LoadRadius - RecenterMargin);
        bool first  = _builtTile.X == int.MinValue;
        bool nearEdge = !first &&
            Math.Max(Math.Abs(t.X - _builtTile.X), Math.Abs(t.Y - _builtTile.Y)) > trigger;

        bool newWork = first || nearEdge || _tilesDirty;

        (int X, int Y) center;
        if (first || nearEdge)            center = t;
        else if (_tilesDirty || _buildPending) center = _builtTile;
        else return;

        _tilesDirty = false;
        _builtTile = center;
        if (newWork) _objectsDirty = true;

        int r = LoadRadius;
        var visible = new List<(AdtFile Adt, int D)>();
        for (int ty = center.Y - r; ty <= center.Y + r; ty++)
        for (int tx = center.X - r; tx <= center.X + r; tx++)
        {
            if (tx < 0 || tx >= WdtFile.GridSize || ty < 0 || ty >= WdtFile.GridSize) continue;
            if (!_wdt.TileExists[ty, tx]) continue;
            if (_tileCache.TryGetValue((tx, ty), out var adt) && adt != null)
                visible.Add((adt, Math.Max(Math.Abs(tx - t.X), Math.Abs(ty - t.Y))));
        }
        visible.Sort((a, b) => a.D.CompareTo(b.D));
        var order = visible.ConvertAll(v => v.Adt);

        int pending = _terrain.SetVisible(order, _texCache, Math.Max(1, TileBuildBudget));
        _buildPending = pending > 0;

        if (!_buildPending && _objectsDirty)
        {
            if (_doodads != null && _texCache != null) _doodads.Load(order, _texCache);
            if (_wmos != null && _texCache != null) _wmos.Load(order, _texCache);
            _objectsDirty = false;
        }

        TileCountChanged?.Invoke(order.Count);
    }

    private void StartWorker()
    {
        int generation = ++_generation;
        _workerRun = true;
        _worker = new Thread(() => WorkerLoop(generation))
        {
            IsBackground = true,
            Name = "AdtPrefetch",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    private void StopWorker()
    {
        _workerRun = false;
        _generation++;
        _worker = null;
    }

    private void WorkerLoop(int generation)
    {
        var mpq = _mpq;
        var wdt = _wdt;
        var map = _mapName;
        var cache = _tileCache;
        if (mpq == null || wdt == null || map == null) return;

        while (_workerRun && generation == _generation)
        {
            var (cx, cy) = Unpack(_cameraTilePacked);
            int r = LoadRadius + PrefetchMargin;
            bool foundMissing = false;
            bool brokeEarly = false;

            for (int ring = 0; ring <= r && _workerRun && generation == _generation && !brokeEarly; ring++)
            for (int ty = cy - ring; ty <= cy + ring && !brokeEarly; ty++)
            for (int tx = cx - ring; tx <= cx + ring; tx++)
            {
                if (Math.Max(Math.Abs(tx - cx), Math.Abs(ty - cy)) != ring) continue;
                if (tx < 0 || tx >= WdtFile.GridSize || ty < 0 || ty >= WdtFile.GridSize) continue;
                if (!wdt.TileExists[ty, tx]) continue;
                if (cache.ContainsKey((tx, ty))) continue;

                if (_editedTiles.TryGetValue((tx, ty), out var editedTile))
                {
                    cache[(tx, ty)] = editedTile;
                    if (Math.Max(Math.Abs(tx - cx), Math.Abs(ty - cy)) <= LoadRadius) _tilesDirty = true;
                    continue;
                }

                foundMissing = true;
                SetLoading(true);

                AdtFile? adt = null;
                try
                {
                    var path = $@"World\Maps\{map}\{map}_{tx}_{ty}.adt";
                    if (mpq.FileExists(path))
                    {
                        using var s = mpq.OpenFile(path);
                        adt = AdtFile.Parse(s);
                    }
                }
                catch { adt = null; }

                cache[(tx, ty)] = adt;
                if (adt != null && Math.Max(Math.Abs(tx - cx), Math.Abs(ty - cy)) <= LoadRadius)
                    _tilesDirty = true;

                if (Pack(cx, cy) != _cameraTilePacked) { brokeEarly = true; break; }
            }

            int evict = Math.Max(2 * LoadRadius - RecenterMargin, LoadRadius + PrefetchMargin) + 1;
            foreach (var k in cache.Keys)
                if (Math.Abs(k.X - cx) > evict || Math.Abs(k.Y - cy) > evict)
                    cache.TryRemove(k, out _);

            if (!brokeEarly && !foundMissing) { SetLoading(false); Thread.Sleep(20); }
        }
    }

    public void RefreshStreaming() => _builtTile = (int.MinValue, int.MinValue);

    public string? MapName => _mapName;
}
