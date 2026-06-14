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

public sealed partial class GlViewport : GLWpfControl, IDisposable
{
    private TerrainRenderer? _terrain;
    private DoodadRenderer? _doodads;
    private WmoRenderer? _wmos;
    private SkyRenderer? _sky;
    private BrushOverlay? _brush;
    private TextOverlay? _text;
    private GizmoRenderer? _gizmo;
    private TextureCache? _texCache;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool EditMode { get; set; }

    private SculptTool CurrentSculptTool =>
        FlattenMode ? SculptTool.Flatten : DeleteMode ? SculptTool.Delete :
        SmoothMode ? SculptTool.Smooth : NoiseMode ? SculptTool.Noise :
        ShadowMode ? SculptTool.Shadow : SculptTool.Sculpt;

    private void ToggleSculptTool(SculptTool t)
    {
        if (!EditMode) return;
        if (RiverMode) { RiverMode = false; _riverNodes.Clear(); _riverVertCount = 0; }
        var target = CurrentSculptTool == t ? SculptTool.Sculpt : t;
        FlattenMode = target == SculptTool.Flatten;
        DeleteMode  = target == SculptTool.Delete;
        SmoothMode  = target == SculptTool.Smooth;
        NoiseMode   = target == SculptTool.Noise;
        ShadowMode  = target == SculptTool.Shadow;
        Notify?.Invoke(target == SculptTool.Sculpt ? "Sculpt height (raise/lower)." :
            target == SculptTool.Shadow ? "Shadow brush ON - paint baked shadow, Ctrl+LMB erases (ghost shadows, seams)." :
            $"{target} brush ON.");
        ModeChanged?.Invoke(ModeLabel());
    }

    private readonly HashSet<int> _dirtyAlpha = new();

    private bool _blendMode;

    private bool _blendAggressive;
    private int _blendBand = 16;
    public bool BlendAggressive => _blendAggressive;
    public int BlendBandWidth => _blendBand;
    public event Action? OpenBlendSettings;
    public void SetBlendConfig(bool aggressive, int band)
    {
        _blendAggressive = aggressive;
        _blendBand = Math.Clamp(band, 2, 64);
        Notify?.Invoke($"Blend: {(aggressive ? "aggressive" : "feather")}, band {_blendBand}.");
    }
    private readonly List<(AdtFile Adt, int TileX, int TileY, int CRow, int CCol)> _blendSel = new();
    private float[] _outline = new float[4 * 9 * 3];
    private readonly List<float> _compatPts = new();
    private float[] _compatBuf = Array.Empty<float>();
    private int _compatVerts;

    private readonly string?[] _genTex = new string?[4];
    private readonly float[] _genCov = { 0.55f, 0.35f, 0.18f };
    private float _genScale = 0.018f;

    public bool GenerateReady => _genTex[0] != null && _genTex[1] != null && _genTex[2] != null && _genTex[3] != null;

    public float GenScale => _genScale;
    public string? GenTex(int i) => i >= 0 && i < 4 ? _genTex[i] : null;
    public float GenCoverage(int layer) => layer >= 1 && layer <= 3 ? _genCov[layer - 1] : 0f;

    private readonly string?[] _roadTex = new string?[3];
    private float _roadWidth = 0.45f;
    private float _roadContour = 0.30f;
    private float _roadNoise = 0.12f;

    public bool RoadReady => _roadTex[0] != null && _roadTex[1] != null && _roadTex[2] != null;

    public event Action? OpenTexturePicker;

    public float RoadWidth => _roadWidth;
    public float RoadContour => _roadContour;
    public float RoadNoise => _roadNoise;
    public string? RoadTex(int i) => i >= 0 && i < 3 ? _roadTex[i] : null;

    private readonly string?[] _autoTex = new string?[3];
    private float _autoLowDeg = 27f;
    private float _autoHighDeg = 50f;

    public bool AutoPaintReady => _autoTex[0] != null && _autoTex[2] != null;
    public string? AutoPaintTex(int i) => i >= 0 && i < 3 ? _autoTex[i] : null;
    public float AutoPaintLowDeg => _autoLowDeg;
    public float AutoPaintHighDeg => _autoHighDeg;

    public event Action<string>? ModeChanged;

    public enum EditorMode { None, Edit, Texture, Doodad, Wmo, FlightPath, Liquid, Npc, Herb }

    private EditorMode CurrentPrimary =>
        EditMode ? EditorMode.Edit : TextureMode ? EditorMode.Texture :
        DoodadMode ? EditorMode.Doodad : WmoMode ? EditorMode.Wmo :
        FlightMode ? EditorMode.FlightPath : LiquidMode ? EditorMode.Liquid :
        NpcMode ? EditorMode.Npc : HerbMode ? EditorMode.Herb : EditorMode.None;

    public void SetPrimaryMode(EditorMode m)
    {
        EditMode    = m == EditorMode.Edit;
        TextureMode = m == EditorMode.Texture;
        DoodadMode  = m == EditorMode.Doodad;
        WmoMode     = m == EditorMode.Wmo;
        LiquidMode  = m == EditorMode.Liquid;
        bool wasFlight = FlightMode;
        FlightMode  = m == EditorMode.FlightPath;
        bool wasNpc = NpcMode;
        NpcMode     = m == EditorMode.Npc;
        if (NpcMode && !wasNpc) NpcModeEntered?.Invoke();
        else if (!NpcMode && wasNpc) HideNpcs();
        bool wasHerb = HerbMode;
        HerbMode    = m == EditorMode.Herb;
        if (HerbMode && !wasHerb) HerbModeEntered?.Invoke();
        else if (!HerbMode && wasHerb) HideHerbs();

        if (!TextureMode) { PaintTexture = null; IsolateLayer = false; GenerateMode = false; RoadMode = false; AutoPaintMode = false; }
        if (!EditMode) { RiverMode = false; _riverNodes.Clear(); _riverVertCount = 0; }
        if (!DoodadMode) PlaceDoodad = null;
        if (!WmoMode) PlaceWmo = null;
        if (!EditMode) { FlattenMode = false; DeleteMode = false; SmoothMode = false; NoiseMode = false; ShadowMode = false; }
        if (!LiquidMode) { _liqSel.Clear(); _liqArmed = 0; _liqScrollStroke = null; }
        if (!EditMode && !TextureMode) _blendMode = false;
        _areaMode = false;
        if (_hmPlacing) { _hmPlacing = false; _hmCornerA = null; _hmLum = null; }

        if (FlightMode) EnterFlightMode();
        else if (wasFlight) ClearFlightSelection();
        if (FlightMode != wasFlight) FlightPathsChanged?.Invoke(FlightMode);

        PaintTextureChanged?.Invoke(PaintTexture);
        ModeChanged?.Invoke(ModeLabel());
    }

    private void TogglePrimaryMode(EditorMode m) => SetPrimaryMode(CurrentPrimary == m ? EditorMode.None : m);

    public enum Tool { Navigate, Sculpt, Flatten, Smooth, Noise, Delete, Shadow, Liquid, Texture, Doodad, Wmo, Flight, Npc, Zone, Herb }

    public Tool CurrentTool =>
        _areaMode ? Tool.Zone :
        FlightMode ? Tool.Flight : DoodadMode ? Tool.Doodad : WmoMode ? Tool.Wmo :
        TextureMode ? Tool.Texture : LiquidMode ? Tool.Liquid : NpcMode ? Tool.Npc :
        HerbMode ? Tool.Herb :
        EditMode ? (FlattenMode ? Tool.Flatten : DeleteMode ? Tool.Delete : SmoothMode ? Tool.Smooth :
                    NoiseMode ? Tool.Noise : ShadowMode ? Tool.Shadow : Tool.Sculpt) :
        Tool.Navigate;

    public void ActivateTool(Tool t)
    {
        switch (t)
        {
            case Tool.Navigate: SetPrimaryMode(EditorMode.None); break;
            case Tool.Texture:  SetPrimaryMode(EditorMode.Texture); break;
            case Tool.Doodad:   SetPrimaryMode(EditorMode.Doodad); break;
            case Tool.Wmo:      SetPrimaryMode(EditorMode.Wmo); break;
            case Tool.Flight:   SetPrimaryMode(EditorMode.FlightPath); break;
            case Tool.Liquid:   SetPrimaryMode(EditorMode.Liquid); break;
            case Tool.Npc:      SetPrimaryMode(EditorMode.Npc); break;
            case Tool.Herb:     SetPrimaryMode(EditorMode.Herb); break;
            case Tool.Zone:     if (!_areaMode) ToggleAreaMode(); break;
            default:
                if (!EditMode) SetPrimaryMode(EditorMode.Edit);
                FlattenMode = t == Tool.Flatten;
                DeleteMode  = t == Tool.Delete;
                SmoothMode  = t == Tool.Smooth;
                NoiseMode   = t == Tool.Noise;
                ShadowMode  = t == Tool.Shadow;
                ModeChanged?.Invoke(ModeLabel());
                break;
        }
    }

    private string ModeLabel() => _hmPlacing ? "Mode: Place heightmap"
        : _areaMode ? (_targetAreaId > 0
            ? $"Mode: Zones - painting {_targetAreaId}{(AreaName(_targetAreaId) is string an ? $" ({an})" : "")}"
            : "Mode: Zones - inspect")
        : CurrentPrimary switch
    {
        EditorMode.Edit    => DeleteMode ? "Mode: Delete terrain" : FlattenMode ? "Mode: Flatten" : SmoothMode ? "Mode: Smooth" : NoiseMode ? "Mode: Noise" : ShadowMode ? "Mode: Shadow brush" : "Mode: Sculpt height",
        EditorMode.Texture => string.IsNullOrEmpty(PaintTexture) ? "Mode: Texture (no texture)" : "Mode: Paint " + System.IO.Path.GetFileName(PaintTexture),
        EditorMode.Doodad  => "Mode: Doodads",
        EditorMode.Wmo     => "Mode: WMOs",
        EditorMode.Liquid  => _liqArmed != 0 ? $"Mode: Liquids - placing {WaterTypeName(_liqArmed)}"
                            : _liqSel.Count > 0 ? $"Mode: Liquids - {_liqSel.Count} chunk(s) @ {_liqSelLevel:F1}"
                            : "Mode: Liquids",
        EditorMode.FlightPath => _selectedPath != null ? $"Mode: Taxi - {_selectedPath.Name}" : "Mode: Taxi",
        EditorMode.Npc => $"Mode: NPC view ({_npcCount} spawns)",
        EditorMode.Herb => $"Mode: Herb/Vein view ({_herbShown.Count} nodes)",
        _ => "Mode: Navigate",
    };

    public GlViewport()
    {
        Focusable = true;
        Render += OnRenderTick;
        Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3 });
    }

    private bool _glReady;
    private double _dpiX = 1.0, _dpiY = 1.0;

    public new int Width => Math.Max(1, (int)Math.Round(ActualWidth * _dpiX));

    public new int Height => Math.Max(1, (int)Math.Round(ActualHeight * _dpiY));

    private bool IsHandleCreated => _glReady;

    [System.Runtime.InteropServices.DllImport("opengl32.dll")]
    private static extern IntPtr wglGetCurrentContext();
    [System.Runtime.InteropServices.DllImport("opengl32.dll")]
    private static extern IntPtr wglGetCurrentDC();
    [System.Runtime.InteropServices.DllImport("opengl32.dll")]
    private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

    private IntPtr _glDc, _glRc;

    private void MakeCurrent()
    {
        if (_glRc != IntPtr.Zero && wglGetCurrentContext() != _glRc)
            wglMakeCurrent(_glDc, _glRc);
    }

    public void SetMpq(MpqManager mpq)
    {
        _mpq = mpq;
        if (IsHandleCreated)
        {
            _texCache?.Dispose();
            _doodads?.Dispose();
            _wmos?.Dispose();
        }
        _texCache = new TextureCache(mpq);
        _doodads = new DoodadRenderer(mpq) { RenderDistance = _doodadDist };
        _wmos = new WmoRenderer(mpq) { DoodadSink = _doodads, RenderDistance = _wmoDist };
    }

    private float _doodadDist = 450f, _wmoDist = 900f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float DoodadRenderDistance
    {
        get => _doodadDist;
        set { _doodadDist = value; if (_doodads != null) _doodads.RenderDistance = value; }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float WmoRenderDistance
    {
        get => _wmoDist;
        set { _wmoDist = value; if (_wmos != null) _wmos.RenderDistance = value; }
    }

    public List<(uint Horde, uint Alliance, string Sample, int Count)> FlightMountOptions() =>
        _flightData?.MountPairs ?? new List<(uint, uint, string, int)>();

    public int FlightEditCount => _flightData?.EditCount ?? 0;

    private void ToggleGenerateMode()
    {
        if (!TextureMode || DoodadMode || WmoMode)
        {
            Notify?.Invoke("Enable texture mode (Ctrl+T) to use the ground generator.");
            return;
        }
        GenerateMode = !GenerateMode;
        _blendMode = false;
        RoadMode = false;
        AutoPaintMode = false;
        _areaMode = false;
        if (GenerateMode) { OpenGeneratePicker?.Invoke(); return; }
        Notify?.Invoke("Ground generator off.");
    }

    private void ToggleRoadMode()
    {
        if (!TextureMode || DoodadMode || WmoMode)
        {
            Notify?.Invoke("Enable texture mode (Ctrl+T) to use the road generator.");
            return;
        }
        RoadMode = !RoadMode;
        _blendMode = false;
        GenerateMode = false;
        AutoPaintMode = false;
        _areaMode = false;
        if (RoadMode)
        {
            PaintTexture = null;
            if (!RoadReady) OpenRoadPicker?.Invoke();
            Notify?.Invoke("Road mode: Ctrl+N to set textures, drag to paint.");
            return;
        }
        Notify?.Invoke("Road off.");
    }

    private void ToggleAutoPaintMode()
    {
        if (!TextureMode || DoodadMode || WmoMode)
        {
            Notify?.Invoke("Enable texture mode (Ctrl+T) to use slope auto-paint.");
            return;
        }
        AutoPaintMode = !AutoPaintMode;
        _blendMode = false;
        GenerateMode = false;
        RoadMode = false;
        _areaMode = false;
        if (AutoPaintMode)
        {
            PaintTexture = null;
            if (!AutoPaintReady) OpenAutoPaintPicker?.Invoke();
            Notify?.Invoke("Slope auto-paint: drag to texture by slope. Ctrl+N to configure.");
            return;
        }
        Notify?.Invoke("Slope auto-paint off.");
    }

    private void ApplyAutoPaintBrush(Vector3 hit)
    {
        float r = BrushRadius;
        string flatTex = _autoTex[0]!, cliffTex = _autoTex[2]!;
        string? midTex = _autoTex[1];
        const float tw = 4f;
        const float jitterDeg = 3f;
        const float eps = 1.5f;

        var rebuild = new HashSet<AdtFile>();
        var edited = new HashSet<AdtFile>();
        int skippedChunks = 0;
        Span<(int Map, float W)> bands = stackalloc (int, float)[3];

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

                if (_stroke != null && !_stroke.Textures.ContainsKey(chunk))
                { _stroke.Textures[chunk] = SnapshotTexState(chunk); _stroke.Tiles.Add(adt); }

                bool fresh = false;
                int flatMap = EnsureAutoLayer(adt, chunk, flatTex, ref fresh);
                int midMap = midTex != null ? EnsureAutoLayer(adt, chunk, midTex, ref fresh) : -1;
                int cliffMap = EnsureAutoLayer(adt, chunk, cliffTex, ref fresh);
                if (fresh) rebuild.Add(adt);
                if (flatMap == -2 || cliffMap == -2 || (midTex != null && midMap == -2)) { skippedChunks++; continue; }

                float cs = AdtFile.ChunkSize, sx = 63f / cs;
                int rx0 = 64, ry0 = 64, rx1 = -1, ry1 = -1;
                int tX0 = Math.Clamp((int)MathF.Floor((hit.X - r + chunk.Position.Y) * sx), 0, 63);
                int tX1 = Math.Clamp((int)MathF.Ceiling((hit.X + r + chunk.Position.Y) * sx), 0, 63);
                int tZ0 = Math.Clamp((int)MathF.Floor((hit.Z - r + chunk.Position.X) * sx), 0, 63);
                int tZ1 = Math.Clamp((int)MathF.Ceiling((hit.Z + r + chunk.Position.X) * sx), 0, 63);

                for (int tz = tZ0; tz <= tZ1; tz++)
                for (int txp = tX0; txp <= tX1; txp++)
                {
                    float gx = -(chunk.Position.Y - txp / 63f * cs);
                    float gz = -(chunk.Position.X - tz / 63f * cs);
                    float dx = gx - hit.X, dz = gz - hit.Z;
                    float t = MathF.Sqrt(dx * dx + dz * dz) / r;
                    if (t >= 1f) continue;

                    float hxp = HeightAtGL(gx + eps, gz), hxm = HeightAtGL(gx - eps, gz);
                    float hzp = HeightAtGL(gx, gz + eps), hzm = HeightAtGL(gx, gz - eps);
                    if (float.IsNaN(hxp) || float.IsNaN(hxm) || float.IsNaN(hzp) || float.IsNaN(hzm)) continue;
                    float dhdx = (hxp - hxm) / (2f * eps), dhdz = (hzp - hzm) / (2f * eps);
                    float angle = MathHelper.RadiansToDegrees(MathF.Atan(MathF.Sqrt(dhdx * dhdx + dhdz * dhdz)));
                    angle += (Fbm(gx * 0.23f, gz * 0.23f) - 0.5f) * 2f * jitterDeg;

                    float wCliff = Smoothstep(_autoHighDeg - tw, _autoHighDeg + tw, angle);
                    float wMid = 0f;
                    if (midTex != null)
                        wMid = (1f - wCliff) * Smoothstep(_autoLowDeg - tw, _autoLowDeg + tw, angle);

                    int nb = 0;
                    if (flatMap >= 0) bands[nb++] = (flatMap, 1f - wCliff - wMid);
                    if (midMap >= 0 && midTex != null) bands[nb++] = (midMap, wMid);
                    if (cliffMap >= 0) bands[nb++] = (cliffMap, wCliff);
                    for (int i = 1; i < nb; i++)
                        for (int j = i; j > 0 && bands[j].Map > bands[j - 1].Map; j--)
                            (bands[j], bands[j - 1]) = (bands[j - 1], bands[j]);

                    int idx = tz * 64 + txp;
                    float blend = 1f - Smoothstep(0.75f, 1f, t);
                    bool wrote = false;

                    float above = 0f;
                    for (int b = 0; b < nb; b++)
                    {
                        float a = Math.Clamp(bands[b].W / MathF.Max(1f - above, 1e-4f), 0f, 1f);
                        above += bands[b].W;
                        var map = chunk.AlphaMaps[bands[b].Map];
                        if (map == null) continue;
                        byte nv = (byte)MathF.Round(map[idx] + (a * 255f - map[idx]) * blend);
                        if (nv != map[idx]) { map[idx] = nv; wrote = true; }
                    }
                    for (int mi = 0; mi < chunk.NLayers - 1; mi++)
                    {
                        if (mi == flatMap || mi == midMap || mi == cliffMap) continue;
                        var map = chunk.AlphaMaps[mi];
                        if (map == null || map[idx] == 0) continue;
                        byte nv = (byte)MathF.Round(map[idx] * (1f - blend));
                        if (nv != map[idx]) { map[idx] = nv; wrote = true; }
                    }
                    if (!wrote) continue;

                    if (txp < rx0) rx0 = txp; if (txp > rx1) rx1 = txp;
                    if (tz < ry0) ry0 = tz; if (tz > ry1) ry1 = tz;
                }

                if (rx1 < rx0) continue;
                edited.Add(adt);
                if (rebuild.Contains(adt)) continue;
                int w = rx1 - rx0 + 1, h = ry1 - ry0 + 1;
                for (int mi = 0; mi < chunk.NLayers - 1; mi++)
                {
                    var map = chunk.AlphaMaps[mi];
                    if (map == null) continue;
                    if (_terrain!.TryGetChunkAlpha(adt, cy, cx, mi, out int glId))
                    { TextureCache.UpdateAlphaRect(glId, map, rx0, ry0, w, h); _dirtyAlpha.Add(glId); }
                }
            }
        }

        foreach (var adt in rebuild) { edited.Add(adt); _terrain!.RebuildTile(adt, _texCache); }
        foreach (var adt in edited) MarkEdited(adt);
        if (skippedChunks > 0) Notify?.Invoke($"Auto-paint: {skippedChunks} chunk(s) skipped (4-layer limit).");
    }

    private int EnsureAutoLayer(AdtFile adt, Salmiak.Core.Formats.MapChunk chunk, string texPath, ref bool changed)
    {
        int ti = ResolveTex(adt, texPath);
        for (int l = 0; l < chunk.NLayers; l++)
            if (chunk.Layers[l].TextureIndex == ti) return l - 1;
        if (chunk.NLayers >= MapChunkMaxLayers) return -2;
        int L = Math.Max(chunk.NLayers, 1);
        chunk.Layers[L] = new Salmiak.Core.Formats.TextureLayer { TextureIndex = (uint)ti };
        chunk.AlphaMaps[L - 1] ??= new byte[4096];
        chunk.NLayers = L + 1;
        changed = true;
        return L - 1;
    }

    private void ApplyRoadBrush(Vector3 hit)
    {
        float r = BrushRadius;
        float core = _roadWidth, edge = Math.Min(0.95f, _roadWidth + _roadContour);
        float footEdge = Math.Min(0.99f, edge + 0.15f);
        var rebuild = new HashSet<AdtFile>();
        var edited = new HashSet<AdtFile>();

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

                if (_stroke != null && !_stroke.Textures.ContainsKey(chunk))
                { _stroke.Textures[chunk] = SnapshotTexState(chunk); _stroke.Tiles.Add(adt); }

                bool fresh = SetupRoadLayers(adt, chunk, out int roadMap, out int c1Map, out int c2Map);
                if (roadMap < 0) { if (fresh) rebuild.Add(adt); continue; }
                if (fresh) rebuild.Add(adt);

                var aR = chunk.AlphaMaps[roadMap]!;
                var aC1 = c1Map >= 0 ? chunk.AlphaMaps[c1Map] : null;
                var aC2 = c2Map >= 0 ? chunk.AlphaMaps[c2Map] : null;

                float cs = AdtFile.ChunkSize, sx = 63f / cs;
                int rx0 = 64, ry0 = 64, rx1 = -1, ry1 = -1;
                int tX0 = Math.Clamp((int)MathF.Floor((hit.X - r + chunk.Position.Y) * sx), 0, 63);
                int tX1 = Math.Clamp((int)MathF.Ceiling((hit.X + r + chunk.Position.Y) * sx), 0, 63);
                int tZ0 = Math.Clamp((int)MathF.Floor((hit.Z - r + chunk.Position.X) * sx), 0, 63);
                int tZ1 = Math.Clamp((int)MathF.Ceiling((hit.Z + r + chunk.Position.X) * sx), 0, 63);

                for (int tz = tZ0; tz <= tZ1; tz++)
                for (int txp = tX0; txp <= tX1; txp++)
                {
                    float gx = -(chunk.Position.Y - txp / 63f * cs);
                    float gz = -(chunk.Position.X - tz / 63f * cs);
                    float dx = gx - hit.X, dz = gz - hit.Z;
                    float t = MathF.Sqrt(dx * dx + dz * dz) / r;
                    if (t >= 1f) continue;

                    float wobble = (Fbm(gx * 0.05f, gz * 0.05f) - 0.5f) * _roadNoise;
                    float tt = Math.Clamp(t + wobble, 0f, 1f);
                    float road = 1f - Smoothstep(core, edge, tt);
                    float band = Smoothstep(core - 0.2f, edge, tt) * (1f - Smoothstep(edge, footEdge, tt));

                    int idx = tz * 64 + txp;
                    bool grew = false;
                    float oldR = aR[idx] / 255f, nr = MathF.Max(oldR, road);
                    if (nr > oldR) { aR[idx] = (byte)MathF.Round(nr * 255f); grew = true; }
                    if (aC1 != null) { float o = aC1[idx] / 255f, v = MathF.Max(o, band); if (v > o) { aC1[idx] = (byte)MathF.Round(v * 255f); grew = true; } }
                    if (aC2 != null) { float blend = Smoothstep(0.35f, 0.65f, Fbm(gx * 0.03f + 50f, gz * 0.03f + 50f)); float o = aC2[idx] / 255f, v = MathF.Max(o, band * blend); if (v > o) { aC2[idx] = (byte)MathF.Round(v * 255f); grew = true; } }
                    if (!grew) continue;

                    if (txp < rx0) rx0 = txp; if (txp > rx1) rx1 = txp;
                    if (tz < ry0) ry0 = tz; if (tz > ry1) ry1 = tz;
                }

                if (rx1 < rx0) continue;
                edited.Add(adt);
                if (rebuild.Contains(adt)) continue;
                int w = rx1 - rx0 + 1, h = ry1 - ry0 + 1;
                if (_terrain!.TryGetChunkAlpha(adt, cy, cx, roadMap, out int g0)) { TextureCache.UpdateAlphaRect(g0, aR, rx0, ry0, w, h); _dirtyAlpha.Add(g0); }
                if (aC1 != null && _terrain!.TryGetChunkAlpha(adt, cy, cx, c1Map, out int g1)) { TextureCache.UpdateAlphaRect(g1, aC1, rx0, ry0, w, h); _dirtyAlpha.Add(g1); }
                if (aC2 != null && _terrain!.TryGetChunkAlpha(adt, cy, cx, c2Map, out int g2)) { TextureCache.UpdateAlphaRect(g2, aC2, rx0, ry0, w, h); _dirtyAlpha.Add(g2); }
            }
        }

        foreach (var adt in rebuild) { edited.Add(adt); _terrain!.RebuildTile(adt, _texCache); }
        foreach (var adt in edited) MarkEdited(adt);
    }

    private bool SetupRoadLayers(AdtFile adt, Salmiak.Core.Formats.MapChunk chunk,
                                 out int roadMap, out int c1Map, out int c2Map)
    {
        bool changed = false;
        roadMap = EnsureRoadLayer(adt, chunk, _roadTex[0]!, ref changed);
        c1Map = EnsureRoadLayer(adt, chunk, _roadTex[1]!, ref changed);
        c2Map = EnsureRoadLayer(adt, chunk, _roadTex[2]!, ref changed);
        return changed;
    }

    private int EnsureRoadLayer(AdtFile adt, Salmiak.Core.Formats.MapChunk chunk, string texPath, ref bool changed)
    {
        int ti = ResolveTex(adt, texPath);
        for (int l = 0; l < chunk.NLayers; l++)
            if (chunk.Layers[l].TextureIndex == ti) return l - 1;
        if (chunk.NLayers >= MapChunkMaxLayers) return -1;
        int L = chunk.NLayers;
        chunk.Layers[L] = new Salmiak.Core.Formats.TextureLayer { TextureIndex = (uint)ti };
        chunk.AlphaMaps[L - 1] ??= new byte[4096];
        chunk.NLayers = L + 1;
        changed = true;
        return L - 1;
    }

    private void GenerateAt(int mx, int my)
    {
        if (_terrain == null || _wdt == null) return;
        if (!GenerateReady) { OpenGeneratePicker?.Invoke(); return; }
        if (!PickTerrain(mx, my, out var hit)) return;
        if (!TryGetChunkAt(hit, out var adt, out var chunk, out _, out _, out _, out _)) return;

        var rec = new Stroke();
        rec.Textures[chunk] = SnapshotTexState(chunk);
        rec.Tiles.Add(adt);
        PushUndo(rec);

        SetupGenLayers(adt, chunk);
        GenTexture(chunk);
        MarkEdited(adt);
        _terrain.RebuildTile(adt, _texCache);
    }

    private void SetupGenLayers(AdtFile adt, Salmiak.Core.Formats.MapChunk chunk)
    {
        chunk.NLayers = 4;
        for (int l = 0; l < 4; l++)
            chunk.Layers[l] = new Salmiak.Core.Formats.TextureLayer { TextureIndex = (uint)ResolveTex(adt, _genTex[l]!) };
        chunk.AlphaMaps[0] ??= new byte[4096];
        chunk.AlphaMaps[1] ??= new byte[4096];
        chunk.AlphaMaps[2] ??= new byte[4096];
    }

    private static int ResolveTex(AdtFile adt, string path)
    {
        int i = adt.Textures.FindIndex(t => string.Equals(t, path, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { adt.Textures.Add(path); i = adt.Textures.Count - 1; }
        return i;
    }

    private void GenTexture(Salmiak.Core.Formats.MapChunk chunk)
    {
        var a1 = chunk.AlphaMaps[0]!;
        var a2 = chunk.AlphaMaps[1]!;
        var a3 = chunk.AlphaMaps[2]!;
        float cs = AdtFile.ChunkSize;
        float s = _genScale;
        float t1 = 1f - _genCov[0], t2 = 1f - _genCov[1], t3 = 1f - _genCov[2];
        const float edge = 0.14f;

        for (int ty = 0; ty < 64; ty++)
        for (int tx = 0; tx < 64; tx++)
        {
            float gx = -(chunk.Position.Y - tx / 63f * cs);
            float gz = -(chunk.Position.X - ty / 63f * cs);

            float n1 = WarpedFbm(gx * s,          gz * s,          0.9f);
            float n2 = WarpedFbm(gx * s * 2.1f + 53f, gz * s * 2.1f + 53f, 0.7f);
            float n3 = WarpedFbm(gx * s * 4.3f + 131f, gz * s * 4.3f + 131f, 0.5f);

            float c1 = Smoothstep(t1 - edge, t1 + edge, n1);
            float c2 = Smoothstep(t2 - edge, t2 + edge, n2);
            float c3 = Smoothstep(t3 - edge, t3 + edge, n3);

            int idx = ty * 64 + tx;
            a1[idx] = (byte)MathF.Round(c1 * 255f);
            a2[idx] = (byte)MathF.Round(c2 * 255f);
            a3[idx] = (byte)MathF.Round(c3 * 255f);
        }
    }

    private static float WarpedFbm(float x, float z, float warp)
    {
        float wx = Fbm(x + 5.2f, z + 1.3f) - 0.5f;
        float wz = Fbm(x + 8.3f, z + 2.8f) - 0.5f;
        return Fbm(x + wx * warp, z + wz * warp);
    }

    private static float Smoothstep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Hash2(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }

    private static float ValueNoise(float x, float z)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float fx = x - xi, fz = z - zi;
        float a = Hash2(xi, zi), b = Hash2(xi + 1, zi), c = Hash2(xi, zi + 1), d = Hash2(xi + 1, zi + 1);
        float ux = fx * fx * (3f - 2f * fx), uz = fz * fz * (3f - 2f * fz);
        return a + (b - a) * ux + (c - a) * uz + (a - b - c + d) * ux * uz;
    }

    private static float Fbm(float x, float z)
    {
        float sum = 0f, amp = 0.5f, freq = 1f;
        for (int i = 0; i < 4; i++) { sum += ValueNoise(x * freq, z * freq) * amp; freq *= 2f; amp *= 0.5f; }
        return sum;
    }

    private void Undo() => ApplyStroke(_undo, _redo, "Undo");
    private void Redo() => ApplyStroke(_redo, _undo, "Redo");

    private void ApplyStroke(List<Stroke> source, List<Stroke> dest, string label)
    {
        if (source.Count == 0) { Notify?.Invoke($"Nothing to {label.ToLowerInvariant()}."); return; }
        var rec = source[^1];
        source.RemoveAt(source.Count - 1);
        if (rec.Placed.Count > 0 || rec.Removed.Count > 0) ClearSelection();

        var inv = new Stroke();
        foreach (var adt in rec.Tiles) inv.Tiles.Add(adt);

        foreach (var (chunk, orig) in rec.Heights)
        {
            inv.Heights[chunk] = (float[])chunk.Heights.Clone();
            Array.Copy(orig, chunk.Heights, orig.Length);
        }
        foreach (var (chunk, h) in rec.Holes) { inv.Holes[chunk] = chunk.Holes; chunk.Holes = h; }
        foreach (var (chunk, liq) in rec.Liquid) { inv.Liquid[chunk] = chunk.Liquid; chunk.Liquid = liq; }
        foreach (var (chunk, id) in rec.Area) { inv.Area[chunk] = chunk.AreaId; chunk.AreaId = id; }
        foreach (var (chunk, s) in rec.Textures)
        {
            inv.Textures[chunk] = SnapshotTexState(chunk);
            chunk.NLayers = s.NLayers;
            Array.Copy(s.Layers, chunk.Layers, s.Layers.Length);
            for (int i = 0; i < chunk.AlphaMaps.Length; i++) chunk.AlphaMaps[i] = s.AlphaMaps[i];
        }
        foreach (var (chunk, sh) in rec.Shadows)
        {
            inv.Shadows[chunk] = chunk.ShadowMap != null ? (byte[])chunk.ShadowMap.Clone() : null;
            chunk.ShadowMap = sh != null ? (byte[])sh.Clone() : null;
        }

        bool changedPlacements = false;

        foreach (var (adt, def) in rec.Placed) { adt.Doodads.Remove(def); inv.Removed.Add((adt, def)); changedPlacements = true; }
        foreach (var (adt, def) in rec.Removed) { adt.Doodads.Add(def); inv.Placed.Add((adt, def)); changedPlacements = true; }
        foreach (var (adt, def) in rec.PlacedWmo) { adt.Wmos.Remove(def); inv.RemovedWmo.Add((adt, def)); changedPlacements = true; }
        foreach (var (adt, def) in rec.RemovedWmo) { adt.Wmos.Add(def); inv.PlacedWmo.Add((adt, def)); changedPlacements = true; }

        foreach (var (adt, index, before) in rec.DoodadXform)
            if (index >= 0 && index < adt.Doodads.Count) { inv.DoodadXform.Add((adt, index, adt.Doodads[index])); adt.Doodads[index] = before; changedPlacements = true; }
        foreach (var (adt, index, before) in rec.WmoXform)
            if (index >= 0 && index < adt.Wmos.Count) { inv.WmoXform.Add((adt, index, adt.Wmos[index])); adt.Wmos[index] = before; changedPlacements = true; }

        foreach (var adt in rec.Tiles)
        {
            if (rec.Textures.Count > 0 || rec.Holes.Count > 0 || rec.Shadows.Count > 0) _terrain?.RebuildTile(adt, _texCache);
            else _terrain?.UpdateTileHeights(adt);
            if (rec.Liquid.Count > 0) _terrain?.RebuildWater(adt);
        }

        bool movedFollowers = false;
        foreach (var kv in rec.DoodadPos)
        {
            var (adt, idx) = kv.Key;
            if (idx >= 0 && idx < adt.Doodads.Count) { var d = adt.Doodads[idx]; inv.DoodadPos[kv.Key] = d.Position; d.Position = kv.Value; adt.Doodads[idx] = d; movedFollowers = true; }
        }
        foreach (var kv in rec.WmoPos)
        {
            var (adt, idx) = kv.Key;
            if (idx >= 0 && idx < adt.Wmos.Count) { var d = adt.Wmos[idx]; inv.WmoPos[kv.Key] = d.Position; d.Position = kv.Value; adt.Wmos[idx] = d; movedFollowers = true; }
        }

        if (changedPlacements || movedFollowers) RefreshPlacements();

        dest.Add(inv);
        if (dest.Count > MaxUndo) dest.RemoveAt(0);
        Notify?.Invoke($"{label}.");
    }

    public new void Dispose()
    {
        StopWorker();
        if (IsHandleCreated)
        {
            _terrain?.Dispose();
            _doodads?.Dispose();
            _wmos?.Dispose();
            _sky?.Dispose();
            _brush?.Dispose();
            _text?.Dispose();
            _gizmo?.Dispose();
            _texCache?.Dispose();
            if (_minimapTex != 0) GL.DeleteTexture(_minimapTex);
        }
        base.Dispose();
    }
}
