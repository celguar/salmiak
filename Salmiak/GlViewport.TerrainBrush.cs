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
    private float[] _ring = new float[(BrushSegments + 1) * 3];

    private const int BrushSegments = 48;

    public event Action<float>? BrushRadiusChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float BrushRadius { get; set; } = 20f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float BrushStrength { get; set; } = 40f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool FlattenMode { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DeleteMode { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SmoothMode { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool NoiseMode { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float NoiseGrain { get; set; } = 8f;

    public enum BrushShape { Smooth, Linear, Flat, Sphere }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public BrushShape BrushFalloff { get; set; } = BrushShape.Smooth;

    private static float Falloff(float dist, float radius, BrushShape shape)
    {
        if (dist >= radius) return 0f;
        float t = 1f - dist / radius;
        return shape switch
        {
            BrushShape.Flat   => 1f,
            BrushShape.Linear => t,
            BrushShape.Sphere => MathF.Sqrt(MathF.Max(0f, 1f - (1f - t) * (1f - t))),
            _                 => t * t * (3f - 2f * t),
        };
    }

    private enum SculptTool { Sculpt, Flatten, Delete, Smooth, Noise, Shadow }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowMode { get; set; }

    //Shadow bake settings: ray-traced shadows from WMO/M2 triangles (ported from WoWViewer's baker).
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    //Defaults match WoW Viewer's (30° height, 34° rotation), which is what reproduces Blizzard's
    //own baked shadows; see the sun vector build in BakeShadows for the axis mapping.
    public float ShadowSunHeight { get; set; } = 30f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float ShadowSunRotation { get; set; } = 34f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    //In ADT units, so 250 is 7.5 chunks of reach. WoW Viewer's own default, 150, only reaches 4.5
    //chunks and clips the crown of the taller trees: at the retail sun, a shadow runs about as far
    //above the ground as the object is tall. Longer rays also mean more work for texels that have
    //nothing near them, so raise it only as far as the scenery actually needs.
    public float ShadowMaxDistance { get; set; } = 250f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowDoodads { get; set; } = true;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowWmos { get; set; } = true;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowKeepOld { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowTerrainBlocks { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowAlphaTest { get; set; } = true;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    //Alpha ≥ this blocks light. 200 is where the ray-traced trees stop eating the leaves of
    //Blizzard's own shadows; 128 lets too much foliage cast.
    public int ShadowAlphaThreshold { get; set; } = 200;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowSkipTransparent { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowHighRes { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ShadowHighResSamples { get; set; } = 3;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ShadowHighResThreshold { get; set; } = 1;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShadowJitter { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float ShadowJitterValue { get; set; } = 0.005f;

    public enum BakeScope { SelectedChunks, EditedTiles, AllTiles }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DoodadFollow { get; set; }

    private float _flattenTarget = float.NaN;

    private uint _noiseSeed;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool RiverMode { get; private set; }

    private readonly List<Vector3> _riverNodes = new();

    private float _riverBedWidth = 14f;

    private float _riverBankWidth = 10f;

    private float _riverDepth = 6f;

    private int _riverWaterType = 1;

    private float _riverWaterDepth = 3f;

    private bool _prevCtrlJ, _prevRvEnter, _prevRvBack;

    private float[] _riverVerts = [];

    private int _riverVertCount;

    public event Action? OpenRiverPicker;

    public float RiverBedWidth => _riverBedWidth;

    public float RiverBankWidth => _riverBankWidth;

    public float RiverDepth => _riverDepth;

    public int RiverWaterType => _riverWaterType;

    public float RiverWaterDepth => _riverWaterDepth;

    public void SetRiverConfig(float bedWidth, float bankWidth, float depth, int waterType, float waterDepth)
    {
        _riverBedWidth = Math.Clamp(bedWidth, 4f, 60f);
        _riverBankWidth = Math.Clamp(bankWidth, 2f, 40f);
        _riverDepth = Math.Clamp(depth, 1f, 30f);
        _riverWaterType = Math.Clamp(waterType, 0, 4);
        _riverWaterDepth = Math.Clamp(waterDepth, 0.5f, _riverDepth);
        RebuildRiverPreview();
        Notify?.Invoke("River: click waypoints along the terrain, Enter carves, Backspace undoes a point.");
    }

    private int BuildBrushRing(Vector3 center)
    {
        int n = BrushSegments;
        for (int i = 0; i < n; i++)
        {
            float a = MathF.PI * 2f * i / n;
            float x = center.X + MathF.Cos(a) * BrushRadius;
            float z = center.Z + MathF.Sin(a) * BrushRadius;
            float y = HeightAtGL(x, z);
            if (float.IsNaN(y)) y = center.Y;
            _ring[i * 3] = x; _ring[i * 3 + 1] = y + 0.4f; _ring[i * 3 + 2] = z;
        }
        return n;
    }

    private void EditStep(float dt)
    {
        if (_terrain == null || _wdt == null) return;
        if (!string.IsNullOrEmpty(PlaceDoodad)) return;
        //The chunk picker of the Shadows tool is a click target, not a brush: while chunks are
        //being picked nothing may sculpt, otherwise a selection click also lifts the terrain.
        if (BakeChunksMode) return;
        if (!PickTerrain(_mouseX, _mouseY, out var hit)) return;

        if (TextureMode)
        {
            if (RoadMode) { if (RoadReady) ApplyRoadBrush(hit); return; }
            if (AutoPaintMode) { if (AutoPaintReady) ApplyAutoPaintBrush(hit); return; }
            bool erase = (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
            if (erase && (!string.IsNullOrEmpty(PaintTexture) || PaintLayer >= 1)) { ApplyEraseBrush(hit, dt); return; }
            if (!string.IsNullOrEmpty(PaintTexture)) ApplyPaintBrush(hit, dt);
            return;
        }

        if (RiverMode) return;
        if (ShadowMode && !BakeChunksMode) { ApplyShadowBrush(hit, (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0); return; }
        if (DeleteMode) { DeleteTerrainAt(hit, (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0); return; }
        if (SmoothMode) { ApplySmooth(hit, dt); return; }
        if (NoiseMode) { ApplyNoise(hit, dt); return; }
        if (FlattenMode) ApplyFlatten(hit, dt);
        else
        {
            bool lower = (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
            ApplyBrush(hit, dt, lower);
        }
    }

    private void ApplyShadowBrush(Vector3 hit, bool erase)
    {
        float r = BrushRadius;
        var rebuild = new HashSet<AdtFile>();
        var edited = new HashSet<AdtFile>();

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            if (!TileOverlapsBrush(kv.Key, hit, r)) continue;

            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null || !BrushHitsChunk(chunk, hit, r)) continue;
                if (erase && chunk.ShadowMap == null) continue;

                if (_stroke != null && !_stroke.Shadows.ContainsKey(chunk))
                {
                    _stroke.Shadows[chunk] = chunk.ShadowMap != null ? (byte[])chunk.ShadowMap.Clone() : null;
                    _stroke.Tiles.Add(adt);
                }

                bool freshMap = chunk.ShadowMap == null;
                chunk.ShadowMap ??= new byte[4096];
                var map = chunk.ShadowMap;

                float cs = AdtFile.ChunkSize, sx = 63f / cs;
                int rx0 = 64, ry0 = 64, rx1 = -1, ry1 = -1;
                int tX0 = Math.Clamp((int)MathF.Floor((hit.X - r + chunk.Position.Y) * sx), 0, 63);
                int tX1 = Math.Clamp((int)MathF.Ceiling((hit.X + r + chunk.Position.Y) * sx), 0, 63);
                int tZ0 = Math.Clamp((int)MathF.Floor((hit.Z - r + chunk.Position.X) * sx), 0, 63);
                int tZ1 = Math.Clamp((int)MathF.Ceiling((hit.Z + r + chunk.Position.X) * sx), 0, 63);
                bool changed = false;

                for (int tz = tZ0; tz <= tZ1; tz++)
                for (int txp = tX0; txp <= tX1; txp++)
                {
                    float gx = -(chunk.Position.Y - txp / 63f * cs);
                    float gz = -(chunk.Position.X - tz / 63f * cs);
                    float dx = gx - hit.X, dz = gz - hit.Z;
                    float d = MathF.Sqrt(dx * dx + dz * dz);
                    if (d >= r) continue;
                    float f = Falloff(d, r, BrushFalloff);
                    if (f < 0.999f && f < Fbm(gx * 2.3f, gz * 2.3f)) continue;

                    int idx = tz * 64 + txp;
                    byte nv = erase ? (byte)0 : (byte)255;
                    if (map[idx] == nv) continue;
                    map[idx] = nv;
                    changed = true;
                    if (txp < rx0) rx0 = txp; if (txp > rx1) rx1 = txp;
                    if (tz < ry0) ry0 = tz; if (tz > ry1) ry1 = tz;
                }

                if (!changed) { if (freshMap) chunk.ShadowMap = null; continue; }
                edited.Add(adt);

                if (erase)
                {
                    bool any = false;
                    for (int i = 0; i < 4096 && !any; i++) any = map[i] != 0;
                    if (!any) { chunk.ShadowMap = null; rebuild.Add(adt); continue; }
                }

                if (freshMap) rebuild.Add(adt);
                else if (!rebuild.Contains(adt) && _terrain!.TryGetChunkShadow(adt, cy, cx, out int glId))
                {
                    TextureCache.UpdateAlphaRect(glId, map, rx0, ry0, rx1 - rx0 + 1, ry1 - ry0 + 1);
                    _dirtyAlpha.Add(glId);
                }
                else rebuild.Add(adt);
            }
        }

        foreach (var adt in rebuild) { edited.Add(adt); _terrain!.RebuildTile(adt, _texCache); }
        foreach (var adt in edited) MarkEdited(adt);
    }

    //A ray bake is a job, not a call: the viewport render loop resumes it frame by frame so the
    //window keeps drawing (and the sidebar bar keeps moving) while thousands of texels are tested.
    private sealed class BakeJob
    {
        public List<(AdtFile Adt, MapChunk Ch)> Targets = new();
        public List<ShadowOcc> Wmos = new();
        public List<ShadowOcc> M2s = new();
        public ShadowOpt Opt = null!;
        public Stroke Rec = null!;
        public bool[] Touched = [];
        public Vector3 Sun;
        public float MaxDist, Cs, TexStep, BaseJitter;
        public int SubSamples, SubThreshold;
        public int Index;
        //Current slice size, in chunks; 0 until the first slice has been timed.
        public int Slice;
        public string Map = "";
    };

    private BakeJob? _job;

    //Blocking bake for the map menu, which has no viewport frames to drive the incremental one.
    public int BakeShadows(BakeScope scope)
    {
        if (_job != null) { Notify?.Invoke("Bake shadows: a bake is already running in the viewport, wait for its bar."); return 0; }
        if (!PrepareShadowBake(scope)) return 0;
        BakeChunks(-1);
        return FinishShadowBake();
    }

    //Starts a bake that the viewport render loop walks slice by slice, so the window keeps drawing
    //and the shadow sidebar's progress bar moves while the rays run.
    public bool StartShadowBake(BakeScope scope)
    {
        if (_job != null) { Notify?.Invoke("Bake shadows: a bake is already running - wait until its bar is full."); return false; }
        if (!PrepareShadowBake(scope)) return false;
        if (_job is not { } j) return false;
        Notify?.Invoke($"Ray-baking shadows over {j.Targets.Count} chunk(s) frame by frame - watch the bar, Ctrl+Z undoes when the bar is full.");
        return true;
    }

    //Gathers the chunks to bake and the real triangles that can cast on them, then leaves the work
    //on _job. Everything expensive but chunk independent happens here so the rays can be sliced.
    private bool PrepareShadowBake(BakeScope scope)
    {
        if (_terrain == null) return false;
        MakeCurrent();

        float pitch = MathHelper.DegreesToRadians(Math.Clamp(ShadowSunHeight, -80f, 80f));
        float yaw = MathHelper.DegreesToRadians(ShadowSunRotation);
        //WoW Viewer builds (cos(H)·sin(R), sin(H), cos(H)·cos(R)) in WoW's own (X, Y, height) space,
        //so its elevation sine lands on a horizontal axis and "Sun Height 30°" is really a sun about
        //46° above the horizon. Salmiak rays run in GL space, where x = -adtY, z = -adtX and y is the
        //height, which turns the very same pair of angles into the vector below: 30 / 34 reproduces
        //the shadows the 1.12 client has, the way WoW Viewer's own defaults do.
        var sun = new Vector3(-MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw), -MathF.Cos(pitch) * MathF.Sin(yaw));
        float maxDist = Math.Max(10f, ShadowMaxDistance);
        int subSamples = ShadowHighRes ? Math.Clamp(ShadowHighResSamples, 2, 8) : 1;
        int subThreshold = Math.Clamp(ShadowHighResThreshold, 1, 16);
        float baseJitter = ShadowJitter ? Math.Clamp(ShadowJitterValue, 0f, 0.1f) : 0f;
        int alphaThreshold = Math.Clamp(ShadowAlphaThreshold, 0, 255);

        //Chunks first: the occluder gather below only parses tiles that can reach these chunks.
        var targets = CollectBakeTargets(scope);
        if (targets.Count == 0)
        {
            Notify?.Invoke(scope == BakeScope.SelectedChunks
                ? "Bake shadows: no chunks selected - click chunks, press Shift+C for the whole tile, or pick the Edited tiles scope."
                : "Bake shadows: nothing in scope.");
            return false;
        }
        var wantTiles = BakeTilesInReach(targets, maxDist);

        //Real triangle occluders: WMO group geometry (local space + world inverse transform).
        var wmoOcc = new List<ShadowOcc>();
        var m2Occ = new List<ShadowOcc>();
        var wmoTex = new Dictionary<string, (byte[] Rgba, int W, int H)>(StringComparer.OrdinalIgnoreCase);
        //A model's own triangles, texture ranges and per-triangle boxes are the same for every
        //placement of it, and a forest reuses the same tree hundreds of times, so parse them into
        //one template per geometry and stamp a cheap placement copy off it. Both dictionaries key
        //on the cached geometry object, which the renderers hand back unchanged for a repeated path.
        var wmoTpl = new Dictionary<WmoFile.Group, ShadowOcc>();
        var m2Tpl = new Dictionary<Salmiak.Rendering.DoodadRenderer.CpuModel, ShadowOcc>();

        if (ShadowWmos && _wmos != null && _mpq != null)
            foreach (var kv in _tileCache)
            {
                var a = kv.Value;
                if (a == null || !wantTiles.Contains(kv.Key)) continue;
                foreach (var w in a.Wmos)
                {
                    if (string.IsNullOrEmpty(w.ModelPath)) continue;
                    var groups = _wmos.GetCpuGroups(w.ModelPath);
                    if (groups.Count == 0) continue;
                    if (!_wmos.TryGetModelAabb(w.ModelPath, out var wmn, out var wmx)) continue;
                    var wxf = Salmiak.Rendering.WmoRenderer.BuildTransform(w);
                    var winv = wxf.Inverted();
                    var wcr = Vector3.TransformPosition((wmn + wmx) * 0.5f, wxf);
                    float wr = Math.Max(2f, (wmx - wmn).Length * 0.5f);
                    foreach (var grp in groups)
                    {
                        if (grp.Vertices.Length < WmoFile.GroupVertexStride || grp.Indices.Length < 3) continue;
                        if (!wmoTpl.TryGetValue(grp, out var tpl))
                        {
                            var ranges = new List<TriRange>(grp.Batches.Count);
                            foreach (var bt in grp.Batches)
                            {
                                if (bt.IndexCount < 3) continue;
                                var tr = new TriRange { Start = bt.IndexStart, Count = bt.IndexCount, Alpha = bt.AlphaTest, Tex = bt.TexturePath ?? "" };
                                if (tr.Alpha && tr.Tex.Length > 0)
                                {
                                    if (!wmoTex.TryGetValue(tr.Tex, out var dec))
                                    {
                                        dec = (new byte[0], 0, 0);
                                        try { using var ts = _mpq!.OpenFile(tr.Tex.Replace('/', '\\')); dec = BlpReader.Decode(ts); }
                                        catch { }
                                        wmoTex[tr.Tex] = dec;
                                    }
                                    var (decRgba, decW, decH) = dec;
                                    tr.Rgba = decRgba; tr.TW = decW; tr.TH = decH;
                                }
                                ranges.Add(tr);
                            }
                            tpl = wmoTpl[grp] = new ShadowOcc { W = true, V = grp.Vertices, Idx = grp.Indices,
                                                                Stride = WmoFile.GroupVertexStride, Ranges = ranges.ToArray() };
                            tpl.BBox = BuildTriBoxes(tpl.V, tpl.Idx, tpl.Stride);
                        }
                        if (tpl.Ranges.Length == 0) continue;
                        wmoOcc.Add(new ShadowOcc { W = true, WC = wcr, WR = wr, Scale = 1f, Inv = winv,
                                                   V = tpl.V, Idx = tpl.Idx, Stride = tpl.Stride,
                                                   Ranges = tpl.Ranges, BBox = tpl.BBox });
                    }
                }
            }

        if (ShadowDoodads && _doodads != null)
            foreach (var kv in _tileCache)
            {
                var a = kv.Value;
                if (a == null || !wantTiles.Contains(kv.Key)) continue;
                foreach (var d in a.Doodads)
                {
                    if (string.IsNullOrEmpty(d.ModelPath)) continue;
                    var cm = _doodads.GetCpuGeometry(d.ModelPath);
                    if (cm == null || cm.Indices.Length < 3 || cm.Submeshes.Length == 0) continue;
                    if (!_doodads.TryGetModelBounds(d.ModelPath, out var mcen, out var mrad)) continue;
                    var dxf = Salmiak.Rendering.DoodadRenderer.BuildTransform(d);
                    if (!m2Tpl.TryGetValue(cm, out var tpl))
                    {
                        var ranges = new List<TriRange>(cm.Submeshes.Length);
                        foreach (var sm in cm.Submeshes)
                        {
                            if (sm.Count < 3) continue;
                            var tex = sm.Tex ?? "";
                            var tr = new TriRange { Start = sm.Start, Count = sm.Count, Alpha = true, Tex = tex };
                            if (tex.Length > 0 && cm.Textures.TryGetValue(tex, out var ct))
                            {
                                var (ctRgba, ctW, ctH) = ct;
                                tr.Rgba = ctRgba; tr.TW = ctW; tr.TH = ctH;
                            }
                            ranges.Add(tr);
                        }
                        tpl = m2Tpl[cm] = new ShadowOcc { W = false, V = cm.Vertices, Idx = cm.Indices,
                                                          Stride = 8, Ranges = ranges.ToArray() };
                        tpl.BBox = BuildTriBoxes(tpl.V, tpl.Idx, tpl.Stride);
                    }
                    if (tpl.Ranges.Length == 0) continue;
                    var dcen = Vector3.TransformPosition(mcen, dxf);
                    float dr = Math.Max(0.5f, mrad * Math.Abs(d.Scale));
                    m2Occ.Add(new ShadowOcc { W = false, WC = dcen, WR = dr, Inv = dxf.Inverted(),
                                              Scale = Math.Abs(d.Scale) < 1e-4f ? 1f : d.Scale,
                                              V = tpl.V, Idx = tpl.Idx, Stride = tpl.Stride,
                                              Ranges = tpl.Ranges, BBox = tpl.BBox });
                }
            }

        //Textures that block light: alpha coverage on a coarse grid (skip-transparent heuristic).
        var texBlocks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var lst in new[] { wmoOcc, m2Occ })
            foreach (var m in lst)
                foreach (var r in m.Ranges)
                {
                    r.Blocks = true;
                    if (!r.Alpha || r.TW <= 0) continue;
                    if (!texBlocks.TryGetValue(r.Tex, out var ok))
                        texBlocks[r.Tex] = ok = AlphaCoverage(r.Rgba, r.TW, r.TH, alphaThreshold);
                    r.Blocks = ok;
                }

        var rec = new Stroke();
        float cs = AdtFile.ChunkSize;
        float texStep = cs / 63f;
        var opt = new ShadowOpt { AlphaTest = ShadowAlphaTest, AlphaThreshold = alphaThreshold,
                                   SkipTransparent = ShadowSkipTransparent, Terrain = ShadowTerrainBlocks,
                                   Jitter = baseJitter, SubSamples = subSamples, SubThreshold = subThreshold,
                                   Blocks = texBlocks };

        foreach (var (adt, ch) in targets)
        {
            rec.Shadows[ch] = ch.ShadowMap != null ? (byte[])ch.ShadowMap.Clone() : null;
            rec.Tiles.Add(adt);
        }

        var touched = new bool[targets.Count];
        _job = new BakeJob { Targets = targets, Wmos = wmoOcc, M2s = m2Occ, Opt = opt, Rec = rec,
                             Touched = touched, Sun = sun, MaxDist = maxDist, Cs = cs, TexStep = texStep,
                             SubSamples = subSamples, SubThreshold = subThreshold, BaseJitter = baseJitter,
                             Map = _mapName ?? "" };
        ShadowBakeProgressReset?.Invoke();
        return true;
    }

    //Chunks the bake scope covers; an empty selection never widens to the whole map.
    private List<(AdtFile Adt, MapChunk Ch)> CollectBakeTargets(BakeScope scope)
    {
        var seen = new HashSet<MapChunk>();
        var targets = new List<(AdtFile Adt, MapChunk Ch)>();
        void AddTile(AdtFile a)
        {
            if (a == null) return;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
                for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
                {
                    var ch = a.Chunks[cy, cx];
                    if (ch != null && seen.Add(ch)) targets.Add((a, ch));
                }
        }
        if (scope == BakeScope.SelectedChunks)
        {
            foreach (var k in _bakeChunks)
            {
                int tileX = BakeChunkTile(k.Col), tileY = BakeChunkTile(k.Row);
                if (!_tileCache.TryGetValue((tileX, tileY), out var a) || a == null) continue;
                var ch = a.Chunks[BakeChunkLocal(k.Row), BakeChunkLocal(k.Col)];
                if (ch != null && seen.Add(ch)) targets.Add((a, ch));
            }
            //An empty selection stays empty: the caller refuses such a bake instead of widening it
            //to the whole map, because these users asked for chunks they picked.
        }
        else if (scope == BakeScope.EditedTiles) foreach (var a in _editedTiles.Values) if (a != null) AddTile(a);
        else foreach (var kv in _tileCache) if (kv.Value != null) AddTile(kv.Value);
        return targets;
    }

    //Tiles whose geometry can reach those chunks: the tiles the chunks live in, padded by the ray
    //length because a shadow crosses a tile border. Only these get their WMO/M2 geometry parsed,
    //which is what keeps baking a handful of chunks snappy in a streamed map. One tile is 533 yd,
    //so any reach up to that (the sidebar's 250 default included) still pads by exactly one ring.
    private HashSet<(int X, int Y)> BakeTilesInReach(List<(AdtFile Adt, MapChunk Ch)> targets, float maxDist)
    {
        const float tileSize = AdtFile.TileSize;
        int pad = (int)Math.Ceiling(Math.Max(1f, maxDist) / tileSize) + 1;
        var want = new HashSet<(int X, int Y)>();
        foreach (var t in targets)
            foreach (var kv in _tileCache)
            {
                if (kv.Value != t.Adt) continue;
                var (tx, ty) = kv.Key;
                for (int dy = -pad; dy <= pad; dy++)
                    for (int dx = -pad; dx <= pad; dx++)
                        want.Add((tx + dx, ty + dy));
            }
        return want;
    }

    //Runs one slice of the pending job: slice chunks worth of rays (-1 = every remaining chunk),
    //in parallel, and each chunk's grid in parallel row bands when the slice is too short to fill
    //the cores by itself. The viewport render loop calls it a few chunks per frame, so the window
    //keeps drawing between slices and only the chunk work below has to stay untouched.
    private void BakeChunks(int slice)
    {
        if (_job is not { } j) return;
        var targets = j.Targets;
        var touched = j.Touched;
        var wmoOcc = j.Wmos;
        var m2Occ = j.M2s;
        var opt = j.Opt;
        var sun = j.Sun;
        var cs = j.Cs;
        var texStep = j.TexStep;
        var maxDist = j.MaxDist;
        var subSamples = j.SubSamples;
        var subThreshold = j.SubThreshold;
        var baseJitter = j.BaseJitter;
        var from = j.Index;
        var to = slice < 0 ? targets.Count : Math.Min(targets.Count, from + slice);
        j.Index = to;

        //One progress event per slice, fired here rather than from the chunk workers: this runs on
        //the window's render thread, so the queued update reaches the sidebar bar at the frame end.
        if (to > from) ShadowBakeProgress?.Invoke(to, targets.Count);

        System.Threading.Tasks.Parallel.For(from, to, i =>
        {
            var ch = targets[i].Ch;

            var cc = new Vector3(-ch.Position.Y + cs / 2f, ch.Position.Z, -ch.Position.X + cs / 2f);

            //Per-chunk broadphase: instance bounds only, then triangles per texel.
            var localW = new List<ShadowOcc>();
            var localM = new List<ShadowOcc>();
            foreach (var m in wmoOcc)
                if (BakeNear(cc, m, cs, maxDist, sun)) localW.Add(m);
            foreach (var m in m2Occ)
                if (BakeNear(cc, m, cs, maxDist, sun)) localM.Add(m);
            if (localW.Count == 0 && localM.Count == 0) return;

            var old = ch.ShadowMap;
            var map = old != null ? (byte[])old.Clone() : new byte[4096];

            //Aim at ~16 ray tasks whatever the slice holds: a one-chunk pick would otherwise run its
            //4096 rays on a single core, while a whole tile's chunks already fill the pool by
            //themselves. Bands write disjoint row ranges of map, so the grid stays race-free.
            var bands = Math.Clamp(16 / Math.Max(1, to - from), 1, 16);
            System.Threading.Tasks.Parallel.For(0, bands, b =>
            {
                for (int row = b * 64 / bands; row < (b + 1) * 64 / bands; row++)
                for (int col = 0; col < 64; col++)
                {
                    int idx = row * 64 + col;
                    if (ShadowKeepOld && old != null && old[idx] != 0) continue;

                    float gx = -ch.Position.Y + col * texStep;
                    float gz = -ch.Position.X + row * texStep;
                    float gy = HeightAtGL(gx, gz);
                    if (float.IsNaN(gy)) continue;

                    //Water is deliberately not consulted here. The lake bed is lit like any other
                    //ground in the client's own MCSH payloads, so a submerged texel keeps the shadow
                    //its bank casts on it; skipping it leaves a crisp shadow edge at the shoreline,
                    //which is not what the 1.12 tiles have. The heightmap only decides where a ray
                    //starts, and the sole texels left out are those with no height to start at.
                    var origin = new Vector3(gx, gy + 0.3f, gz);
                    bool shadowed = false;
                    if (!(opt.Terrain && TerrainBlocksRay(origin, sun, 32f)))
                    {
                        float dist = float.MaxValue;
                        //PASS 1: WMO only, at the texel centre.
                        shadowed = SunRayOccluded(opt, origin, sun, maxDist, localW, localM, true, false, out dist);
                        if (!shadowed && subSamples > 1)
                        {
                            //PASS 2: sub-texel samples with alpha-tested foliage.
                            int totalHits = 0;
                            float nearest = float.MaxValue;
                            float cell = texStep / subSamples;
                            for (int sy = 0; sy < subSamples; sy++)
                            for (int sx = 0; sx < subSamples; sx++)
                            {
                                float px = gx + (sx + 0.5f) * cell;
                                float pz = gz + (sy + 0.5f) * cell;
                                float py = HeightAtGL(px, pz);
                                if (float.IsNaN(py)) continue;
                                var sub = new Vector3(px, py + 0.3f, pz);
                                var dir = sun;
                                if (baseJitter > 0f) dir = JitterBake(dir, (sy * subSamples) + sx, nearest, baseJitter);
                                float hd;
                                if (SunRayOccluded(opt, sub, dir, maxDist, localW, localM, false, true, out hd))
                                {
                                    totalHits++;
                                    if (hd > 0f && hd < nearest) nearest = hd;
                                }
                            }
                            if (totalHits > subThreshold) shadowed = true;
                        }
                        else if (!shadowed) shadowed = SunRayOccluded(opt, origin, sun, maxDist, localW, localM, false, true, out dist);
                    }

                    byte val = shadowed ? (byte)255 : (byte)0;
                    if (old == null ? val == 0 : old[idx] == val) continue;
                    map[idx] = val;
                }
            });

            //What moved comes from diffing the grid once instead of a flag inside the ray loop,
            //because the bands write it at the same time. 4096 bytes is nothing next to the rays.
            bool changedAny = false;
            bool any = false;
            for (int k = 0; k < 4096; k++)
            {
                if (map[k] != 0) any = true;
                if (old == null ? map[k] != 0 : map[k] != old[k]) changedAny = true;
            }

            if (!changedAny) return;
            ch.ShadowMap = any ? map : null;
            touched[i] = true;
        });
    }

    //Closes the job: pushes one undo stroke over everything the rays touched and rebuilds the tiles
    //whose texels actually changed, so Ctrl+Z can roll a whole multi-frame bake back at once.
    private int FinishShadowBake()
    {
        if (_job is not { } j) return 0;
        var targets = j.Targets;

        //Finish the progress bar: the last event always lands after the workers queued their own.
        ShadowBakeProgress?.Invoke(targets.Count, targets.Count);

        int baked = 0;
        var dirty = new HashSet<AdtFile>();
        for (int i = 0; i < targets.Count; i++)
            if (j.Touched[i]) { baked++; dirty.Add(targets[i].Adt); }

        //The CPU copies only exist for the rays, so drop them: a streamed map never has to keep
        //several megabytes of duplicate geometry around once the bake is over.
        _wmos?.ClearCpuGeometry();
        _doodads?.ClearCpuGeometry();

        if (baked == 0) { _job = null; Notify?.Invoke("Bake shadows: no occluders found in scope, nothing changed."); return 0; }

        PushUndo(j.Rec);
        foreach (var a in dirty) { MarkEdited(a); _terrain?.RebuildTile(a, _texCache); }
        Notify?.Invoke($"Ray-baked {baked} of {targets.Count} chunk(s) ({j.Wmos.Count} WMO + {j.M2s.Count} M2 occluders, " +
                       $"sun height {ShadowSunHeight}°, rotation {ShadowSunRotation}°, max {ShadowMaxDistance} yd). Ctrl+Z undoes.");
        _job = null;
        return baked;
    }

    //One frame's worth of the pending bake. The slice is sized from what the last one cost rather
    //than a fixed step count: a 100-chunk selection would otherwise spend most of its wall clock
    //in a hundred redraws instead of in rays, while a whole map would freeze the window on one
    //enormous slice. Sizing by milliseconds keeps the bar moving and the redraws cheap.
    private void StepBakeFrame()
    {
        if (_job is not { } j) return;
        //A bake is tied to the map its chunks came from, and a loaded map invalidates them.
        if (j.Map != _mapName)
        {
            _job = null;
            Notify?.Invoke("Bake shadows: the map changed while rays were running, that bake was dropped.");
            return;
        }
        int remaining = j.Targets.Count - j.Index;
        if (remaining <= 0) { FinishShadowBake(); return; }
        //First slice still guesses from the chunk count; afterwards it comes from measured cost.
        int slice = j.Slice > 0 ? j.Slice : Math.Max(1, remaining / 40);
        slice = Math.Min(slice, remaining);
        long t0 = _sw.ElapsedTicks;
        BakeChunks(slice);
        double spent = Math.Max(1d, (_sw.ElapsedTicks - t0) * 1000d / Stopwatch.Frequency);
        //Scale the next slice to about 12 ms of rays, which keeps a frame's worth of bake near the
        //frame budget whatever the chunks happen to cost in this part of the map.
        j.Slice = Math.Clamp((int)(slice * 12d / spent + 0.5d), 1, 1024);
        if (j.Index >= j.Targets.Count) FinishShadowBake();
    }

    private sealed class ShadowOpt
    {
        public bool AlphaTest, SkipTransparent, Terrain;
        public int AlphaThreshold, SubSamples, SubThreshold;
        public float Jitter;
        public Dictionary<string, bool> Blocks = new();
    }

    private sealed class ShadowOcc
    {
        public bool W;
        public Vector3 WC;
        public float WR, Scale;
        public Matrix4 Inv;
        public float[] V = [];
        public uint[] Idx = [];
        public int Stride;
        public TriRange[] Ranges = [];
        public float[] BBox = [];
    }

    private sealed class TriRange
    {
        public int Start, Count;
        public bool Alpha, Blocks;
        public string Tex = "";
        public byte[]? Rgba;
        public int TW, TH;
    }

    private static float[] BuildTriBoxes(float[] v, uint[] idx, int stride)
    {
        int n = idx.Length / 3;
        var bb = new float[n * 6];
        for (int t = 0; t < n; t++)
        {
            int ii = t * 3, b = t * 6;
            int o0 = (int)idx[ii] * stride, o1 = (int)idx[ii + 1] * stride, o2 = (int)idx[ii + 2] * stride;
            if (o2 + 2 >= v.Length)
            {
                bb[b] = bb[b + 1] = bb[b + 2] = float.MinValue;
                bb[b + 3] = bb[b + 4] = bb[b + 5] = float.MaxValue;
                continue;
            }
            float minX = v[o0], minY = v[o0 + 1], minZ = v[o0 + 2];
            float maxX = minX, maxY = minY, maxZ = minZ;
            foreach (int o in new[] { o1, o2 })
            {
                if (v[o] < minX) minX = v[o]; else if (v[o] > maxX) maxX = v[o];
                if (v[o + 1] < minY) minY = v[o + 1]; else if (v[o + 1] > maxY) maxY = v[o + 1];
                if (v[o + 2] < minZ) minZ = v[o + 2]; else if (v[o + 2] > maxZ) maxZ = v[o + 2];
            }
            bb[b] = minX; bb[b + 1] = minY; bb[b + 2] = minZ;
            bb[b + 3] = maxX; bb[b + 4] = maxY; bb[b + 5] = maxZ;
        }
        return bb;
    }

    private static bool BakeNear(Vector3 cc, ShadowOcc m, float cs, float maxDist, Vector3 sun)
    {
        float dx = m.WC.X - cc.X, dy = m.WC.Y - cc.Y, dz = m.WC.Z - cc.Z;
        float r = maxDist + cs + m.WR;
        if (dx * dx + dy * dy + dz * dz > r * r) return false;
        return dx * sun.X + dy * sun.Y + dz * sun.Z >= -m.WR - cs;
    }

    private static Vector3 JitterBake(Vector3 dir, int seed, float dist, float baseJitter)
    {
        float amt = dist > 0f && dist < float.MaxValue ? baseJitter * dist : 0.02f;
        if (amt <= 0f) return dir;
        var j = dir + new Vector3(MathF.Sin(seed * 1.234f), MathF.Cos(seed * 5.678f), MathF.Sin(seed * 9.012f)) * amt;
        return j.X * j.X + j.Y * j.Y + j.Z * j.Z < 1e-9f ? dir : Vector3.Normalize(j);
    }

    private bool TerrainBlocksRay(Vector3 origin, Vector3 dir, float maxT)
    {
        for (float t = 1.5f; t < maxT; t += 1f + t * 0.06f)
        {
            float px = origin.X + dir.X * t, py = origin.Y + dir.Y * t, pz = origin.Z + dir.Z * t;
            float h = HeightAtGL(px, pz);
            if (!float.IsNaN(h) && h > py + 0.5f) return true;
        }
        return false;
    }

    private static bool AlphaCoverage(byte[]? rgba, int w, int h, int threshold)
    {
        if (rgba == null || w <= 0 || h <= 0) return true;
        int stepX = Math.Max(1, w / 16), stepY = Math.Max(1, h / 16);
        int samples = 0, solid = 0;
        for (int y = 0; y < h; y += stepY)
            for (int x = 0; x < w; x += stepX)
            {
                int ai = (y * w + x) * 4 + 3;
                if (ai >= rgba.Length) continue;
                samples++;
                if (rgba[ai] >= threshold) solid++;
            }
        if (samples == 0) return true;
        return solid * 100 >= samples * 3;
    }

    private bool SunRayOccluded(ShadowOpt opt, Vector3 origin, Vector3 dir, float maxDist,
                                 List<ShadowOcc> w, List<ShadowOcc> m, bool wmoOnly, bool alphaTest, out float dist)
    {
        dist = -1f;
        //The alpha-test checkbox gates per-texel alpha sampling; with it off, alpha-tested
        //geometry (leaves, fences) is treated as fully solid.
        bool sample = alphaTest && opt.AlphaTest;
        foreach (var occ in w)
            if (RayOccludes(opt, occ, origin, dir, maxDist, sample, out dist)) return true;
        if (!wmoOnly)
            foreach (var occ in m)
                if (RayOccludes(opt, occ, origin, dir, maxDist, sample, out dist)) return true;
        return false;
    }

    private static bool RayOccludes(ShadowOpt opt, ShadowOcc occ, Vector3 origin, Vector3 dir, float maxDist,
                                     bool alphaTest, out float dist)
    {
        dist = -1f;
        float wx = occ.WC.X - origin.X, wy = occ.WC.Y - origin.Y, wz = occ.WC.Z - origin.Z;
        float along = wx * dir.X + wy * dir.Y + wz * dir.Z;
        float tClosest = along < 0f ? 0f : along > maxDist ? maxDist : along;
        float cx = wx - dir.X * tClosest, cy = wy - dir.Y * tClosest, cz = wz - dir.Z * tClosest;
        if (cx * cx + cy * cy + cz * cz > occ.WR * occ.WR) return false;

        var lo = Vector3.TransformPosition(origin, occ.Inv);
        var ld = Vector3.TransformVector(dir, occ.Inv);
        float lox = lo.X, loy = lo.Y, loz = lo.Z;
        float ldx = ld.X, ldy = ld.Y, ldz = ld.Z;
        float scale = occ.Scale <= 0f ? 1f : occ.Scale;
        float maxLocal = maxDist / scale;

        var v = occ.V;
        var idx = occ.Idx;
        var bb = occ.BBox;
        int stride = occ.Stride, nTri = idx.Length / 3;
        foreach (var rg in occ.Ranges)
        {
            if (opt.SkipTransparent && !rg.Blocks) continue;
            bool sampleAlpha = rg.Alpha && alphaTest;
            byte[] rgba = rg.Rgba ?? new byte[0];
            int tw = rg.TW, th = rg.TH;
            if (sampleAlpha && (rgba.Length < 4 || tw <= 0 || th <= 0)) sampleAlpha = false;

            int last = Math.Min(rg.Start + rg.Count, idx.Length);
            for (int ii = rg.Start; ii + 2 < last; ii += 3)
            {
                int tri = ii / 3;
                if (bb != null && tri < nTri)
                {
                    int b = tri * 6;
                    float t0 = 0f, t1 = maxLocal;
                    bool miss = false;
                    for (int ax = 0; ax < 3 && !miss; ax++)
                    {
                        float od = ax == 0 ? lox : ax == 1 ? loy : loz;
                        float dd = ax == 0 ? ldx : ax == 1 ? ldy : ldz;
                        float blo = bb[b + ax], bhi = bb[b + 3 + ax];
                        if (MathF.Abs(dd) < 1e-6f) { if (od < blo || od > bhi) miss = true; continue; }
                        float ta = (blo - od) / dd, tb = (bhi - od) / dd;
                        if (ta > tb) (ta, tb) = (tb, ta);
                        if (ta > t0) t0 = ta;
                        if (tb < t1) t1 = tb;
                        if (t0 > t1) miss = true;
                    }
                    if (miss) continue;
                }

                uint i0 = idx[ii], i1 = idx[ii + 1], i2 = idx[ii + 2];
                int o0 = (int)i0 * stride, o1 = (int)i1 * stride, o2 = (int)i2 * stride;
                if (o2 + 7 >= v.Length) continue;

                float axx = v[o0], ayy = v[o0 + 1], azz = v[o0 + 2];
                float e1x = v[o1] - axx, e1y = v[o1 + 1] - ayy, e1z = v[o1 + 2] - azz;
                float e2x = v[o2] - axx, e2y = v[o2 + 1] - ayy, e2z = v[o2 + 2] - azz;
                float px = ldy * e2z - ldz * e2y, py = ldz * e2x - ldx * e2z, pz = ldx * e2y - ldy * e2x;
                float det = e1x * px + e1y * py + e1z * pz;
                if (det > -1e-7f && det < 1e-7f) continue;
                float invDet = 1f / det;

                float sx = lox - axx, sy = loy - ayy, sz = loz - azz;
                float u = (sx * px + sy * py + sz * pz) * invDet;
                if (u < 0f || u > 1f) continue;
                float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                float vv = (ldx * qx + ldy * qy + ldz * qz) * invDet;
                if (vv < 0f || u + vv > 1f) continue;
                float t = (e2x * qx + e2y * qy + e2z * qz) * invDet;
                if (t < 0.05f || t > maxLocal) continue;

                if (sampleAlpha)
                {
                    float u0 = v[o0 + 6], u1 = v[o1 + 6], u2 = v[o2 + 6];
                    float w0 = v[o0 + 7], w1 = v[o1 + 7], w2 = v[o2 + 7];
                    if (u0 != 0f || u1 != 0f || u2 != 0f || w0 != 0f || w1 != 0f || w2 != 0f)
                    {
                        float bw = 1f - u - vv;
                        float hu = bw * u0 + u * u1 + vv * u2;
                        float hv = bw * w0 + u * w1 + vv * w2;
                        int tx = (int)((hu - MathF.Floor(hu)) * tw);
                        int ty = (int)((hv - MathF.Floor(hv)) * th);
                        if (tx < 0) tx = 0; else if (tx >= tw) tx = tw - 1;
                        if (ty < 0) ty = 0; else if (ty >= th) ty = th - 1;
                        int ai = (ty * tw + tx) * 4 + 3;
                        if (ai >= rgba.Length || rgba[ai] < opt.AlphaThreshold) continue;
                    }
                }

                dist = t * scale;
                return true;
            }
        }
        return false;
    }

    private static bool BrushHitsChunk(Salmiak.Core.Formats.MapChunk chunk, Vector3 hit, float r)
    {
        const float cs = AdtFile.ChunkSize;
        float minX = -chunk.Position.Y, maxX = minX + cs;
        float minZ = -chunk.Position.X, maxZ = minZ + cs;
        float nx = Math.Clamp(hit.X, minX, maxX);
        float nz = Math.Clamp(hit.Z, minZ, maxZ);
        float dx = hit.X - nx, dz = hit.Z - nz;
        return dx * dx + dz * dz <= r * r;
    }

    private void ApplyBrush(Vector3 hit, float dt, bool lower)
    {
        const float step = AdtFile.ChunkSize / 8f;
        float r = BrushRadius;
        float amount = BrushStrength * dt * (lower ? -1f : 1f);
        var affected = new HashSet<AdtFile>();

        if (DoodadFollow) CaptureFollowClearance(hit, r);

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
                float ccx = -(chunk.Position.Y - 4 * step), ccz = -(chunk.Position.X - 4 * step);
                if (MathF.Abs(ccx - hit.X) > r + 30f || MathF.Abs(ccz - hit.Z) > r + 30f) continue;

                if (_stroke != null && !_stroke.Heights.ContainsKey(chunk))
                    _stroke.Heights[chunk] = (float[])chunk.Heights.Clone();

                bool touched = false;
                int vi = 0;
                for (int row = 0; row < 9; row++)
                {
                    for (int col = 0; col < 9; col++) { if (Adjust(chunk, col, row, vi, amount, r, hit, step, BrushFalloff)) touched = true; vi++; }
                    if (row < 8)
                        for (int col = 0; col < 8; col++) { if (Adjust(chunk, col + .5f, row + .5f, vi, amount, r, hit, step, BrushFalloff)) touched = true; vi++; }
                }
                if (touched) affected.Add(adt);
            }
        }

        if (_stroke != null) foreach (var adt in affected) _stroke.Tiles.Add(adt);
        foreach (var adt in affected) { MarkEdited(adt); _terrain!.UpdateTileHeights(adt); }

        if (DoodadFollow) FollowObjects(hit, r);
    }

    private readonly Dictionary<(AdtFile, int), float> _followClearD = new();

    private readonly Dictionary<(AdtFile, int), float> _followClearW = new();

    private void CaptureFollowClearance(Vector3 hit, float radius)
    {
        const float mapHalf = 32f * AdtFile.TileSize;
        float r2 = radius * radius;
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            for (int i = 0; i < adt.Doodads.Count; i++)
            {
                var key = (adt, i);
                if (_followClearD.ContainsKey(key)) continue;
                var p = adt.Doodads[i].Position;
                float gx = p.X - mapHalf, gz = p.Z - mapHalf, dx = gx - hit.X, dz = gz - hit.Z;
                if (dx * dx + dz * dz >= r2) continue;
                float g = HeightAtGL(gx, gz);
                if (float.IsNaN(g)) continue;
                _followClearD[key] = p.Y - g;
                if (_stroke != null && !_stroke.DoodadPos.ContainsKey(key)) _stroke.DoodadPos[key] = p;
            }
            for (int i = 0; i < adt.Wmos.Count; i++)
            {
                var key = (adt, i);
                if (_followClearW.ContainsKey(key)) continue;
                var p = adt.Wmos[i].Position;
                float gx = p.X - mapHalf, gz = p.Z - mapHalf, dx = gx - hit.X, dz = gz - hit.Z;
                if (dx * dx + dz * dz >= r2) continue;
                float g = HeightAtGL(gx, gz);
                if (float.IsNaN(g)) continue;
                _followClearW[key] = p.Y - g;
                if (_stroke != null && !_stroke.WmoPos.ContainsKey(key)) _stroke.WmoPos[key] = p;
            }
        }
    }

    private void FollowObjects(Vector3 hit, float radius)
    {
        const float mapHalf = 32f * AdtFile.TileSize;
        float r2 = radius * radius;
        bool needFull = false;
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            for (int i = 0; i < adt.Doodads.Count; i++)
            {
                if (!_followClearD.TryGetValue((adt, i), out float clr)) continue;
                var d = adt.Doodads[i];
                float gx = d.Position.X - mapHalf, gz = d.Position.Z - mapHalf, dx = gx - hit.X, dz = gz - hit.Z;
                if (dx * dx + dz * dz >= r2) continue;
                float g = HeightAtGL(gx, gz);
                if (float.IsNaN(g)) continue;
                d.Position.Y = g + clr;
                adt.Doodads[i] = d;
                MarkEdited(adt);
                if (_doodads == null || !_doodads.UpdateInstance(adt, i, DoodadRenderer.BuildTransform(d))) needFull = true;
            }
            for (int i = 0; i < adt.Wmos.Count; i++)
            {
                if (!_followClearW.TryGetValue((adt, i), out float clr)) continue;
                var d = adt.Wmos[i];
                float gx = d.Position.X - mapHalf, gz = d.Position.Z - mapHalf, dx = gx - hit.X, dz = gz - hit.Z;
                if (dx * dx + dz * dz >= r2) continue;
                float g = HeightAtGL(gx, gz);
                if (float.IsNaN(g)) continue;
                d.Position.Y = g + clr;
                adt.Wmos[i] = d;
                MarkEdited(adt);
                if (_wmos == null || !_wmos.UpdateInstance(adt, i, WmoRenderer.BuildTransform(d))) needFull = true;
            }
        }
        if (needFull) RefreshStreaming();
    }

    private void ApplyFlatten(Vector3 hit, float dt)
    {
        if (float.IsNaN(_flattenTarget))
        {
            float t = HeightAtGL(hit.X, hit.Z);
            if (float.IsNaN(t)) return;
            _flattenTarget = t;
        }

        const float step = AdtFile.ChunkSize / 8f;
        float r = BrushRadius;
        float rate = Math.Clamp(BrushStrength * dt * 0.05f, 0f, 1f);
        var affected = new HashSet<AdtFile>();

        if (DoodadFollow) CaptureFollowClearance(hit, r);

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
                float ccx = -(chunk.Position.Y - 4 * step), ccz = -(chunk.Position.X - 4 * step);
                if (MathF.Abs(ccx - hit.X) > r + 30f || MathF.Abs(ccz - hit.Z) > r + 30f) continue;

                if (_stroke != null && !_stroke.Heights.ContainsKey(chunk))
                    _stroke.Heights[chunk] = (float[])chunk.Heights.Clone();

                bool touched = false;
                int vi = 0;
                for (int row = 0; row < 9; row++)
                {
                    for (int col = 0; col < 9; col++) { if (FlattenVert(chunk, col, row, vi, rate, r, hit, step)) touched = true; vi++; }
                    if (row < 8)
                        for (int col = 0; col < 8; col++) { if (FlattenVert(chunk, col + .5f, row + .5f, vi, rate, r, hit, step)) touched = true; vi++; }
                }
                if (touched) affected.Add(adt);
            }
        }

        if (_stroke != null) foreach (var adt in affected) _stroke.Tiles.Add(adt);
        foreach (var adt in affected) { MarkEdited(adt); _terrain!.UpdateTileHeights(adt); }

        if (DoodadFollow) FollowObjects(hit, r);
    }

    private bool FlattenVert(Salmiak.Core.Formats.MapChunk ch, float col, float row, int vi,
                             float rate, float radius, Vector3 hit, float step)
    {
        float gx = -(ch.Position.Y - col * step);
        float gz = -(ch.Position.X - row * step);
        float dx = gx - hit.X, dz = gz - hit.Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d >= radius) return false;
        float f = Falloff(d, radius, BrushFalloff);
        float absH = ch.Position.Z + ch.Heights[vi];
        float nh = absH + (_flattenTarget - absH) * (rate * f);
        ch.Heights[vi] = nh - ch.Position.Z;
        return true;
    }

    private void ApplySmooth(Vector3 hit, float dt)
    {
        const float step = AdtFile.ChunkSize / 8f;
        float r = BrushRadius;
        float rate = Math.Clamp(BrushStrength * dt * 0.06f, 0f, 1f);

        double sum = 0, wsum = 0;
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value; if (adt == null) continue;
            if (!TileOverlapsBrush(kv.Key, hit, r)) continue;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null || !ChunkNearBrush(chunk, hit, r, step)) continue;
                ForEachVertex(chunk, step, (gx, gz, vi) =>
                {
                    float d = Dist(gx, gz, hit);
                    if (d >= r) return;
                    float w = Falloff(d, r, BrushFalloff);
                    sum += w * (chunk.Position.Z + chunk.Heights[vi]);
                    wsum += w;
                });
            }
        }
        if (wsum <= 0) return;
        float avg = (float)(sum / wsum);

        if (DoodadFollow) CaptureFollowClearance(hit, r);

        var affected = new HashSet<AdtFile>();
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value; if (adt == null) continue;
            if (!TileOverlapsBrush(kv.Key, hit, r)) continue;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null || !ChunkNearBrush(chunk, hit, r, step)) continue;
                if (_stroke != null && !_stroke.Heights.ContainsKey(chunk))
                    _stroke.Heights[chunk] = (float[])chunk.Heights.Clone();
                bool touched = false;
                ForEachVertex(chunk, step, (gx, gz, vi) =>
                {
                    float d = Dist(gx, gz, hit);
                    if (d >= r) return;
                    float f = Falloff(d, r, BrushFalloff);
                    float absH = chunk.Position.Z + chunk.Heights[vi];
                    chunk.Heights[vi] = (absH + (avg - absH) * rate * f) - chunk.Position.Z;
                    touched = true;
                });
                if (touched) affected.Add(adt);
            }
        }

        if (_stroke != null) foreach (var adt in affected) _stroke.Tiles.Add(adt);
        foreach (var adt in affected) { MarkEdited(adt); _terrain!.UpdateTileHeights(adt); }
        if (DoodadFollow) FollowObjects(hit, r);
    }

    private void ApplyNoise(Vector3 hit, float dt)
    {
        const float step = AdtFile.ChunkSize / 8f;
        float r = BrushRadius;
        float amp = BrushStrength * dt * 0.6f;
        var affected = new HashSet<AdtFile>();

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value; if (adt == null) continue;
            if (!TileOverlapsBrush(kv.Key, hit, r)) continue;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null || !ChunkNearBrush(chunk, hit, r, step)) continue;
                if (_stroke != null && !_stroke.Heights.ContainsKey(chunk))
                    _stroke.Heights[chunk] = (float[])chunk.Heights.Clone();
                bool touched = false;
                ForEachVertex(chunk, step, (gx, gz, vi) =>
                {
                    float d = Dist(gx, gz, hit);
                    if (d >= r) return;
                    float f = Falloff(d, r, BrushFalloff);
                    chunk.Heights[vi] += NoiseAt(gx, gz) * amp * f;
                    touched = true;
                });
                if (touched) affected.Add(adt);
            }
        }

        if (_stroke != null) foreach (var adt in affected) _stroke.Tiles.Add(adt);
        foreach (var adt in affected) { MarkEdited(adt); _terrain!.UpdateTileHeights(adt); }
    }

    private float NoiseAt(float gx, float gz)
    {
        float cell = MathF.Max(NoiseGrain, 1f);
        float fx = gx / cell, fz = gz / cell;
        int ix = (int)MathF.Floor(fx), iz = (int)MathF.Floor(fz);
        float tx = fx - ix, tz = fz - iz;
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        float v00 = LatticeHash(ix, iz),     v10 = LatticeHash(ix + 1, iz);
        float v01 = LatticeHash(ix, iz + 1), v11 = LatticeHash(ix + 1, iz + 1);
        float a = v00 + (v10 - v00) * tx;
        float b = v01 + (v11 - v01) * tx;
        return a + (b - a) * tz;
    }

    private float LatticeHash(int x, int z)
    {
        uint h = unchecked((uint)x * 73856093u ^ (uint)z * 19349663u ^ _noiseSeed * 83492791u);
        h ^= h >> 13; h *= 0x85ebca6bu; h ^= h >> 16;
        return h / (float)uint.MaxValue * 2f - 1f;
    }

    private static float Dist(float gx, float gz, Vector3 hit)
    { float dx = gx - hit.X, dz = gz - hit.Z; return MathF.Sqrt(dx * dx + dz * dz); }

    private static bool TileOverlapsBrush((int tx, int ty) key, Vector3 hit, float r)
    {
        float oX = -(32 - key.tx) * AdtFile.TileSize, oZ = -(32 - key.ty) * AdtFile.TileSize;
        return !(hit.X + r < oX || hit.X - r > oX + AdtFile.TileSize ||
                 hit.Z + r < oZ || hit.Z - r > oZ + AdtFile.TileSize);
    }

    private static bool ChunkNearBrush(Salmiak.Core.Formats.MapChunk chunk, Vector3 hit, float r, float step)
    {
        float ccx = -(chunk.Position.Y - 4 * step), ccz = -(chunk.Position.X - 4 * step);
        return MathF.Abs(ccx - hit.X) <= r + 30f && MathF.Abs(ccz - hit.Z) <= r + 30f;
    }

    private static void ForEachVertex(Salmiak.Core.Formats.MapChunk ch, float step, Action<float, float, int> visit)
    {
        int vi = 0;
        for (int row = 0; row < 9; row++)
        {
            for (int col = 0; col < 9; col++)
            { visit(-(ch.Position.Y - col * step), -(ch.Position.X - row * step), vi); vi++; }
            if (row < 8)
                for (int col = 0; col < 8; col++)
                { visit(-(ch.Position.Y - (col + .5f) * step), -(ch.Position.X - (row + .5f) * step), vi); vi++; }
        }
    }

    public bool LoadedHeightRange(out float min, out float max)
    {
        min = float.MaxValue; max = float.MinValue; bool any = false;
        foreach (var kv in _tileCache)
        {
            var adt = kv.Value; if (adt == null) continue;
            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx]; if (chunk == null) continue;
                float bz = chunk.Position.Z;
                foreach (float hgt in chunk.Heights)
                { float a = bz + hgt; if (a < min) min = a; if (a > max) max = a; any = true; }
            }
        }
        if (!any) { min = 0f; max = 100f; }
        return any;
    }

    private float[]? _hmLum;

    private int _hmW, _hmH;

    private float _hmMinH, _hmMaxH;

    private bool _hmFlipV;

    private bool _hmPlacing;

    private Vector3? _hmCornerA;

    private readonly float[] _rectOutline = new float[4 * 64 * 3];

    public bool HeightmapPlacing => _hmPlacing;

    public bool BeginHeightmapPlacement(float[] lum, int iw, int ih, float minH, float maxH, bool flipV)
    {
        if (_terrain == null || _wdt == null) { Notify?.Invoke("Heightmap: load a terrain map first."); return false; }
        if (lum == null || iw <= 0 || ih <= 0 || lum.Length < iw * ih) { Notify?.Invoke("Heightmap: invalid image."); return false; }
        _hmLum = lum; _hmW = iw; _hmH = ih; _hmMinH = minH; _hmMaxH = maxH; _hmFlipV = flipV;
        _hmPlacing = true; _hmCornerA = null;
        Notify?.Invoke("Heightmap: click the first corner of the region (Esc to cancel).");
        ModeChanged?.Invoke(ModeLabel());
        return true;
    }

    private void CancelHeightmapPlacement(bool notify = true)
    {
        _hmPlacing = false; _hmCornerA = null; _hmLum = null;
        if (notify) Notify?.Invoke("Heightmap placement cancelled.");
        ModeChanged?.Invoke(ModeLabel());
    }

    private void HeightmapClick(int mx, int my)
    {
        if (!PickTerrain(mx, my, out var hit)) return;
        if (_hmCornerA == null)
        {
            _hmCornerA = hit;
            Notify?.Invoke("Heightmap: click the opposite corner (Esc to cancel).");
        }
        else
        {
            if (ApplyHeightmapRect(_hmCornerA.Value, hit)) CancelHeightmapPlacement(false);
            else _hmCornerA = null;
        }
    }

    private bool ApplyHeightmapRect(Vector3 a, Vector3 b)
    {
        if (_hmLum == null || _terrain == null) return false;
        float minX = MathF.Min(a.X, b.X), maxX = MathF.Max(a.X, b.X);
        float minZ = MathF.Min(a.Z, b.Z), maxZ = MathF.Max(a.Z, b.Z);
        float spanX = maxX - minX, spanZ = maxZ - minZ;
        if (spanX < 1f || spanZ < 1f) { Notify?.Invoke("Heightmap: region too small - click the first corner again."); return false; }

        const float step = AdtFile.ChunkSize / 8f;
        const float ts = AdtFile.TileSize;
        var rec = new Stroke();
        int verts = 0;

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value; if (adt == null) continue;
            var (tx, ty) = kv.Key;
            float tMinX = (tx - 32) * ts, tMinZ = (ty - 32) * ts;
            if (tMinX + ts < minX || tMinX > maxX || tMinZ + ts < minZ || tMinZ > maxZ) continue;

            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx]; if (chunk == null) continue;
                float[]? snapshot = null;
                float bz = chunk.Position.Z;
                ForEachVertex(chunk, step, (gx, gz, vi) =>
                {
                    if (gx < minX || gx > maxX || gz < minZ || gz > maxZ) return;
                    snapshot ??= (float[])chunk.Heights.Clone();
                    float u = (gx - minX) / spanX;
                    float v = (gz - minZ) / spanZ;
                    float s = SampleBilinear(_hmLum, _hmW, _hmH, u, _hmFlipV ? 1f - v : v);
                    chunk.Heights[vi] = (_hmMinH + s * (_hmMaxH - _hmMinH)) - bz;
                    verts++;
                });
                if (snapshot != null) { rec.Heights[chunk] = snapshot; rec.Tiles.Add(adt); }
            }
        }

        if (verts == 0) { Notify?.Invoke("Heightmap: rectangle covered no loaded terrain - click the first corner again."); return false; }
        PushUndo(rec);
        foreach (var adt in rec.Tiles) { MarkEdited(adt); _terrain.UpdateTileHeights(adt); }
        Notify?.Invoke($"Heightmap applied: {verts} vertices across {rec.Tiles.Count} tile(s)  [{_hmMinH:F0}–{_hmMaxH:F0} yd].");
        return true;
    }

    private int BuildRectOutline(Vector3 a, Vector3 b)
    {
        float minX = MathF.Min(a.X, b.X), maxX = MathF.Max(a.X, b.X);
        float minZ = MathF.Min(a.Z, b.Z), maxZ = MathF.Max(a.Z, b.Z);
        float spanX = maxX - minX, spanZ = maxZ - minZ;
        const int per = 48;
        int n = 0;
        void Put(float x, float z)
        {
            if ((n + 1) * 3 > _rectOutline.Length) return;
            float y = HeightAtGL(x, z); if (float.IsNaN(y)) y = a.Y;
            _rectOutline[n * 3] = x; _rectOutline[n * 3 + 1] = y + 0.5f; _rectOutline[n * 3 + 2] = z; n++;
        }
        for (int i = 0; i < per; i++) Put(minX + spanX * i / per, minZ);
        for (int i = 0; i < per; i++) Put(maxX, minZ + spanZ * i / per);
        for (int i = 0; i < per; i++) Put(maxX - spanX * i / per, maxZ);
        for (int i = 0; i < per; i++) Put(minX, maxZ - spanZ * i / per);
        return n;
    }

    private static float SampleBilinear(float[] lum, int iw, int ih, float u, float v)
    {
        u = Math.Clamp(u, 0f, 1f); v = Math.Clamp(v, 0f, 1f);
        float fx = u * (iw - 1), fy = v * (ih - 1);
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, iw - 1), y1 = Math.Min(y0 + 1, ih - 1);
        float tx = fx - x0, ty = fy - y0;
        float top = lum[y0 * iw + x0] + (lum[y0 * iw + x1] - lum[y0 * iw + x0]) * tx;
        float bot = lum[y1 * iw + x0] + (lum[y1 * iw + x1] - lum[y1 * iw + x0]) * tx;
        return top + (bot - top) * ty;
    }

    private void DeleteTerrainAt(Vector3 hit, bool restore)
    {
        const float step = AdtFile.ChunkSize / 8f;
        float r = BrushRadius;
        var affected = new HashSet<AdtFile>();

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
                float ccx = -(chunk.Position.Y - 4 * step), ccz = -(chunk.Position.X - 4 * step);
                if (MathF.Abs(ccx - hit.X) > r + 30f || MathF.Abs(ccz - hit.Z) > r + 30f) continue;

                ushort before = chunk.Holes, mask = before;
                for (int hRow = 0; hRow < 4; hRow++)
                for (int hCol = 0; hCol < 4; hCol++)
                {
                    float gx = -(chunk.Position.Y - (hCol * 2 + 1) * step);
                    float gz = -(chunk.Position.X - (hRow * 2 + 1) * step);
                    float dx = gx - hit.X, dz = gz - hit.Z;
                    if (dx * dx + dz * dz > r * r) continue;
                    int bit = 1 << (hRow * 4 + hCol);
                    mask = (ushort)(restore ? (mask & ~bit) : (mask | bit));
                }
                if (mask == before) continue;
                if (_stroke != null && !_stroke.Holes.ContainsKey(chunk)) _stroke.Holes[chunk] = before;
                chunk.Holes = mask;
                affected.Add(adt);
            }
        }

        if (_stroke != null) foreach (var adt in affected) _stroke.Tiles.Add(adt);
        foreach (var adt in affected) { MarkEdited(adt); _terrain!.RebuildTile(adt, _texCache); }
    }

    private static bool Adjust(Salmiak.Core.Formats.MapChunk ch, float col, float row, int vi,
                               float amount, float radius, Vector3 hit, float step, BrushShape shape)
    {
        float gx = -(ch.Position.Y - col * step);
        float gz = -(ch.Position.X - row * step);
        float dx = gx - hit.X, dz = gz - hit.Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d >= radius) return false;
        ch.Heights[vi] += amount * Falloff(d, radius, shape);
        return true;
    }

    private void ToggleRiverMode()
    {
        if (!EditMode || DoodadMode || WmoMode)
        {
            Notify?.Invoke("Enable terrain edit mode (Ctrl+E) to use the river carver.");
            return;
        }
        RiverMode = !RiverMode;
        _riverNodes.Clear();
        _riverVertCount = 0;
        if (RiverMode)
        {
            OpenRiverPicker?.Invoke();
            Notify?.Invoke("River: click waypoints, Enter carves, Backspace removes a point, Esc cancels.");
            return;
        }
        Notify?.Invoke("River carve off.");
    }

    private void RiverClick(int mx, int my)
    {
        if (!PickTerrain(mx, my, out var hit)) return;
        _riverNodes.Add(hit);
        RebuildRiverPreview();
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t
                       + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                       + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private List<Vector3> SampleRiverSpline(float spacing)
    {
        var pts = new List<Vector3>();
        int n = _riverNodes.Count;
        if (n < 2) return pts;
        for (int i = 0; i < n - 1; i++)
        {
            var p0 = _riverNodes[Math.Max(i - 1, 0)];
            var p1 = _riverNodes[i];
            var p2 = _riverNodes[i + 1];
            var p3 = _riverNodes[Math.Min(i + 2, n - 1)];
            float dx = p2.X - p1.X, dz = p2.Z - p1.Z;
            int steps = Math.Max(2, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / spacing));
            for (int s = i == 0 ? 0 : 1; s <= steps; s++)
                pts.Add(CatmullRom(p0, p1, p2, p3, s / (float)steps));
        }
        return pts;
    }

    private static (float D2, float RefY) NearestOnSpline(List<Vector3> pts, float gx, float gz)
    {
        float best = float.MaxValue, refY = 0f;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            float ax = pts[i].X, az = pts[i].Z;
            float bx = pts[i + 1].X, bz = pts[i + 1].Z;
            float abx = bx - ax, abz = bz - az;
            float len2 = abx * abx + abz * abz;
            float t = len2 > 1e-6f ? Math.Clamp(((gx - ax) * abx + (gz - az) * abz) / len2, 0f, 1f) : 0f;
            float px = ax + abx * t, pz = az + abz * t;
            float dx = gx - px, dz = gz - pz;
            float d2 = dx * dx + dz * dz;
            if (d2 < best) { best = d2; refY = pts[i].Y + (pts[i + 1].Y - pts[i].Y) * t; }
        }
        return (best, refY);
    }

    private void RebuildRiverPreview()
    {
        var verts = new List<float>();
        foreach (var nd in _riverNodes) AppendCross(verts, nd + new Vector3(0f, 0.6f, 0f), 1.6f);

        var pts = SampleRiverSpline(3f);
        float half = _riverBedWidth * 0.5f;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            void Seg(Vector3 a, Vector3 b)
            {
                float ya = HeightAtGL(a.X, a.Z), yb = HeightAtGL(b.X, b.Z);
                verts.Add(a.X); verts.Add(float.IsNaN(ya) ? a.Y + 0.5f : ya + 0.5f); verts.Add(a.Z);
                verts.Add(b.X); verts.Add(float.IsNaN(yb) ? b.Y + 0.5f : yb + 0.5f); verts.Add(b.Z);
            }
            var p = pts[i]; var q = pts[i + 1];
            float dx = q.X - p.X, dz = q.Z - p.Z;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 1e-4f) continue;
            var perp = new Vector3(-dz / len * half, 0f, dx / len * half);
            Seg(p, q);
            Seg(p + perp, q + perp);
            Seg(p - perp, q - perp);
        }

        _riverVertCount = verts.Count / 3;
        if (_riverVerts.Length < verts.Count) _riverVerts = new float[verts.Count];
        verts.CopyTo(_riverVerts);
    }

    private void CarveRiver()
    {
        if (_riverNodes.Count < 2) { Notify?.Invoke("River: click at least two waypoints first."); return; }
        var pts = SampleRiverSpline(2f);
        if (pts.Count < 2) return;

        float halfBed = _riverBedWidth * 0.5f;
        float outer = halfBed + _riverBankWidth;
        const float step = AdtFile.ChunkSize / 8f;

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in pts)
        { minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X); minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z); }
        minX -= outer; maxX += outer; minZ -= outer; maxZ += outer;

        var rec = new Stroke();
        var affected = new HashSet<AdtFile>();
        _carveSnapshotTarget = rec;

        foreach (var kv in _tileCache)
        {
            var adt = kv.Value;
            if (adt == null) continue;
            (int tx, int ty) = kv.Key;
            float oX = -(32 - tx) * AdtFile.TileSize, oZ = -(32 - ty) * AdtFile.TileSize;
            if (maxX < oX || minX > oX + AdtFile.TileSize || maxZ < oZ || minZ > oZ + AdtFile.TileSize) continue;

            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var chunk = adt.Chunks[cy, cx];
                if (chunk == null) continue;
                float cMinX = -chunk.Position.Y, cMinZ = -chunk.Position.X;
                if (maxX < cMinX || minX > cMinX + AdtFile.ChunkSize ||
                    maxZ < cMinZ || minZ > cMinZ + AdtFile.ChunkSize) continue;

                bool touched = false;
                int vi = 0;
                for (int row = 0; row < 9; row++)
                {
                    for (int col = 0; col < 9; col++) { if (CarveVertex(chunk, col, row, vi, pts, halfBed, outer, step)) touched = true; vi++; }
                    if (row < 8)
                        for (int col = 0; col < 8; col++) { if (CarveVertex(chunk, col + .5f, row + .5f, vi, pts, halfBed, outer, step)) touched = true; vi++; }
                }
                if (touched)
                {
                    affected.Add(adt);
                    rec.Tiles.Add(adt);
                }
            }
        }
        foreach (var adt in affected) { MarkEdited(adt); _terrain!.UpdateTileHeights(adt); }

        if (_riverWaterType > 0)
        {
            var waterAdts = new HashSet<AdtFile>();
            foreach (var kv in _tileCache)
            {
                var adt = kv.Value;
                if (adt == null) continue;
                (int tx, int ty) = kv.Key;
                float oX = -(32 - tx) * AdtFile.TileSize, oZ = -(32 - ty) * AdtFile.TileSize;
                if (maxX < oX || minX > oX + AdtFile.TileSize || maxZ < oZ || minZ > oZ + AdtFile.TileSize) continue;

                for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
                for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
                {
                    var chunk = adt.Chunks[cy, cx];
                    if (chunk == null || chunk.Liquid != null) continue;
                    float cMinX = -chunk.Position.Y, cMinZ = -chunk.Position.X;
                    if (maxX < cMinX || minX > cMinX + AdtFile.ChunkSize ||
                        maxZ < cMinZ || minZ > cMinZ + AdtFile.ChunkSize) continue;

                    var liq = BuildRiverLiquid(chunk, pts, halfBed, step);
                    if (liq == null) continue;
                    if (!rec.Liquid.ContainsKey(chunk)) rec.Liquid[chunk] = chunk.Liquid;
                    chunk.Liquid = liq;
                    rec.Tiles.Add(adt);
                    waterAdts.Add(adt);
                }
            }
            foreach (var adt in waterAdts) { MarkEdited(adt); _terrain!.RebuildWater(adt); }
        }

        _carveSnapshotTarget = null;
        if (rec.Heights.Count > 0 || rec.Liquid.Count > 0) PushUndo(rec);
        _riverNodes.Clear();
        _riverVertCount = 0;
        Notify?.Invoke($"River carved through {rec.Heights.Count} chunk(s){(_riverWaterType > 0 ? $", {rec.Liquid.Count} with liquid" : "")}. Ctrl+Z undoes.");
    }

    private Stroke? _carveSnapshotTarget;

    private bool CarveVertex(Salmiak.Core.Formats.MapChunk ch, float col, float row, int vi,
                             List<Vector3> pts, float halfBed, float outer, float step)
    {
        float gx = -(ch.Position.Y - col * step);
        float gz = -(ch.Position.X - row * step);
        var (d2, refY) = NearestOnSpline(pts, gx, gz);
        if (d2 >= outer * outer) return false;
        float d = MathF.Sqrt(d2);

        float baseY = ch.Position.Z;
        float h = baseY + ch.Heights[vi];
        float target;
        if (d <= halfBed)
        {
            float u = d / halfBed;
            target = refY - _riverDepth * (1f - u * u);
            target += (Fbm(gx * 0.09f, gz * 0.09f) - 0.5f) * _riverDepth * 0.18f;
        }
        else
        {
            float s = Smoothstep(0f, 1f, (d - halfBed) / MathF.Max(outer - halfBed, 0.01f));
            target = refY + (h - refY) * s;
        }
        if (target >= h) return false;

        if (_carveSnapshotTarget != null && !_carveSnapshotTarget.Heights.ContainsKey(ch))
            _carveSnapshotTarget.Heights[ch] = (float[])ch.Heights.Clone();
        ch.Heights[vi] = target - baseY;
        return true;
    }

    private LiquidLayer? BuildRiverLiquid(Salmiak.Core.Formats.MapChunk chunk, List<Vector3> pts,
                                          float halfBed, float step)
    {
        Span<float> levels = stackalloc float[81];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int row = 0; row < 9; row++)
        for (int col = 0; col < 9; col++)
        {
            float gx = -(chunk.Position.Y - col * step);
            float gz = -(chunk.Position.X - row * step);
            var (_, refY) = NearestOnSpline(pts, gx, gz);
            float lvl = refY - _riverDepth + _riverWaterDepth;
            levels[row * 9 + col] = lvl;
            lo = MathF.Min(lo, lvl); hi = MathF.Max(hi, lvl);
        }

        var render = new bool[64];
        bool any = false;
        for (int row = 0; row < 8; row++)
        for (int col = 0; col < 8; col++)
        {
            float gx = -(chunk.Position.Y - (col + 0.5f) * step);
            float gz = -(chunk.Position.X - (row + 0.5f) * step);
            var (d2, refY) = NearestOnSpline(pts, gx, gz);
            if (d2 > halfBed * halfBed) continue;
            float lvl = refY - _riverDepth + _riverWaterDepth;
            float ground = HeightAtGL(gx, gz);
            if (!float.IsNaN(ground) && lvl > ground + 0.05f) { render[row * 8 + col] = true; any = true; }
        }
        if (!any) return null;

        var liq = new LiquidLayer { Type = _riverWaterType, MinHeight = lo, MaxHeight = hi };
        for (int i = 0; i < 81; i++) liq.Heights[i] = levels[i];
        for (int i = 0; i < 64; i++) liq.Render[i] = render[i];
        return liq;
    }
}
