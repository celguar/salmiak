using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Salmiak.Core.Formats;

public sealed class ZoneLighting
{
    public const int IntBandsPerParam = 18;
    public const int FloatBandsPerParam = 6;
    public const int DefaultNightParam = 16;

    private const int FMap = 1, FX = 2, FZ = 4, FFall0 = 5, FFall1 = 6, FParamClear = 7;

    public DbcFile Light = null!;
    public DbcFile Params = null!;
    public DbcFile IntBand = null!;
    public DbcFile FloatBand = null!;

    private readonly Dictionary<uint, int> _intById = new();
    private readonly Dictionary<uint, int> _floatById = new();
    public bool LightDirty { get; private set; }
    private bool _bandsDirty;

    public static ZoneLighting Load(Func<string, Stream> open)
    {
        var z = new ZoneLighting();
        using (var s = open(@"DBFilesClient\Light.dbc")) z.Light = DbcFile.Read(s);
        using (var s = open(@"DBFilesClient\LightParams.dbc")) z.Params = DbcFile.Read(s);
        using (var s = open(@"DBFilesClient\LightIntBand.dbc")) z.IntBand = DbcFile.Read(s);
        using (var s = open(@"DBFilesClient\LightFloatBand.dbc")) z.FloatBand = DbcFile.Read(s);
        for (int i = 0; i < z.IntBand.RecordCount; i++) z._intById[z.IntBand.GetU(i, 0)] = i;
        for (int i = 0; i < z.FloatBand.RecordCount; i++) z._floatById[z.FloatBand.GetU(i, 0)] = i;
        return z;
    }

    public readonly record struct Zone(
        int RecIndex, uint Id, int MapId, int TileX, int TileY,
        float Falloff0, float Falloff1, uint Param, float AmbientLum, bool IsNight,
        int TileX0, int TileY0, int TileX1, int TileY1, bool IsGlobal)
    {
        public string TileSpan => IsGlobal ? "whole map"
            : TileX0 == TileX1 && TileY0 == TileY1 ? $"tile ({TileX0},{TileY0})"
            : $"tiles ({TileX0}–{TileX1}, {TileY0}–{TileY1})";
    }

    private const float DbcCoordScale = 36f;
    private const float TileSize = 533.33333f;

    public List<Zone> ListZones(int continentId)
    {
        var list = new List<Zone>();
        for (int i = 0; i < Light.RecordCount; i++)
        {
            if (Light.GetI(i, FMap) != continentId) continue;
            float x = Light.GetF(i, FX), zc = Light.GetF(i, FZ);
            int tx = (int)(x / DbcCoordScale / TileSize), ty = (int)(zc / DbcCoordScale / TileSize);
            uint param = Light.GetU(i, FParamClear);
            float f1 = Light.GetF(i, FFall1);
            bool global = f1 <= 1f;
            float rTiles = global ? 0f : f1 / DbcCoordScale / TileSize;
            int tx0 = Math.Clamp((int)(x / DbcCoordScale / TileSize - rTiles), 0, 63);
            int ty0 = Math.Clamp((int)(zc / DbcCoordScale / TileSize - rTiles), 0, 63);
            int tx1 = Math.Clamp((int)(x / DbcCoordScale / TileSize + rTiles), 0, 63);
            int ty1 = Math.Clamp((int)(zc / DbcCoordScale / TileSize + rTiles), 0, 63);
            list.Add(new Zone(i, Light.GetU(i, 0), continentId, tx, ty,
                Light.GetF(i, FFall0), f1, param,
                AmbientLuminance(param), param == DefaultNightParam,
                tx0, ty0, tx1, ty1, global));
        }
        return list;
    }

    private float AmbientLuminance(uint param)
    {
        if (param == 0) return 999f;
        if (!_intById.TryGetValue((param - 1) * IntBandsPerParam + 2, out int idx)) return 999f;
        uint num = IntBand.GetU(idx, 1);
        float max = 0f;
        for (int k = 0; k < (int)num && k < 16; k++)
        {
            uint v = IntBand.GetU(idx, 18 + k);
            max = Math.Max(max, 0.299f * ((v >> 16) & 0xFF) + 0.587f * ((v >> 8) & 0xFF) + 0.114f * (v & 0xFF));
        }
        return max;
    }

    public void MakeNight(int lightRecIndex, float darkness = 1f, int baseParam = DefaultNightParam)
    {
        int param = darkness >= 0.999f ? baseParam : EnsureDarkParam(baseParam, Math.Clamp(darkness, 0.05f, 1f));
        Light.SetU(lightRecIndex, FParamClear, (uint)param);
        Light.SetU(lightRecIndex, FParamClear + 2, (uint)param);
        LightDirty = true;
    }

    private int _darkParamId = -1;

    private int EnsureDarkParam(int baseParam, float darkness)
    {
        if (_darkParamId >= 0) return _darkParamId;

        uint maxP = 0;
        for (int i = 0; i < Params.RecordCount; i++) maxP = Math.Max(maxP, Params.GetU(i, 0));
        uint n = maxP + 1;

        int pIdx = Params.IndexOfId((uint)baseParam);
        if (pIdx < 0) return baseParam;
        int np = Params.AddRecord(Params.Records[pIdx]);
        Params.SetU(np, 0, n);

        for (int k = 1; k <= IntBandsPerParam; k++)
        {
            if (!_intById.TryGetValue((uint)((baseParam - 1) * IntBandsPerParam + k), out int src)) continue;
            int ni = IntBand.AddRecord(IntBand.Records[src]);
            IntBand.SetU(ni, 0, (uint)((n - 1) * IntBandsPerParam + k));
            for (int j = 0; j < 16; j++)
                IntBand.SetU(ni, 18 + j, ScaleColor(IntBand.GetU(ni, 18 + j), darkness));
        }
        for (int k = 1; k <= FloatBandsPerParam; k++)
        {
            if (!_floatById.TryGetValue((uint)((baseParam - 1) * FloatBandsPerParam + k), out int src)) continue;
            int nf = FloatBand.AddRecord(FloatBand.Records[src]);
            FloatBand.SetU(nf, 0, (uint)((n - 1) * FloatBandsPerParam + k));
        }

        _bandsDirty = true;
        _darkParamId = (int)n;
        return _darkParamId;
    }

    private static uint ScaleColor(uint v, float f)
    {
        uint b0 = (uint)Math.Clamp((int)((v & 0xFF) * f + 0.5f), 0, 255);
        uint b1 = (uint)Math.Clamp((int)(((v >> 8) & 0xFF) * f + 0.5f), 0, 255);
        uint b2 = (uint)Math.Clamp((int)(((v >> 16) & 0xFF) * f + 0.5f), 0, 255);
        return (v & 0xFF000000u) | (b2 << 16) | (b1 << 8) | b0;
    }

    public readonly record struct Sampled(Vector3 Ambient, Vector3 SkyTop, Vector3 FogColor, float FogEndYards);

    private const float MapHalf = 32f * TileSize;

    public Sampled SampleAt(int continent, float glX, float glZ, float time)
    {
        float lx = 36f * (glX + MapHalf), lz = 36f * (glZ + MapHalf);
        uint defParam = 0, zoneParam = 0;
        float bestW = 0f;
        for (int i = 0; i < Light.RecordCount; i++)
        {
            if (Light.GetI(i, FMap) != continent) continue;
            float f1 = Light.GetF(i, FFall1);
            uint p = Light.GetU(i, FParamClear);
            if (f1 <= 1f) { if (defParam == 0) defParam = p; continue; }
            float dx = Light.GetF(i, FX) - lx, dz = Light.GetF(i, FZ) - lz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist >= f1) continue;
            float f0 = Light.GetF(i, FFall0);
            float w = f1 > f0 ? Math.Clamp((f1 - dist) / (f1 - f0), 0f, 1f) : 1f;
            if (w > bestW) { bestW = w; zoneParam = p; }
        }
        if (defParam == 0) defParam = zoneParam != 0 ? zoneParam : (uint)DefaultNightParam;
        if (zoneParam == 0) zoneParam = defParam;

        Vector3 amb = Vector3.Lerp(IntBandColor(defParam, 2, time), IntBandColor(zoneParam, 2, time), bestW);
        Vector3 top = Vector3.Lerp(IntBandColor(defParam, 3, time), IntBandColor(zoneParam, 3, time), bestW);
        Vector3 fog = Vector3.Lerp(IntBandColor(defParam, 8, time), IntBandColor(zoneParam, 8, time), bestW);
        float fogD = Lerp(FloatBandValue(defParam, 1, time), FloatBandValue(zoneParam, 1, time), bestW) / 36f;
        return new Sampled(amb, top, fog, Math.Clamp(fogD, 150f, 5000f));
    }

    private Vector3 IntBandColor(uint param, int band, float time)
    {
        if (param == 0 || !_intById.TryGetValue((param - 1) * IntBandsPerParam + (uint)band, out int idx))
            return Vector3.Zero;
        uint num = IntBand.GetU(idx, 1);
        if (num == 0) return Vector3.Zero;
        if (time <= IntBand.GetU(idx, 2)) return Color(IntBand.GetU(idx, 18));
        for (int k = 1; k < (int)num && k < 16; k++)
        {
            uint t0 = IntBand.GetU(idx, 2 + k - 1), t1 = IntBand.GetU(idx, 2 + k);
            if (time <= t1)
            {
                float f = t1 > t0 ? (time - t0) / (t1 - t0) : 0f;
                return Vector3.Lerp(Color(IntBand.GetU(idx, 18 + k - 1)), Color(IntBand.GetU(idx, 18 + k)), f);
            }
        }
        return Color(IntBand.GetU(idx, 18 + (int)num - 1));
    }

    private float FloatBandValue(uint param, int band, float time)
    {
        if (param == 0 || !_floatById.TryGetValue((param - 1) * FloatBandsPerParam + (uint)band, out int idx))
            return 1000f;
        uint num = FloatBand.GetU(idx, 1);
        if (num == 0) return 1000f;
        if (time <= FloatBand.GetU(idx, 2)) return FloatBand.GetF(idx, 18);
        for (int k = 1; k < (int)num && k < 16; k++)
        {
            uint t0 = FloatBand.GetU(idx, 2 + k - 1), t1 = FloatBand.GetU(idx, 2 + k);
            if (time <= t1)
            {
                float f = t1 > t0 ? (time - t0) / (t1 - t0) : 0f;
                return Lerp(FloatBand.GetF(idx, 18 + k - 1), FloatBand.GetF(idx, 18 + k), f);
            }
        }
        return FloatBand.GetF(idx, 18 + (int)num - 1);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Vector3 Color(uint v) =>
        new(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);

    public Dictionary<string, byte[]> BuildPatchFiles()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (LightDirty) files[@"DBFilesClient\Light.dbc"] = Light.Write();
        if (_bandsDirty)
        {
            files[@"DBFilesClient\LightParams.dbc"] = Params.Write();
            files[@"DBFilesClient\LightIntBand.dbc"] = IntBand.Write();
            files[@"DBFilesClient\LightFloatBand.dbc"] = FloatBand.Write();
        }
        return files;
    }
}
