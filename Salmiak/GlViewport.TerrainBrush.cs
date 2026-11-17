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
    //Display only: paints rendered shadows dark violet so a bake's outline is readable at a glance,
    //which the plain Blizzard dimming often is not. Kept out of the bake options because it changes
    //nothing about what gets ray-traced or exported, and the gamelike look stays the default.
    public bool ShadowHighlight { get; set; }

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

                float cs = AdtFile.ChunkSize, sx = 64f / cs;
                int rx0 = 64, ry0 = 64, rx1 = -1, ry1 = -1;
                int tX0 = Math.Clamp((int)MathF.Floor((hit.X - r + chunk.Position.Y) * sx), 0, 63);
                int tX1 = Math.Clamp((int)MathF.Ceiling((hit.X + r + chunk.Position.Y) * sx), 0, 63);
                int tZ0 = Math.Clamp((int)MathF.Floor((hit.Z - r + chunk.Position.X) * sx), 0, 63);
                int tZ1 = Math.Clamp((int)MathF.Ceiling((hit.Z + r + chunk.Position.X) * sx), 0, 63);
                bool changed = false;

                for (int tz = tZ0; tz <= tZ1; tz++)
                for (int txp = tX0; txp <= tX1; txp++)
                {
                    //Same grid as the baker: texel (txp, tz) covers ChunkSize/64 of ground and is
                    //painted at the middle of that cell, which is where the client and the terrain
                    //shader read it back, so strokes land on the cells the rays would have filled.
                    float gx = -(chunk.Position.Y - (txp + 0.5f) * cs / 64f);
                    float gz = -(chunk.Position.X - (tz + 0.5f) * cs / 64f);
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
        //The ground each target's rays start on, as the span of the tile it lives in: the box test
        //below needs it per chunk, and a chunk's own heightmap is not enough because a texel on a
        //chunk border leans on the next chunk's height vertices, which can be much higher or lower.
        public float[] HyLo = [], HyHi = [];
        public string Map = "";
    };

    private BakeJob? _job;

    //A bake that was only asked for: the click queues it, the render loop gathers on a later frame.
    //Gathering is not cheap (CPU copies of every WMO group and M2 model that can reach the chunks,
    //plus one triangle template and box list per geometry) and doing it inside the click spends the
    //whole frame there, so an emptied bar would only be drawn once the rays had already started.
    private BakeScope _queuedScope;
    private bool _bakeQueued;
    private bool _bakeQueuedIdleDrawn;

    //Blocking bake for the map menu, which has no viewport frames to drive the incremental one.
    public int BakeShadows(BakeScope scope)
    {
        if (_job != null) { Notify?.Invoke("Bake shadows: a bake is already running in the viewport, wait for its bar."); return 0; }
        if (_bakeQueued) { Notify?.Invoke("Bake shadows: a bake is queued in the viewport and gathering its models - wait for its bar."); return 0; }
        if (!PrepareShadowBake(scope)) return 0;
        BakeChunks(-1);
        return FinishShadowBake();
    }

    //Starts a bake that the viewport render loop walks slice by slice, so the window keeps drawing
    //and the shadow sidebar's progress bar moves while the rays run.
    public bool StartShadowBake(BakeScope scope)
    {
        if (_job != null) { Notify?.Invoke("Bake shadows: a bake is already running - wait until its bar is full."); return false; }
        if (_bakeQueued) { Notify?.Invoke("Bake shadows: the models of the bake you started are still being gathered - wait for its bar."); return false; }
        //Nothing heavy here on pain of the click never reading: the bar is emptied and labelled, the
        //model gather happens in StepBakeFrame, and it gets its own frame, so the sidebar always
        //shows an empty blue bar with "Preparing models" before any ray is traced.
        _queuedScope = scope;
        _bakeQueued = true;
        _bakeQueuedIdleDrawn = false;
        ShadowBakeProgressReset?.Invoke();
        ShadowBakePreparing?.Invoke();
        Notify?.Invoke("Ray-baking shadows frame by frame - watch the bar under the Bake button, it turns green when the bake lands; Ctrl+Z undoes it.");
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
                    //The placement's transform, and nothing else. The box each group is culled by
                    //comes from that group's own triangles below, which lets a model that was never
                    //uploaded for drawing still bake, and keeps a block of a city from being culled
                    //by a sphere as large as the block.
                    var wxf = Salmiak.Rendering.WmoRenderer.BuildTransform(w);
                    var winv = wxf.Inverted();
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
                                        //The renderer's own decoded pixels: a WMO that is on screen has
                                        //them already, so a bake near a city opens no BLP at all.
                                        dec = _wmos!.CpuTexture(tr.Tex);
                                    wmoTex[tr.Tex] = dec;
                                    var (decRgba, decW, decH) = dec;
                                    tr.Rgba = decRgba; tr.TW = decW; tr.TH = decH;
                                }
                                ranges.Add(tr);
                            }
                            tpl = wmoTpl[grp] = new ShadowOcc { W = true, V = grp.Vertices, Idx = grp.Indices,
                                                                Stride = WmoFile.GroupVertexStride, Ranges = ranges.ToArray() };
                            tpl.BBox = BuildTriBoxes(tpl.V, tpl.Idx, tpl.Stride);
                            tpl.Grid = BuildTriGrid(tpl.Ranges, tpl.Idx, tpl.BBox);
                            tpl.LB = tpl.Grid != null ? GridBox(tpl.Grid) : LocalBoxOf(tpl.V, tpl.Stride);
                        }
                        if (tpl.Ranges.Length == 0 || tpl.LB.Length < 6) continue;
                        //The group's own box through this placement's transform, re-boxed along the
                        //world axes: this is what rejects the buildings a ray cannot reach at all.
                        var wb = TransformBox(tpl.LB, wxf);
                        if (wb.Length < 6) continue;
                        var wcen = new Vector3((wb[0] + wb[3]) * 0.5f, (wb[1] + wb[4]) * 0.5f, (wb[2] + wb[5]) * 0.5f);
                        var wdi = new Vector3(wb[3] - wb[0], wb[4] - wb[1], wb[5] - wb[2]);
                        wmoOcc.Add(new ShadowOcc { W = true, WC = wcen, WR = Math.Max(2f, wdi.Length * 0.5f), Scale = 1f, Inv = winv,
                                                   V = tpl.V, Idx = tpl.Idx, Stride = tpl.Stride,
                                                   Ranges = tpl.Ranges, BBox = tpl.BBox,
                                                   LB = tpl.LB, WB = wb, Grid = tpl.Grid });
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
                    //The model's own box comes from the CPU copy that was just read, not from the
                    //renderer: a doodad whose GPU copy is still on its way can be baked anyway, and
                    //its sphere then only has to be close enough for the box below to do the real
                    //rejecting.
                    var mrad = cm.Radius;
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
                        tpl.Grid = BuildTriGrid(tpl.Ranges, tpl.Idx, tpl.BBox);
                        tpl.LB = tpl.Grid != null ? GridBox(tpl.Grid) : LocalBoxOf(tpl.V, tpl.Stride);
                    }
                    if (tpl.Ranges.Length == 0 || tpl.LB.Length < 6) continue;
                    //The model's box through this placement, same as a WMO group gets its own: the
                    //rays of a chunk only ever meet a doodad whose turned, scaled box lies inside
                    //the segment they sweep.
                    var wb = TransformBox(tpl.LB, dxf);
                    if (wb.Length < 6) continue;
                    var wcen = new Vector3((wb[0] + wb[3]) * 0.5f, (wb[1] + wb[4]) * 0.5f, (wb[2] + wb[5]) * 0.5f);
                    var wdi = new Vector3(wb[3] - wb[0], wb[4] - wb[1], wb[5] - wb[2]);
                    float dr = Math.Max(0.5f, mrad * Math.Abs(d.Scale));
                    m2Occ.Add(new ShadowOcc { W = false, WC = wcen, WR = Math.Max(dr, wdi.Length * 0.5f), Inv = dxf.Inverted(),
                                              Scale = Math.Abs(d.Scale) < 1e-4f ? 1f : d.Scale,
                                              V = tpl.V, Idx = tpl.Idx, Stride = tpl.Stride,
                                              Ranges = tpl.Ranges, BBox = tpl.BBox,
                                              LB = tpl.LB, WB = wb, Grid = tpl.Grid });
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
        //The client's own matrix settles the grid: AllocShadowTexture makes one 64x64 texture per
        //chunk, and the shadow pass binds it to Tex1, whose transform is the alpha matrix scaled by
        //0.125 (MapRenderChunk.cpp: alphaScale.x = -s_geoToTex * 0.125, s_geoToTex = -1 / vertexList
        //[1].y = 1 / 4.1666665f). So texcoord = world offset / ChunkSize: texel k covers the world
        //CELL from k * ChunkSize/64 up to one cell further east, and the texel is read at the middle
        //of that cell, not at its west edge. Tracing at the centre is what keeps one chunk's last
        //cell butting against the next chunk's first one; dividing the chunk by 63 walked the grid
        //short of its own border, and then the outermost chunk of a TILE traced its last texel
        //exactly on the tile edge, where the height lookup has no height at all - those texels were
        //skipped and left a shadowless line one texel wide along tile borders, which a later bake
        //could not clear either because it never looked at them.
        float texStep = cs / 64f;
        var opt = new ShadowOpt { AlphaTest = ShadowAlphaTest, AlphaThreshold = alphaThreshold,
                                   SkipTransparent = ShadowSkipTransparent, Terrain = ShadowTerrainBlocks,
                                   Jitter = baseJitter, SubSamples = subSamples, SubThreshold = subThreshold,
                                   Blocks = texBlocks };

        //The ground each target's rays start on, once per tile rather than per chunk: the box test
        //that gathers a chunk's occluders has to know how high the ground runs, and a texel that sits
        //on a chunk border leans on the next chunk's height vertices, which can be far higher or
        //lower than the chunk's own ground - a chunk at the foot of a cliff is the case where this
        //matters most. A texel outside a streamed tile has no ground at all, so it needs no span.
        var hyLo = new float[targets.Count];
        var hyHi = new float[targets.Count];
        var spanOf = new Dictionary<AdtFile, (float Lo, float Hi)>();
        for (int i = 0; i < targets.Count; i++)
        {
            var adt = targets[i].Adt;
            if (!spanOf.TryGetValue(adt, out var span))
                spanOf[adt] = span = GroundSpan(adt);
            hyLo[i] = span.Lo;
            hyHi[i] = span.Hi;
        }

        //TEMPORARY, one bake per session: the cell walk must decide the same thing as the plain walk.
        //When it does not, dropping the grids of this bake keeps the shadows right, and the message
        //says so rather than leaving a bake that quietly lost a shadow to be read as a bug.
        if (ShadowGridCheck)
        {
            ShadowGridCheck = false;
            var gridOk = CheckShadowGrids(wmoOcc, m2Occ, opt, maxDist);
            if (gridOk == 2)
            {
                foreach (var lst in new[] { wmoOcc, m2Occ })
                    foreach (var o in lst)
                        o.Grid = null;
                foreach (var t in wmoTpl.Values) t.Grid = null;
                foreach (var t in m2Tpl.Values) t.Grid = null;
                Notify?.Invoke("Shadow self-check: the triangle grid disagreed with the plain walk over the same triangles, " +
                               "so the grids were dropped and this bake walks every triangle it is told about. The shadows are " +
                               "the right ones either way, only slower.");
            }
            else if (gridOk == 0)
                Notify?.Invoke("Shadow self-check: nothing testable in this bake, the grid walk was not compared. Report it only " +
                               "as a note - the bake itself is unaffected.");
        }

        foreach (var (adt, ch) in targets)
        {
            rec.Shadows[ch] = ch.ShadowMap != null ? (byte[])ch.ShadowMap.Clone() : null;
            rec.Tiles.Add(adt);
        }

        var touched = new bool[targets.Count];
        _job = new BakeJob { Targets = targets, Wmos = wmoOcc, M2s = m2Occ, Opt = opt, Rec = rec,
                             Touched = touched, Sun = sun, MaxDist = maxDist, Cs = cs, TexStep = texStep,
                             SubSamples = subSamples, SubThreshold = subThreshold, BaseJitter = baseJitter,
                             HyLo = hyLo, HyHi = hyHi,
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

        //How far a soft pass may swing a ray sideways along the reach, in units of the reach: the
        //jittered sub-samples fan around the straight sun ray, so the box cull per chunk below has
        //to hold that fan as well. Rays of a bake that keeps the sun exact have none at all.
        var dev = baseJitter > 0f ? Math.Min(0.5f, Math.Max(0.02f, baseJitter * maxDist * 0.5f)) : 0f;

        System.Threading.Tasks.Parallel.For(from, to, i =>
        {
            var ch = targets[i].Ch;

            var cc = new Vector3(-ch.Position.Y + cs / 2f, ch.Position.Z, -ch.Position.X + cs / 2f);

            //The ground these rays start on, from the tile this chunk lives in (see PrepareShadowBake,
            //where it is measured once per tile). A chunk at the foot of a cliff or of a city wall
            //keeps its occluder whatever the height difference is.
            var hy0 = i < j.HyLo.Length ? j.HyLo[i] : -1e4f;
            var hy1 = i < j.HyHi.Length ? j.HyHi[i] : 1e4f;

            //No broadphase shortcut here. A chunk with nothing in reach still has to have every one
            //of its texels rewritten, otherwise a shadow the user brushed into such a chunk - or one
            //a previous bake left behind when the occluders were in reach then - stays in the map
            //forever and never gets cleared by the bake that should clear it.
            var localW = new List<ShadowOcc>();
            var localM = new List<ShadowOcc>();
            foreach (var m in wmoOcc)
                if (BakeNear(cc, hy0, hy1, cs, maxDist, sun, dev, m)) localW.Add(m);
            foreach (var m in m2Occ)
                if (BakeNear(cc, hy0, hy1, cs, maxDist, sun, dev, m)) localM.Add(m);

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

                    //Middle of the texel's cell, the point the client samples it at (see texStep).
                    float gx = -ch.Position.Y + (col + 0.5f) * texStep;
                    float gz = -ch.Position.X + (row + 0.5f) * texStep;
                    float gy = HeightAtGL(gx, gz);
                    if (float.IsNaN(gy))
                    {
                        //No ground to trace from, which leaves tiles that are not streamed. Clearing
                        //keeps a bake total: every texel of every selected chunk gets a verdict, so
                        //stale shadow cannot hide behind a skipped texel.
                        if (map[idx] != 0) map[idx] = 0;
                        continue;
                    }

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
                                float px = gx + (sx + 0.5f - subSamples * 0.5f) * cell;
                                float pz = gz + (sy + 0.5f - subSamples * 0.5f) * cell;
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

        //The CPU copies stay. They are what the next bake of the same stretch reads, and both
        //renderers keep them within their own budget, so getting ready for a second bake of a city
        //costs no read and no parse at all - which is why a bake near Ironforge or Orgrimmar used to
        //spend tens of seconds before its bar could even start moving.

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
        //A queued bake gets one frame of its own, with no rays and no gather in it: that frame is
        //what draws the empty blue bar and the "Preparing models" label the click asked for, and the
        //gather then costs the next frame while the sidebar still reads the same way.
        if (_job == null && _bakeQueued)
        {
            if (!_bakeQueuedIdleDrawn)
            {
                _bakeQueuedIdleDrawn = true;
                return;
            }

            var scope = _queuedScope;
            _bakeQueued = false;
            _bakeQueuedIdleDrawn = false;
            PrepareShadowBake(scope);
            return;
        }

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
        //The box around this placement's own triangles in world space, and the same box in the
        //geometry's own space. A whole city block is culled by a sphere as large as the block, so
        //near Ironforge the sphere lets nearly every ray through and the ray then pays for a
        //transform and a walk over the same building again; the box is what rejects it.
        public float[] WB = [];
        public float[] LB = [];
        //The triangles of this geometry bucketed in its own space, shared by every placement of it
        //since a ray is brought into that space anyway. Null when the geometry is too small for one
        //to pay off, in which case its triangles are simply walked.
        public TriGrid? Grid;
    }

    //The local-space triangle grid a ray walks: every triangle a range covers is bucketed into the
    //cells its own box touches, so a ray only meets the triangles sitting in the cells it crosses
    //instead of every triangle of every building in reach. A triangle whose box spans many cells
    //goes to Wide instead, which is always tested: there are few of them, long walls and floor
    //quads, and pasting them into hundreds of cells would cost more than testing them does.
    private sealed class TriGrid
    {
        public int DX, DY, DZ;
        public float MinX, MinY, MinZ, Cell;
        public int[] Start = [];   // one past the last triangle of each cell, so cells + 1 entries
        public int[] Tri = [];
        public int[] Wide = [];
        public int[] RangeOf = []; // which entry of Ranges covers a triangle, -1 when none does
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
            //Measured straight here, without a pair-sized array per triangle: this runs over every
            //triangle of every model a bake reads, which is a large share of what getting ready
            //costs near a city, and allocating for each triangle buries the compiler in garbage.
            for (int k = 1; k <= 2; k++)
            {
                int o = k == 1 ? o1 : o2;
                if (v[o] < minX) minX = v[o]; else if (v[o] > maxX) maxX = v[o];
                if (v[o + 1] < minY) minY = v[o + 1]; else if (v[o + 1] > maxY) maxY = v[o + 1];
                if (v[o + 2] < minZ) minZ = v[o + 2]; else if (v[o + 2] > maxZ) maxZ = v[o + 2];
            }
            bb[b] = minX; bb[b + 1] = minY; bb[b + 2] = minZ;
            bb[b + 3] = maxX; bb[b + 4] = maxY; bb[b + 5] = maxZ;
        }
        return bb;
    }

    //The box around a geometry's own vertices, in its own space: the loosest box that costs nothing
    //but a pass over the positions, and the fallback for a model with no grid of its own. Empty when
    //there is nothing measurable in there.
    private static float[] LocalBoxOf(float[] v, int stride)
    {
        var bb = new float[] { float.MaxValue, float.MaxValue, float.MaxValue,
                               float.MinValue, float.MinValue, float.MinValue };
        for (int o = 0; o + 2 < v.Length; o += stride)
        {
            float x = v[o], y = v[o + 1], z = v[o + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            if (x < bb[0]) bb[0] = x;
            if (y < bb[1]) bb[1] = y;
            if (z < bb[2]) bb[2] = z;
            if (x > bb[3]) bb[3] = x;
            if (y > bb[4]) bb[4] = y;
            if (z > bb[5]) bb[5] = z;
        }
        return bb[0] > bb[3] ? [] : bb;
    }

    //The same box through a transform, re-boxed along the axes: a WMO is rotated, so the rays have
    //to be rejected in world space against a box that grew to hold the turned one.
    private static float[] TransformBox(float[] box, Matrix4 xf)
    {
        if (box.Length < 6) return [];
        var bb = new float[] { float.MaxValue, float.MaxValue, float.MaxValue,
                               float.MinValue, float.MinValue, float.MinValue };
        for (int c = 0; c < 8; c++)
        {
            var p = Vector3.TransformPosition(new Vector3((c & 1) == 0 ? box[0] : box[3],
                                                           (c & 2) == 0 ? box[1] : box[4],
                                                           (c & 4) == 0 ? box[2] : box[5]), xf);
            if (p.X < bb[0]) bb[0] = p.X;
            if (p.Y < bb[1]) bb[1] = p.Y;
            if (p.Z < bb[2]) bb[2] = p.Z;
            if (p.X > bb[3]) bb[3] = p.X;
            if (p.Y > bb[4]) bb[4] = p.Y;
            if (p.Z > bb[5]) bb[5] = p.Z;
        }
        return bb[0] > bb[3] ? [] : bb;
    }

    //A grid's own box: the cells cover the boxes of every triangle bucketed into it, wide ones
    //included, so this is the box the rays of this geometry can ever touch and the box a placement
    //is culled by.
    private static float[] GridBox(TriGrid g)
    {
        if (g.DX <= 0) return [];
        return new float[] { g.MinX, g.MinY, g.MinZ,
                             g.MinX + g.DX * g.Cell, g.MinY + g.DY * g.Cell, g.MinZ + g.DZ * g.Cell };
    }

    //The triangles of one geometry, bucketed in its own space: every triangle a range covers goes
    //into the cells its own box touches, so a ray only meets the triangles that sit in the cells it
    //crosses. A cell size around the size of a triangle is what keeps a ray down to the handful of
    //triangles it passes, and triangles whose box spans many cells are kept aside in Wide instead,
    //since pasting a long wall into a hundred cells costs more than testing it does for the few
    //rays that reach it. Null for geometry too small or too lopsided for this to pay off, in which
    //case its triangles are simply walked.
    private static TriGrid? BuildTriGrid(TriRange[] ranges, uint[] idx, float[] bb)
    {
        int nTri = idx.Length / 3;
        if (nTri < 96) return null;

        // which range covers which triangle: a triangle no range covers is never tested at all, so
        // it must not be in the grid either.
        var rangeOf = new int[nTri];
        for (int i = 0; i < nTri; i++) rangeOf[i] = -1;
        for (int r = 0; r < ranges.Length; r++)
        {
            var rg = ranges[r];
            int last = Math.Min(rg.Start + rg.Count, (int)idx.Length);
            for (int rt = Math.Max(0, rg.Start) / 3, lastTri = last / 3; rt < lastTri; rt++)
                rangeOf[rt] = r;
        }

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        var span = 0f;
        var covered = 0;
        for (int t = 0; t < nTri; t++)
        {
            if (rangeOf[t] < 0) continue;
            var b = t * 6;
            if (b + 5 >= bb.Length || bb[b] > bb[b + 3]) continue;
            float sx = bb[b + 3] - bb[b], sy = bb[b + 4] - bb[b + 1], sz = bb[b + 5] - bb[b + 2];
            span += Math.Max(Math.Max(sx, sy), sz);
            covered++;
            if (bb[b] < minX) minX = bb[b];
            if (bb[b + 1] < minY) minY = bb[b + 1];
            if (bb[b + 2] < minZ) minZ = bb[b + 2];
            if (bb[b] > maxX) maxX = bb[b];
            if (bb[b + 1] > maxY) maxY = bb[b + 1];
            if (bb[b + 2] > maxZ) maxZ = bb[b + 2];
        }
        if (covered == 0 || !(minX <= maxX) || !(minY <= maxY) || !(minZ <= maxZ)) return null;
        float ex = maxX - minX, ey = maxY - minY, ez = maxZ - minZ;
        var biggest = Math.Max(Math.Max(Math.Max(ex, ey), ez), 1e-3f);

        //A cell around the size of a triangle is what keeps a ray down to the handful of triangles
        //it really passes: a triangle then lands in a couple of cells, and the ray crosses only the
        //cells it goes through. Bounded so a whole city cannot ask for a million cells.
        var cell = Math.Max(1f, span / covered);
        if (biggest / cell > 256f) cell = biggest / 256f;
        if (!(cell > 1e-4f) || !float.IsFinite(cell)) return null;
        int dx = Math.Max(1, (int)Math.Floor(ex / cell) + 1),
            dy = Math.Max(1, (int)Math.Floor(ey / cell) + 1),
            dz = Math.Max(1, (int)Math.Floor(ez / cell) + 1);
        var cells = (long)dx * dy * dz;
        if (cells > (1L << 20)) return null;
        int cc = (int)cells;

        int x0 = 0, x1 = 0, y0 = 0, y1 = 0, z0 = 0, z1 = 0;
        var eps = cell * 1e-3f;
        //The cells a triangle's own box touches. False when the box could not be measured at all,
        //which puts the triangle in Wide: such a triangle reaches past the vertex array, and the ray
        //test below throws it away just as the plain walk does.
        bool CellsOf(int t)
        {
            var b = t * 6;
            if (b + 5 >= bb.Length || bb[b] > bb[b + 3]) return false;
            x0 = Math.Clamp((int)Math.Floor((bb[b] + eps - minX) / cell), 0, dx - 1);
            x1 = Math.Clamp((int)Math.Floor((bb[b + 3] + eps - minX) / cell), 0, dx - 1);
            y0 = Math.Clamp((int)Math.Floor((bb[b + 1] + eps - minY) / cell), 0, dy - 1);
            y1 = Math.Clamp((int)Math.Floor((bb[b + 4] + eps - minY) / cell), 0, dy - 1);
            z0 = Math.Clamp((int)Math.Floor((bb[b + 2] + eps - minZ) / cell), 0, dz - 1);
            z1 = Math.Clamp((int)Math.Floor((bb[b + 5] + eps - minZ) / cell), 0, dz - 1);
            return true;
        }

        var count = new int[cc];
        var wide = new List<int>();
        for (int t = 0; t < nTri; t++)
        {
            if (rangeOf[t] < 0) continue;
            if (!CellsOf(t)) { wide.Add(t); continue; }
            if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 8) { wide.Add(t); continue; }
            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                {
                    var row = (z * dy + y) * dx;
                    for (var x = x0; x <= x1; x++) count[row + x]++;
                }
        }

        var start = new int[cc + 1];
        for (int c = 0; c < cc; c++) start[c + 1] = start[c] + count[c];
        var tri = new int[start[cc]];
        for (int c = 0; c < cc; c++) count[c] = start[c];
        for (int t = 0; t < nTri; t++)
        {
            if (rangeOf[t] < 0) continue;
            if (!CellsOf(t)) continue;
            if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 8) continue;
            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                {
                    var row = (z * dy + y) * dx;
                    for (var x = x0; x <= x1; x++) tri[count[row + x]++] = t;
                }
        }
        return new TriGrid
        {
            DX = dx, DY = dy, DZ = dz,
            MinX = minX, MinY = minY, MinZ = minZ, Cell = cell,
            Start = start, Tri = tri, Wide = wide.ToArray(), RangeOf = rangeOf,
        };
    }

    //The loose test used when a placement carries no box of its own: the model's sphere, inflated by
    //the reach of a ray from the chunk, plus the test that it is not behind the chunk along the sun.
    private static bool BakeNearSphere(Vector3 cc, ShadowOcc m, float cs, float maxDist, Vector3 sun)
    {
        float dx = m.WC.X - cc.X, dy = m.WC.Y - cc.Y, dz = m.WC.Z - cc.Z;
        float r = maxDist + cs + m.WR;
        if (dx * dx + dy * dy + dz * dz > r * r) return false;
        return dx * sun.X + dy * sun.Y + dz * sun.Z >= -m.WR - cs;
    }

    //Can the rays that start inside this chunk ever reach this placement's own box? They run maxDist
    //along the sun from the chunk's footprint at its own ground, hy0 to hy1, so they sweep that
    //footprint pushed by the segment they trace - per axis, by how far the sun goes along it. This
    //box replaces the sphere test above for everything the baker builds now, and the difference is
    //large near a city: a block of buildings is culled by a sphere as large as the block, which
    //accepted nearly every chunk and left every ray of the bake to be moved into the model's space
    //and walked over the same building again. dev is how far a soft pass can swing a ray sideways.
    private static bool BakeNear(Vector3 cc, float hy0, float hy1, float cs, float maxDist,
                                 Vector3 sun, float dev, ShadowOcc m)
    {
        var wb = m.WB;
        if (wb.Length < 6) return BakeNearSphere(cc, m, cs, maxDist, sun);
        float reachX = maxDist * (MathF.Abs(sun.X) + dev);
        float reachY = maxDist * (MathF.Abs(sun.Y) + dev);
        float reachZ = maxDist * (MathF.Abs(sun.Z) + dev);
        float x0 = cc.X - cs, x1 = cc.X + cs;
        float z0 = cc.Z - cs, z1 = cc.Z + cs;
        if (wb[3] < x0 - (sun.X < 0f ? reachX : 0f) || wb[0] > x1 + (sun.X > 0f ? reachX : 0f)) return false;
        if (wb[4] < hy0 - (sun.Y < 0f ? reachY : 0f) || wb[1] > hy1 + (sun.Y > 0f ? reachY : 0f)) return false;
        if (wb[5] < z0 - (sun.Z < 0f ? reachZ : 0f) || wb[2] > z1 + (sun.Z > 0f ? reachZ : 0f)) return false;
        return true;
    }

    //The lowest and highest ground a tile's heightmaps give: the rays of a bake start on the ground
    //of the tile their texel sits in, so this is the span they sweep vertically. Every chunk of the
    //tile is walked because a texel on a chunk border is read over the seam, where the neighbouring
    //chunk's own height vertices take part and can be far off a cliff. A tile with no readable height
    //gets a span wide enough that nothing is rejected because of it.
    private (float, float) GroundSpan(AdtFile adt)
    {
        float mn = float.MaxValue, mx = float.MinValue;
        for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var ch = adt.Chunks[cy, cx];
                if (ch == null) continue;
                float zBase = ch.Position.Z;
                foreach (var h in ch.Heights)
                {
                    if (!float.IsFinite(h)) continue;
                    var at = zBase + h;
                    if (at < mn) mn = at;
                    if (at > mx) mx = at;
                }
            }
        if (!(mn <= mx)) { mn = 0f; mx = 0f; }
        //The margin is the ray's own lift plus the texels that lean over the tile's own edge.
        return (mn - 1f, mx + 1f);
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

    //TEMPORARY, one bake per session: does the cell walk agree with walking every triangle? A ray is
    //aimed at one of the geometry's own triangles from a point outside its box, in both directions,
    //so the ray has to cross the box slab, the cells and, for the triangles the grid could not hold,
    //the aside list. The very same ray is then run against a copy of that placement whose triangles
    //are simply walked. Both must call the ray occluding or not, since only that verdict is what the
    //bake reads. A ray the plain walk cannot make hit either is not a test at all, so it is left out
    //until enough of the ones that do hit have been tried. 0 = nothing testable, 1 = agreed,
    //2 = disagreed.
    private bool ShadowGridCheck = true;
    private int CheckShadowGrids(List<ShadowOcc> w, List<ShadowOcc> m, ShadowOpt opt, float maxDist)
    {
        var seed = 0x2545F491u;
        double Rnd()
        {
            seed = seed * 1664525u + 1013904223u;
            return (seed >> 8) * (1d / 16777216d);
        }

        var tried = 0;
        var mismatch = 0;
        foreach (var lst in new[] { w, m })
            foreach (var occ in lst)
            {
                if (occ.Grid == null || occ.Idx.Length < 9) continue;
                //The placement walked twice: once with its grid, once with none.
                var plain = new ShadowOcc { W = occ.W, WC = occ.WC, WR = occ.WR, Scale = occ.Scale,
                                            Inv = occ.Inv, V = occ.V, Idx = occ.Idx, Stride = occ.Stride,
                                            Ranges = occ.Ranges, BBox = occ.BBox, WB = occ.WB, LB = occ.LB };
                var toWorld = occ.Inv.Inverted();
                var lb = occ.LB;
                float diag = lb.Length >= 6 ? new Vector3(lb[3] - lb[0], lb[4] - lb[1], lb[5] - lb[2]).Length : 1f;
                float scale = Math.Abs(occ.Scale) < 1e-4f ? 1f : Math.Abs(occ.Scale);
                //Far enough out that the box slab matters, never past the reach a bake would give.
                float spread = Math.Max(1f, diag) * 0.5f + maxDist / scale * 0.45f;
                var v = occ.V;
                var idx = occ.Idx;
                int stride = occ.Stride;
                int nTri = idx.Length / 3;

                for (int k = 0; k < 8; k++)
                {
                    var ii = ((int)(Rnd() * nTri) % nTri) * 3;
                    if (ii + 2 >= idx.Length) continue;
                    int o0 = (int)idx[ii] * stride, o1 = (int)idx[ii + 1] * stride, o2 = (int)idx[ii + 2] * stride;
                    if (o2 + 2 >= v.Length) continue;
                    //The triangle's own centre, and a start point on a random side of it.
                    var at = new Vector3((v[o0] + v[o1] + v[o2]) / 3f,
                                         (v[o0 + 1] + v[o1 + 1] + v[o2 + 1]) / 3f,
                                         (v[o0 + 2] + v[o1 + 2] + v[o2 + 2]) / 3f);
                    var off = new Vector3((float)(Rnd() * 2d - 1d), (float)(Rnd() * 2d - 1d), (float)(Rnd() * 2d - 1d));
                    if (off.Length < 1e-3f) off = new Vector3(1f, 0f, 0f);
                    off = Vector3.Normalize(off) * spread;
                    if (k >= 4) off = -off;
                    var from = at - off;
                    var dir = at - from;
                    if (dir.Length < 1e-4f) continue;
                    dir = Vector3.Normalize(dir);

                    var origin = Vector3.TransformPosition(from, toWorld);
                    var sun = Vector3.Normalize(Vector3.TransformVector(dir, toWorld));
                    //The world point has to come back to the local one it was built from, or the ray
                    //below would be aimed at nothing and the check would test nothing.
                    var back = Vector3.TransformPosition(origin, occ.Inv);
                    if (new Vector3(back.X - from.X, back.Y - from.Y, back.Z - from.Z).Length > 0.02f * (1f + Math.Abs(from.Length))) continue;
                    float dPlain, dGrid;
                    var hitPlain = RayOccludes(opt, plain, origin, sun, maxDist, true, out dPlain);
                    var hitGrid = RayOccludes(opt, occ, origin, sun, maxDist, true, out dGrid);
                    //A ray that only grazes the end of its reach is not a fair test either.
                    if (hitPlain && dPlain > maxDist * 0.9f) continue;
                    if (hitGrid && dGrid > maxDist * 0.9f) continue;
                    if (hitPlain || hitGrid)
                    {
                        tried++;
                        if (hitPlain != hitGrid) mismatch++;
                    }
                    if (tried >= 4000 || mismatch > 0) return mismatch > 0 ? 2 : 1;
                }
            }
        return tried >= 50 ? 1 : 0;
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

    //One triangle of an occluder against a ray that was already brought into the occluder's own
    //space. Both walks below call this, so a bake that buckets the triangles into cells and one that
    //simply walks them decide exactly the same thing about every triangle they look at.
    private static bool TriOccludes(TriRange rg, ShadowOcc occ, int tri,
                                    float lox, float loy, float loz, float ldx, float ldy, float ldz,
                                    float maxLocal, bool alphaTest, int threshold, out float t)
    {
        t = 0f;
        var v = occ.V;
        var idx = occ.Idx;
        var bb = occ.BBox;
        var stride = occ.Stride;
        var nTri = idx.Length / 3;

        //The triangle's own box, as a slab of the ray: most of the triangles a ray is close to are
        //thrown away here, on six comparisons.
        var b = tri * 6;
        if (bb != null && tri < nTri && b + 5 < bb.Length)
        {
            float t0 = 0f, t1 = maxLocal;
            for (int ax = 0; ax < 3; ax++)
            {
                float od = ax == 0 ? lox : ax == 1 ? loy : loz;
                float dd = ax == 0 ? ldx : ax == 1 ? ldy : ldz;
                float blo = bb[b + ax], bhi = bb[b + 3 + ax];
                if (MathF.Abs(dd) < 1e-6f) { if (od < blo || od > bhi) return false; continue; }
                float ta = (blo - od) / dd, tb = (bhi - od) / dd;
                if (ta > tb) (ta, tb) = (tb, ta);
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 > t1) return false;
            }
        }

        var ii = tri * 3;
        if (ii + 2 >= idx.Length) return false;
        uint i0 = idx[ii], i1 = idx[ii + 1], i2 = idx[ii + 2];
        int o0 = (int)i0 * stride, o1 = (int)i1 * stride, o2 = (int)i2 * stride;
        if (o2 + 7 >= v.Length) return false;

        float axx = v[o0], ayy = v[o0 + 1], azz = v[o0 + 2];
        float e1x = v[o1] - axx, e1y = v[o1 + 1] - ayy, e1z = v[o1 + 2] - azz;
        float e2x = v[o2] - axx, e2y = v[o2 + 1] - ayy, e2z = v[o2 + 2] - azz;
        float px = ldy * e2z - ldz * e2y, py = ldz * e2x - ldx * e2z, pz = ldx * e2y - ldy * e2x;
        float det = e1x * px + e1y * py + e1z * pz;
        if (det > -1e-7f && det < 1e-7f) return false;
        float invDet = 1f / det;

        float sx = lox - axx, sy = loy - ayy, sz = loz - azz;
        float u = (sx * px + sy * py + sz * pz) * invDet;
        if (u < 0f || u > 1f) return false;
        float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
        float vv = (ldx * qx + ldy * qy + ldz * qz) * invDet;
        if (vv < 0f || u + vv > 1f) return false;
        float tt = (e2x * qx + e2y * qy + e2z * qz) * invDet;
        if (tt < 0.05f || tt > maxLocal) return false;

        bool sampleAlpha = rg.Alpha && alphaTest;
        var rgba = rg.Rgba;
        int tw = rg.TW, th = rg.TH;
        if (sampleAlpha && (rgba == null || rgba.Length < 4 || tw <= 0 || th <= 0)) sampleAlpha = false;
        if (sampleAlpha && rgba != null)
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
                if (ai >= rgba.Length || rgba[ai] < threshold) return false;
            }
        }

        t = tt;
        return true;
    }

    //Does one sun ray of the bake hit this occluder at all? Three filters run before a single
    //triangle is looked at, and they are ordered by how much each one costs: the model's sphere is
    //the cheap reject for something far away, the box around its own triangles is what rejects the
    //buildings a ray cannot reach at all - a whole block of a city is inside its own sphere almost
    //anywhere near it, so the sphere alone leaves every ray of a bake in Ironforge to be moved into
    //the model's space and walked over the same building - and then the ray only meets the triangles
    //that sit in the cells it crosses, instead of every triangle of the model.
    private static bool RayOccludes(ShadowOpt opt, ShadowOcc occ, Vector3 origin, Vector3 dir, float maxDist,
                                     bool alphaTest, out float dist)
    {
        dist = -1f;
        float wx = occ.WC.X - origin.X, wy = occ.WC.Y - origin.Y, wz = occ.WC.Z - origin.Z;
        float along = wx * dir.X + wy * dir.Y + wz * dir.Z;
        float tClosest = along < 0f ? 0f : along > maxDist ? maxDist : along;
        float cx = wx - dir.X * tClosest, cy = wy - dir.Y * tClosest, cz = wz - dir.Z * tClosest;
        if (cx * cx + cy * cy + cz * cz > occ.WR * occ.WR) return false;

        float tNear = 0f, tFar = maxDist;
        var wb = occ.WB;
        if (wb.Length == 6)
        {
            for (int ax = 0; ax < 3; ax++)
            {
                float od = ax == 0 ? origin.X : ax == 1 ? origin.Y : origin.Z;
                float dd = ax == 0 ? dir.X : ax == 1 ? dir.Y : dir.Z;
                float blo = wb[ax], bhi = wb[3 + ax];
                if (MathF.Abs(dd) < 1e-6f) { if (od < blo || od > bhi) return false; continue; }
                float ta = (blo - od) / dd, tb = (bhi - od) / dd;
                if (ta > tb) (ta, tb) = (tb, ta);
                if (ta > tNear) tNear = ta;
                if (tb < tFar) tFar = tb;
                if (tNear > tFar) return false;
            }
        }

        var lo = Vector3.TransformPosition(origin, occ.Inv);
        var ld = Vector3.TransformVector(dir, occ.Inv);
        float lox = lo.X, loy = lo.Y, loz = lo.Z;
        float ldx = ld.X, ldy = ld.Y, ldz = ld.Z;
        float scale = occ.Scale <= 0f ? 1f : occ.Scale;
        float maxLocal = maxDist / scale;
        var idx = occ.Idx;
        var threshold = opt.AlphaThreshold;
        float hit;

        var g = occ.Grid;
        if (g != null)
        {
            //the few triangles the grid could not hold, which are tested whatever the ray
            for (int k = 0; k < g.Wide.Length; k++)
            {
                int tri = g.Wide[k], rg = g.RangeOf[tri];
                if (rg < 0) continue;
                var wr = occ.Ranges[rg];
                if (opt.SkipTransparent && !wr.Blocks) continue;
                if (TriOccludes(wr, occ, tri, lox, loy, loz, ldx, ldy, ldz, maxLocal, alphaTest, threshold, out hit))
                { dist = hit * scale; return true; }
            }

            //the cells the ray crosses between here and the far end of its reach
            float gx0 = g.MinX, gy0 = g.MinY, gz0 = g.MinZ;
            float gx1 = gx0 + g.DX * g.Cell, gy1 = gy0 + g.DY * g.Cell, gz1 = gz0 + g.DZ * g.Cell;
            bool miss = false;
            float a0 = 0f, a1 = maxLocal;
            for (int ax = 0; ax < 3 && !miss; ax++)
            {
                float od = ax == 0 ? lox : ax == 1 ? loy : loz;
                float dd = ax == 0 ? ldx : ax == 1 ? ldy : ldz;
                float blo = ax == 0 ? gx0 : ax == 1 ? gy0 : gz0;
                float bhi = ax == 0 ? gx1 : ax == 1 ? gy1 : gz1;
                if (MathF.Abs(dd) < 1e-6f) { if (od < blo || od > bhi) miss = true; continue; }
                float ta = (blo - od) / dd, tb = (bhi - od) / dd;
                if (ta > tb) (ta, tb) = (tb, ta);
                if (ta > a0) a0 = ta;
                if (tb < a1) a1 = tb;
                if (a0 > a1) miss = true;
            }
            if (!miss)
            {
                var cell = g.Cell;
                //the cell holding the ray's entry point, and the parameter of the next cell wall of
                //each axis behind it: the walk always steps through the wall that comes first
                int cxw = Math.Clamp((int)Math.Floor((lox + ldx * a0 - gx0) / cell), 0, g.DX - 1);
                int cyw = Math.Clamp((int)Math.Floor((loy + ldy * a0 - gy0) / cell), 0, g.DY - 1);
                int czw = Math.Clamp((int)Math.Floor((loz + ldz * a0 - gz0) / cell), 0, g.DZ - 1);
                var ddx = MathF.Abs(ldx) < 1e-6f ? float.MaxValue : cell / MathF.Abs(ldx);
                var ddy = MathF.Abs(ldy) < 1e-6f ? float.MaxValue : cell / MathF.Abs(ldy);
                var ddz = MathF.Abs(ldz) < 1e-6f ? float.MaxValue : cell / MathF.Abs(ldz);
                var edge = ldx > 0f ? gx0 + (cxw + 1) * cell : gx0 + cxw * cell;
                float mx = MathF.Abs(ldx) < 1e-6f ? float.MaxValue : (edge - lox) / ldx;
                edge = ldy > 0f ? gy0 + (cyw + 1) * cell : gy0 + cyw * cell;
                float my = MathF.Abs(ldy) < 1e-6f ? float.MaxValue : (edge - loy) / ldy;
                edge = ldz > 0f ? gz0 + (czw + 1) * cell : gz0 + czw * cell;
                float mz = MathF.Abs(ldz) < 1e-6f ? float.MaxValue : (edge - loz) / ldz;
                int at = (czw * g.DY + cyw) * g.DX + cxw;
                for (;;)
                {
                    for (int k = g.Start[at], end = g.Start[at + 1]; k < end; k++)
                    {
                        int tri = g.Tri[k], rg = g.RangeOf[tri];
                        if (rg < 0) continue;
                        var wr = occ.Ranges[rg];
                        if (opt.SkipTransparent && !wr.Blocks) continue;
                        if (TriOccludes(wr, occ, tri, lox, loy, loz, ldx, ldy, ldz, maxLocal, alphaTest, threshold, out hit))
                        { dist = hit * scale; return true; }
                    }
                    //step across the nearest wall, and stop once the ray is out of its reach
                    var axisWall = mx <= my && mx <= mz ? 0 : my <= mz ? 1 : 2;
                    var stepAt = axisWall == 0 ? mx : axisWall == 1 ? my : mz;
                    if (stepAt > a1) break;
                    if (axisWall == 0) { cxw += ldx > 0f ? 1 : -1; mx += ddx; if (cxw < 0 || cxw >= g.DX) break; at = (czw * g.DY + cyw) * g.DX + cxw; }
                    else if (axisWall == 1) { cyw += ldy > 0f ? 1 : -1; my += ddy; if (cyw < 0 || cyw >= g.DY) break; at = (czw * g.DY + cyw) * g.DX + cxw; }
                    else { czw += ldz > 0f ? 1 : -1; mz += ddz; if (czw < 0 || czw >= g.DZ) break; at = (czw * g.DY + cyw) * g.DX + cxw; }
                }
            }
            return false;
        }

        //no grid: the triangles of every range, in the order they are stored
        foreach (var rg in occ.Ranges)
        {
            if (opt.SkipTransparent && !rg.Blocks) continue;
            int last = Math.Min(rg.Start + rg.Count, idx.Length);
            for (int tri = Math.Max(0, rg.Start) / 3, lastTri = last / 3; tri < lastTri; tri++)
                if (TriOccludes(rg, occ, tri, lox, loy, loz, ldx, ldy, ldz, maxLocal, alphaTest, threshold, out hit))
                { dist = hit * scale; return true; }
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
