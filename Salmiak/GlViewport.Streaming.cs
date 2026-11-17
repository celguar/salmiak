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
    private int _tileCount;

    //Streaming runs every frame and usually arrives at the same tile count, so the event is only
    //fired when the count actually changed: the status bar and the message log never need to see
    //the same number over and over.
    private void SetTileCount(int n)
    {
        if (_tileCount == n) return;
        _tileCount = n;
        TileCountChanged?.Invoke(n);
    }

    public event Action<bool>? LoadingChanged;
    private volatile bool _loading;

    private void SetLoading(bool v)
    {
        if (_loading == v) return;
        _loading = v;
        LoadingChanged?.Invoke(v);
    }

    //A cold load is the app opening or a map being switched: none of this view is drawn yet and
    //the archives still have to hand over their tiles, their WMOs and their models, which reads as
    //the window hanging for a few seconds. Until that work runs out, the client's loading screen
    //stands in for the scene and its bar walks whatever is still left waiting in the view, so the
    //bar answers for the loader rather than for a guess at how long a map takes. Travelling later
    //reaches new tiles one at a time and is not a cold load, so it never pulls the screen back up.
    private LoadingScreen? _loadScreen;
    private volatile bool _coldLoad;
    private int _coldSeen;
    private float _coldShown;
    private int _objectsPending;
    private long _coldStartMs;

    //A map made of one huge building has no tiles to wait for, so its cold load has nothing to count:
    //the screen is then held for this long instead, and its bar walks the time rather than the work.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int WmoSceneColdLoadMillis { get; set; } = 1000;

    private int _coldMinMillis;

    public event Action<int, int>? LoadProgress;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int WmoBuildBudget { get; set; } = 8;

    //How many of the tiles this view wants the archive thread has not handed over yet.
    private int MissingTiles((int X, int Y) center)
    {
        if (_wdt == null) return 0;
        int r = LoadRadius, n = 0;
        for (int ty = center.Y - r; ty <= center.Y + r; ty++)
        for (int tx = center.X - r; tx <= center.X + r; tx++)
        {
            if (tx < 0 || tx >= WdtFile.GridSize || ty < 0 || ty >= WdtFile.GridSize) continue;
            if (!_wdt.TileExists[ty, tx]) continue;
            if (!_tileCache.ContainsKey((tx, ty))) n++;
        }
        return n;
    }

    private void BeginColdLoad(string mapName, int minMillis = 0)
    {
        _coldLoad = true;
        _coldSeen = 0;
        _coldShown = 0f;
        _objectsPending = 0;
        _coldMinMillis = Math.Max(0, minMillis);
        _coldStartMs = _sw.ElapsedMilliseconds;
        _loadScreen = null;
        if (_text == null || _texCache == null || _mpq == null) return;
        _loadScreen = new LoadingScreen(_text, _texCache, _mpq);
        _loadScreen.Enable(mapName);

        //What the tables gave is said out loud, so a map that came out with another map's picture can be
        //read off the status bar against its own row of Map.dbc rather than guessed at. Where the chain
        //named a file these archives do not hold, that is said too, since the client's own fallback
        //picture otherwise looks like the map's screen.
        if (!_loadScreen.ArtFound)
            Notify?.Invoke(_loadScreen.ArtAsked.Length > 0
                ? $"Map.dbc names {_loadScreen.ArtAsked} for this map and the archives do not hold it; drawing a plain bar."
                : "Map.dbc names no loading screen for this map; drawing a plain bar.");
        else if (_loadScreen.ArtFallback && _loadScreen.ArtMissing)
            Notify?.Invoke($"Loading screen: the client's own picture - Map.dbc named {_loadScreen.ArtAsked} for this map and the archives do not hold it.");
        else if (_loadScreen.ArtFallback)
            Notify?.Invoke($"Loading screen: the client's own picture - Map.dbc names no screen for this map.");
        else Notify?.Invoke($"Loading screen: {System.IO.Path.GetFileName(_loadScreen.ArtPath)}");
    }

    //How long this cold load is being held up for, in ms.
    private long ColdLoadMillis() => _sw.ElapsedMilliseconds - _coldStartMs;

    //The bar only ever moves forward: the work still waiting is counted as items - tiles the
    //archive thread has not unpacked, chunk meshes still to build, WMO roots still to upload and
    //doodad models still to build - and the largest such count seen so far is what a full bar
    //means, so a phase that only shows up once the tiles behind it arrive cannot drag it back.
    //Where the count runs out before the hold does, the bar walks the time instead, which is what
    //a single huge building needs: it arrives in one go, and a bar that filled on the first frame
    //would leave nothing to look at.
    private void StepColdLoad(int tilesMissing, int meshesPending, int objectsPending, int modelsQueued)
    {
        if (!_coldLoad) return;
        int left = tilesMissing + meshesPending + objectsPending + modelsQueued;
        if (left > _coldSeen) _coldSeen = left;

        long elapsed = ColdLoadMillis();
        if (left <= 0 && elapsed >= _coldMinMillis) { EndColdLoad(); return; }

        float work = _coldSeen > 0 ? 1f - (float)left / _coldSeen : 0f;
        float timed = _coldMinMillis > 0 ? Math.Min(1f, elapsed / (float)_coldMinMillis) : 0f;
        float ratio = Math.Max(work, timed);
        if (ratio <= _coldShown) return;
        _coldShown = ratio;
        _loadScreen?.UpdateProgress(ratio);
        LoadProgress?.Invoke((int)Math.Round(ratio * 100f), 100);
    }

    //A map that is one building has no tiles and no archive thread, so the streaming step that
    //drives the bar never runs for it: this counts what it can - the building's own interior models -
    //and lets the rest of the hold be told by the clock.
    private void StepWmoColdLoad()
    {
        if (!_coldLoad || !WmoOnly) return;
        StepColdLoad(0, 0, 0, _doodads?.ModelsQueued ?? 0);
    }

    //The view is whole: the bar is filled and left up for the frame that found it so, and the
    //next one hands the window over to the scene.
    private void EndColdLoad()
    {
        _coldLoad = false;

        //A map that is one building has no archive thread left to turn the indicator off later, so
        //this is where it goes off; a streamed map keeps its own thread answering for it.
        if (_wdt == null) SetLoading(false);

        _loadScreen?.SetLoaded();
        LoadProgress?.Invoke(100, 100);
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
        BeginColdLoad(mapName);
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

        bool newWork = first || nearEdge || _tilesDirty || _objectsPending > 0;

        (int X, int Y) center;
        if (first || nearEdge)            center = t;
        else if (_tilesDirty || _buildPending || _objectsPending > 0 || _coldLoad) center = _builtTile;
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

        //Buildings come in on the same budget the terrain meshes are built on, so a whole map's
        //WMOs are opened a few roots a frame rather than in one frozen breath: the frame keeps
        //drawing while they arrive, and whatever is left waiting keeps the cold load going.
        if (!_buildPending && _objectsDirty)
        {
            if (_doodads != null && _texCache != null) _doodads.Load(order, _texCache);
            _objectsPending = _wmos != null && _texCache != null
                ? _wmos.Load(order, _texCache, Math.Max(1, WmoBuildBudget)) : 0;
            _objectsDirty = _objectsPending > 0;
        }

        SetTileCount(order.Count);
        StepColdLoad(MissingTiles(center), pending, _objectsPending,
            _doodads?.ModelsQueued ?? 0);
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
