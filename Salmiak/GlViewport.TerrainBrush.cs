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
        if (ShadowMode) { ApplyShadowBrush(hit, (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0); return; }
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

    public int BakeShadows(float azimuthDeg, float elevationDeg, float maxDist, bool doodadShadows, bool wmoShadows, bool editedOnly)
    {
        if (_terrain == null) return 0;
        MakeCurrent();

        float az = MathHelper.DegreesToRadians(azimuthDeg);
        float el = MathHelper.DegreesToRadians(Math.Clamp(elevationDeg, 5f, 85f));
        var sun = new Vector3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
        var perpA = Vector3.Normalize(Vector3.Cross(sun, Vector3.UnitY));
        var perpB = Vector3.Normalize(Vector3.Cross(sun, perpA));
        var rays = new[]
        {
            sun,
            Vector3.Normalize(sun + perpA * 0.05f + perpB * 0.025f),
            Vector3.Normalize(sun - perpA * 0.045f - perpB * 0.03f),
        };

        var spheres = new List<(Vector3 C, float R)>();
        if (doodadShadows && _doodads != null)
        {
            foreach (var kv in _tileCache)
            {
                var a = kv.Value; if (a == null) continue;
                foreach (var d in a.Doodads)
                {
                    if (string.IsNullOrEmpty(d.ModelPath)) continue;
                    if (!_doodads.TryGetModelBounds(d.ModelPath, out var mc, out var mr)) continue;
                    float r = mr * d.Scale * 0.55f;
                    if (r < 2f) continue;
                    spheres.Add((Vector3.TransformPosition(mc, Salmiak.Rendering.DoodadRenderer.BuildTransform(d)), r));
                }
            }
        }

        var boxes = new List<(Matrix4 Inv, Vector3 Min, Vector3 Max, Vector3 WC, float WR)>();
        if (wmoShadows && _wmos != null)
        {
            foreach (var kv in _tileCache)
            {
                var a = kv.Value; if (a == null) continue;
                foreach (var w in a.Wmos)
                {
                    if (string.IsNullOrEmpty(w.ModelPath)) continue;
                    if (!_wmos.TryGetModelAabb(w.ModelPath, out var mn, out var mx)) continue;
                    float wr = (mx - mn).Length * 0.5f;
                    if (wr < 8f) continue;
                    var xf = Salmiak.Rendering.WmoRenderer.BuildTransform(w);
                    boxes.Add((xf.Inverted(), mn, mx, Vector3.TransformPosition((mn + mx) * 0.5f, xf), wr));
                }
            }
        }

        var seen = new HashSet<Salmiak.Core.Formats.MapChunk>();
        var targets = new List<(AdtFile Adt, Salmiak.Core.Formats.MapChunk Ch)>();
        void AddTile(AdtFile a)
        {
            foreach (var ch in a.Chunks)
                if (ch != null && seen.Add(ch)) targets.Add((a, ch));
        }
        if (editedOnly) foreach (var a in _editedTiles.Values) AddTile(a);
        else foreach (var kv in _tileCache) if (kv.Value != null) AddTile(kv.Value);
        if (targets.Count == 0) { Notify?.Invoke("Bake shadows: nothing in scope (no edited tiles?)."); return 0; }

        var rec = new Stroke();
        var adts = new HashSet<AdtFile>();
        const float cs = AdtFile.ChunkSize;
        float texStep = cs / 63f;

        foreach (var (adt, ch) in targets)
        {
            rec.Shadows[ch] = ch.ShadowMap != null ? (byte[])ch.ShadowMap.Clone() : null;
            rec.Tiles.Add(adt);
            adts.Add(adt);
        }

        var results = new byte[]?[targets.Count];
        System.Threading.Tasks.Parallel.For(0, targets.Count, i =>
        {
            var ch = targets[i].Ch;

            var chunkCentre = new Vector3(-ch.Position.Y + cs / 2f, ch.Position.Z, -ch.Position.X + cs / 2f);
            var local = new List<(Vector3 C, float R)>();
            foreach (var s in spheres)
            {
                float dx = s.C.X - chunkCentre.X, dz = s.C.Z - chunkCentre.Z;
                if (MathF.Sqrt(dx * dx + dz * dz) > maxDist + cs + s.R) continue;
                if (Vector3.Dot(s.C - chunkCentre, sun) < -s.R - cs) continue;
                local.Add(s);
            }
            var localBoxes = new List<(Matrix4 Inv, Vector3 Min, Vector3 Max)>();
            foreach (var b in boxes)
            {
                float dx = b.WC.X - chunkCentre.X, dz = b.WC.Z - chunkCentre.Z;
                if (MathF.Sqrt(dx * dx + dz * dz) > maxDist + cs + b.WR) continue;
                if (Vector3.Dot(b.WC - chunkCentre, sun) < -b.WR - cs) continue;
                localBoxes.Add((b.Inv, b.Min, b.Max));
            }

            var map = new byte[4096];
            bool any = false;
            for (int row = 0; row < 64; row++)
            for (int col = 0; col < 64; col++)
            {
                float gx = -ch.Position.Y + col * texStep;
                float gz = -ch.Position.X + row * texStep;
                float gy = HeightAtGL(gx, gz);
                if (float.IsNaN(gy)) continue;

                if (ch.Liquid is { } lq2)
                {
                    int li = (int)MathF.Round(row / 63f * 8f) * 9 + (int)MathF.Round(col / 63f * 8f);
                    if (lq2.Heights[li] > gy + 1f) continue;
                }

                int hits = 0;
                for (int k = 0; k < rays.Length; k++)
                    if (SunRayOccluded(new Vector3(gx, gy + 0.3f, gz), rays[k], maxDist, local, localBoxes)) hits++;
                if (hits == 0) continue;

                float f = hits / (float)rays.Length;
                if (f > Fbm(gx * 2.1f, gz * 2.1f)) { map[row * 64 + col] = 255; any = true; }
            }
            results[i] = any ? map : null;
        });

        for (int i = 0; i < targets.Count; i++)
            targets[i].Ch.ShadowMap = results[i];

        PushUndo(rec);
        foreach (var a in adts) { MarkEdited(a); _terrain.RebuildTile(a, _texCache); }
        Notify?.Invoke($"Baked shadows for {targets.Count} chunk(s) ({spheres.Count} doodad + {boxes.Count} WMO occluders). Ctrl+Z undoes.");
        return targets.Count;
    }

    private bool SunRayOccluded(Vector3 origin, Vector3 dir, float maxDist,
                                List<(Vector3 C, float R)> spheres,
                                List<(Matrix4 Inv, Vector3 Min, Vector3 Max)> boxes)
    {
        foreach (var (c, r) in spheres)
        {
            var oc = c - origin;
            float b = Vector3.Dot(oc, dir);
            if (b < 0f || b > maxDist + r) continue;
            if (oc.LengthSquared - b * b <= r * r) return true;
        }
        foreach (var (inv, mn, mx) in boxes)
        {
            var o = Vector3.TransformPosition(origin, inv);
            var d = Vector3.TransformVector(dir, inv);
            float t0 = 0.5f, t1 = maxDist;
            bool miss = false;
            for (int axis = 0; axis < 3 && !miss; axis++)
            {
                float od = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
                float dd = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
                float lo = axis == 0 ? mn.X : axis == 1 ? mn.Y : mn.Z;
                float hi = axis == 0 ? mx.X : axis == 1 ? mx.Y : mx.Z;
                if (MathF.Abs(dd) < 1e-6f) { if (od < lo || od > hi) miss = true; continue; }
                float ta = (lo - od) / dd, tb = (hi - od) / dd;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
                if (t0 > t1) miss = true;
            }
            if (!miss) return true;
        }
        for (float t = 2f; t < maxDist; t += 1.2f + t * 0.06f)
        {
            float px = origin.X + dir.X * t, py = origin.Y + dir.Y * t, pz = origin.Z + dir.Z * t;
            float h = HeightAtGL(px, pz);
            if (!float.IsNaN(h) && h > py + 0.45f) return true;
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
