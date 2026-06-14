using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public static class AdtWriter
{
    private const float TileSize = 533.333333f;
    private const float ChunkSize = TileSize / 16f;
    private const float MapHalf = 32f * TileSize;

    private sealed class TopChunk { public string Magic = ""; public int MagicPos; public byte[] Data = []; }

    public static byte[] Build(byte[] original, AdtFile edited)
    {
        var orig = ParseTopLevel(original);

        TopChunk? origMhdr = orig.Find(c => c.Magic == "MHDR");
        TopChunk? origMcin = orig.Find(c => c.Magic == "MCIN");
        byte[] mhdrData = origMhdr?.Data is { Length: >= 64 } ? (byte[])origMhdr.Data.Clone() : new byte[64];
        int mhdrBase = (origMhdr != null && origMcin != null)
            ? origMcin.MagicPos - (int)RU32(origMhdr.Data, 4)
            : 20;

        bool subHdr = DetectSubChunkHeaders(orig);

        byte[] mtex = BuildMtex(edited);
        var (mmdx, mmid, mddf) = BuildDoodadChunks(edited);
        var (mwmo, mwid, modf) = BuildWmoChunks(edited);

        var final = new List<TopChunk>();
        void Add(string magic, byte[] data) => final.Add(new TopChunk { Magic = magic, Data = data });

        Add("MVER", orig.Find(c => c.Magic == "MVER")?.Data ?? BitConverter.GetBytes(18u));
        Add("MHDR", mhdrData);
        Add("MCIN", new byte[256 * 16]);
        Add("MTEX", mtex);
        Add("MMDX", mmdx); Add("MMID", mmid); Add("MDDF", mddf);
        Add("MWMO", mwmo); Add("MWID", mwid); Add("MODF", modf);

        var mcnkSlots = new List<int>();
        int slot = 0;
        foreach (var c in orig)
        {
            if (c.Magic != "MCNK") continue;
            int ix = (int)RU32(c.Data, 4), iy = (int)RU32(c.Data, 8);
            var chunk = (iy is >= 0 and < 16 && ix is >= 0 and < 16) ? edited.Chunks[iy, ix] : null;
            byte[] data;
            try { data = chunk != null ? RebuildMcnk(c.Data, chunk, edited, subHdr) : c.Data; }
            catch { data = c.Data; }
            Add("MCNK", data);
            mcnkSlots.Add(slot++);
        }

        var managed = new HashSet<string> { "MVER", "MHDR", "MCIN", "MTEX", "MMDX", "MMID", "MDDF", "MWMO", "MWID", "MODF", "MCNK" };
        foreach (var c in orig)
            if (!managed.Contains(c.Magic)) Add(c.Magic, c.Data);

        int pos = 0;
        foreach (var c in final) { c.MagicPos = pos; pos += 8 + c.Data.Length; }

        var mcin = final.Find(c => c.Magic == "MCIN")!;
        int k = 0;
        foreach (var c in final)
        {
            if (c.Magic != "MCNK") continue;
            if (k < 256)
            {
                WU32(mcin.Data, k * 16, (uint)c.MagicPos);
                WU32(mcin.Data, k * 16 + 4, (uint)(8 + c.Data.Length));
            }
            k++;
        }

        var mapped = new HashSet<string>();
        for (int f = 4; f <= 44; f += 4) WU32(mhdrData, f, 0);
        if (origMhdr != null)
            for (int f = 4; f <= 44; f += 4)
            {
                uint v = RU32(origMhdr.Data, f);
                if (v == 0) continue;
                var oc = orig.Find(c => c.MagicPos == mhdrBase + (int)v);
                if (oc == null) continue;
                var fc = final.Find(c => c.Magic == oc.Magic);
                if (fc != null) { WU32(mhdrData, f, (uint)(fc.MagicPos - mhdrBase)); mapped.Add(oc.Magic); }
            }
        foreach (var (magic, field) in MhdrFallback)
        {
            if (mapped.Contains(magic)) continue;
            var fc = final.Find(c => c.Magic == magic);
            if (fc != null && fc.Data.Length > 0) WU32(mhdrData, field, (uint)(fc.MagicPos - mhdrBase));
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var c in final)
        {
            WriteMagic(w, c.Magic);
            w.Write((uint)c.Data.Length);
            w.Write(c.Data);
        }
        return ms.ToArray();
    }

    public static byte[] BuildTemplate(AdtFile tile)
    {
        string baseTex = tile.Textures.Count > 0 ? tile.Textures[0] : @"Tileset\Barrens\BarrensBaseGrass.blp";
        var mtex = new byte[Encoding.UTF8.GetByteCount(baseTex) + 1];
        Encoding.UTF8.GetBytes(baseTex, 0, baseTex.Length, mtex, 0);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Chunk(string magic, byte[] data) { WriteMagic(w, magic); w.Write((uint)data.Length); w.Write(data); }

        Chunk("MVER", BitConverter.GetBytes(18u));
        long mhdrMagicPos = ms.Length;
        Chunk("MHDR", new byte[64]);
        long mhdrBase = mhdrMagicPos + 8;

        var ofs = new Dictionary<string, uint>();
        void Rec(string magic, byte[] data) { ofs[magic] = (uint)(ms.Length - mhdrBase); Chunk(magic, data); }
        Rec("MCIN", new byte[256 * 16]);
        Rec("MTEX", mtex);
        Rec("MMDX", []); Rec("MMID", []);
        Rec("MWMO", []); Rec("MWID", []);
        Rec("MDDF", []); Rec("MODF", []);

        for (int cy = 0; cy < 16; cy++)
        for (int cx = 0; cx < 16; cx++)
            Chunk("MCNK", BuildTemplateMcnk(tile.Chunks[cy, cx], cx, cy));

        var bytes = ms.ToArray();
        foreach (var (magic, field) in MhdrFallback)
            WU32(bytes, (int)mhdrBase + field, ofs[magic]);
        return bytes;
    }

    private static byte[] BuildTemplateMcnk(MapChunk? chunk, int cx, int cy)
    {
        var body = new MemoryStream();
        var offs = new Dictionary<string, int>();
        void Sub(string name, byte[] data, int declared = -1)
        {
            offs[name] = 128 + (int)body.Length + 8;
            WriteMagicBytes(body, name);
            WriteU32(body, (uint)(declared < 0 ? data.Length : declared));
            body.Write(data, 0, data.Length);
        }

        Sub("MCVT", new byte[MapChunk.HeightCount * 4]);
        var mcnr = new byte[MapChunk.HeightCount * 3 + 13];
        for (int i = 0; i < MapChunk.HeightCount; i++) mcnr[i * 3 + 2] = 127;
        Sub("MCNR", mcnr, MapChunk.HeightCount * 3);
        Sub("MCLY", new byte[16]);
        Sub("MCRF", []);
        Sub("MCAL", []);

        var outArr = new byte[128 + body.Length];
        WU32(outArr, 4, (uint)cx);
        WU32(outArr, 8, (uint)cy);
        WU32(outArr, 12, 1);
        foreach (var (name, o) in offs) WU32(outArr, SubOffField[name], (uint)o);
        var pos = chunk?.Position ?? default;
        WriteF(outArr, 104, pos.X); WriteF(outArr, 108, pos.Y); WriteF(outArr, 112, pos.Z);
        body.ToArray().CopyTo(outArr, 128);
        return outArr;
    }

    private static readonly (string Magic, int Field)[] MhdrFallback =
    {
        ("MCIN", 4), ("MTEX", 8), ("MMDX", 12), ("MMID", 16),
        ("MWMO", 20), ("MWID", 24), ("MDDF", 28), ("MODF", 32),
    };

    private static readonly string[] SubOrder = { "MCVT", "MCNR", "MCLY", "MCRF", "MCAL", "MCSH", "MCSE", "MCLQ" };
    private static readonly Dictionary<string, int> SubOffField = new()
    {
        ["MCVT"] = 20, ["MCNR"] = 24, ["MCLY"] = 28, ["MCRF"] = 32,
        ["MCAL"] = 36, ["MCSH"] = 44, ["MCSE"] = 88, ["MCLQ"] = 96,
    };

    private static byte[] RebuildMcnk(byte[] m, MapChunk chunk, AdtFile edited, bool subHdr)
    {
        var doodadRefs = AssignRefs(chunk, edited, wmo: false);
        var wmoRefs = AssignRefs(chunk, edited, wmo: true);

        var present = new List<(string Name, int Off)>();
        foreach (var name in SubOrder)
        {
            int off = (int)RU32(m, SubOffField[name]);
            if (off > 0) present.Add((name, off));
        }
        present.Sort((a, b) => a.Off.CompareTo(b.Off));

        int hdr = subHdr ? 8 : 0;
        var origRegion = new Dictionary<string, byte[]>();
        for (int i = 0; i < present.Count; i++)
        {
            int start = present[i].Off - hdr;
            int end = i + 1 < present.Count ? present[i + 1].Off - hdr : m.Length;
            start = Math.Clamp(start, 0, m.Length);
            end = Math.Clamp(end, start, m.Length);
            origRegion[present[i].Name] = m[start..end];
        }

        var gen = new Dictionary<string, byte[]>
        {
            ["MCVT"] = GenMcvt(chunk),
            ["MCLY"] = GenMcly(chunk),
            ["MCAL"] = GenMcal(chunk),
            ["MCRF"] = GenMcrf(doodadRefs, wmoRefs),
        };

        bool heightsChanged = HeightsChanged(m, chunk);

        if (origRegion.TryGetValue("MCNR", out var origMcnr) && heightsChanged)
        {
            var data = StripHeader(origMcnr, hdr);
            byte[] tail = data.Length > MapChunk.HeightCount * 3 ? data[(MapChunk.HeightCount * 3)..] : [];
            gen["MCNR"] = GenMcnr(chunk, tail);
        }

        bool shadowChanged = ShadowChanged(m, chunk);
        if (shadowChanged) gen["MCSH"] = GenMcsh(chunk);
        bool clearMcsh = !shadowChanged && origRegion.ContainsKey("MCSH") && heightsChanged;

        bool liquidChanged = LiquidChanged(m, chunk);
        if (liquidChanged) gen["MCLQ"] = GenMclq(chunk);

        var order = new List<string>();
        foreach (var (name, _) in present) order.Add(name);
        foreach (var need in new[] { "MCLY", "MCAL", "MCRF" })
            if (!order.Contains(need) && gen[need].Length > 0)
                order.Insert(InsertIndex(order, need), need);
        if (clearMcsh) order.Remove("MCSH");
        if (shadowChanged)
        {
            if (gen["MCSH"].Length > 0) { if (!order.Contains("MCSH")) order.Insert(InsertIndex(order, "MCSH"), "MCSH"); }
            else order.Remove("MCSH");
        }
        if (liquidChanged)
        {
            if (gen["MCLQ"].Length > 0) { if (!order.Contains("MCLQ")) order.Insert(InsertIndex(order, "MCLQ"), "MCLQ"); }
            else order.Remove("MCLQ");
        }

        using var ms = new MemoryStream();
        var header = (byte[])m[..128].Clone();
        ms.Write(header, 0, 128);

        var newOff = new Dictionary<string, int>();
        var newLen = new Dictionary<string, int>();
        foreach (var name in order)
        {
            byte[] data = gen.TryGetValue(name, out var g) ? g
                        : origRegion.TryGetValue(name, out var o) ? StripHeader(o, hdr) : [];
            int regionStart = (int)ms.Length;
            if (subHdr) { WriteMagicBytes(ms, name); WriteU32(ms, (uint)data.Length); }
            newOff[name] = subHdr ? regionStart + 8 : regionStart;
            newLen[name] = data.Length;
            ms.Write(data, 0, data.Length);
        }

        var outArr = ms.ToArray();

        WU32(outArr, 12, (uint)chunk.NLayers);
        WU32(outArr, 16, (uint)doodadRefs.Count);
        WU32(outArr, 56, (uint)wmoRefs.Count);
        WU16(outArr, 60, chunk.Holes);
        WU32(outArr, 52, (uint)chunk.AreaId);
        foreach (var name in SubOrder)
            WU32(outArr, SubOffField[name], newOff.TryGetValue(name, out var o) ? (uint)o : 0u);
        WU32(outArr, 40, newLen.TryGetValue("MCAL", out var al) ? (uint)al : 0u);
        if (shadowChanged)
        {
            bool hasShadow = gen["MCSH"].Length > 0;
            WU32(outArr, 48, !hasShadow ? 0u : RU32(m, 48) is var os && os >= 512 ? os : (uint)gen["MCSH"].Length);
            uint fl0 = RU32(outArr, 0);
            WU32(outArr, 0, hasShadow ? fl0 | 0x1u : fl0 & ~0x1u);
        }
        else if (clearMcsh)
        {
            WU32(outArr, 48, 0);
            WU32(outArr, 0, RU32(outArr, 0) & ~0x1u);
        }
        else WU32(outArr, 48, RU32(m, 48));
        if (liquidChanged)
        {
            WU32(outArr, 100, gen["MCLQ"].Length > 0 ? (uint)(gen["MCLQ"].Length + 8) : 0u);
            uint fl = RU32(outArr, 0) & ~0x3Cu;
            if (chunk.Liquid is { } lq)
                fl |= lq.Type switch { 2 => 0x08u, 3 => 0x10u, 4 => 0x20u, _ => 0x04u };
            WU32(outArr, 0, fl);
        }
        else WU32(outArr, 100, RU32(m, 100));
        return outArr;
    }

    private static int InsertIndex(List<string> order, string name)
    {
        int rank = Array.IndexOf(SubOrder, name);
        for (int i = 0; i < order.Count; i++)
            if (Array.IndexOf(SubOrder, order[i]) > rank) return i;
        return order.Count;
    }

    private static byte[] StripHeader(byte[] region, int hdr) =>
        region.Length >= hdr ? region[hdr..] : [];

    private static List<int> AssignRefs(MapChunk chunk, AdtFile edited, bool wmo)
    {
        var refs = new List<int>();
        float minX = -chunk.Position.Y, maxX = minX + ChunkSize;
        float minZ = -chunk.Position.X, maxZ = minZ + ChunkSize;
        int count = wmo ? edited.Wmos.Count : edited.Doodads.Count;
        for (int i = 0; i < count; i++)
        {
            var p = wmo ? edited.Wmos[i].Position : edited.Doodads[i].Position;
            float gx = p.X - MapHalf, gz = p.Z - MapHalf;
            if (gx >= minX && gx < maxX && gz >= minZ && gz < maxZ) refs.Add(i);
        }
        return refs;
    }

    private static byte[] GenMcvt(MapChunk chunk)
    {
        var b = new byte[MapChunk.HeightCount * 4];
        for (int i = 0; i < MapChunk.HeightCount; i++) WriteF(b, i * 4, chunk.Heights[i]);
        return b;
    }

    private static bool ShadowChanged(byte[] m, MapChunk chunk)
    {
        uint flags = RU32(m, 0);
        int off = (int)RU32(m, 44);
        uint size = RU32(m, 48);
        byte[]? orig = null;
        if (off > 0 && size >= 512 && (flags & 0x1) != 0 && off + 512 <= m.Length)
        {
            var dst = new byte[4096];
            bool any = false;
            for (int row = 0; row < 64; row++)
            for (int col = 0; col < 64; col++)
                if (((m[off + row * 8 + (col >> 3)] >> (col & 7)) & 1) != 0) { dst[row * 64 + col] = 255; any = true; }
            if (any)
            {
                for (int y = 0; y < 64; y++) dst[y * 64 + 63] = dst[y * 64 + 62];
                for (int x = 0; x < 64; x++) dst[63 * 64 + x] = dst[62 * 64 + x];
                orig = dst;
            }
        }

        var cur = chunk.ShadowMap;
        if (cur == null) return orig != null;
        if (orig == null) return true;
        for (int i = 0; i < 4096; i++)
            if ((cur[i] >= 128) != (orig[i] >= 128)) return true;
        return false;
    }

    private static byte[] GenMcsh(MapChunk chunk)
    {
        var map = chunk.ShadowMap;
        if (map == null) return [];
        var b = new byte[512];
        bool any = false;
        for (int row = 0; row < 64; row++)
        for (int col = 0; col < 64; col++)
            if (map[row * 64 + col] >= 128) { b[row * 8 + (col >> 3)] |= (byte)(1 << (col & 7)); any = true; }
        return any ? b : [];
    }

    private static bool LiquidChanged(byte[] m, MapChunk chunk)
    {
        uint flags = RU32(m, 0);
        int off = (int)RU32(m, 96);
        uint size = RU32(m, 100);
        bool origHas = off > 0 && size >= 720 && (flags & 0x3Cu) != 0 && off + 720 <= m.Length;

        if (chunk.Liquid == null) return origHas;
        if (!origHas) return true;

        int origType = (flags & 0x04) != 0 ? 1 : (flags & 0x08) != 0 ? 2 : (flags & 0x10) != 0 ? 3 : 4;
        if (origType != chunk.Liquid.Type) return true;
        for (int i = 0; i < 81; i++)
            if (BitConverter.ToSingle(m, off + 8 + i * 8 + 4) != chunk.Liquid.Heights[i]) return true;
        for (int i = 0; i < 64; i++)
            if (((m[off + 8 + 648 + i] & 0x0F) != 0x0F) != chunk.Liquid.Render[i]) return true;
        return false;
    }

    private static byte[] GenMclq(MapChunk chunk)
    {
        var lq = chunk.Liquid;
        if (lq == null) return [];

        var b = new byte[8 + 81 * 8 + 64 + 4 + 2 * 40];
        WriteF(b, 0, lq.MinHeight);
        WriteF(b, 4, lq.MaxHeight);

        for (int row = 0; row < 9; row++)
        for (int col = 0; col < 9; col++)
        {
            int i = row * 9 + col;
            int o = 8 + i * 8;
            float ground = chunk.Position.Z + chunk.Heights[row * 17 + col];
            b[o] = (byte)Math.Clamp((int)MathF.Round((lq.Heights[i] - ground) * 8f), 0, 255);
            WriteF(b, o + 4, lq.Heights[i]);
        }

        byte typeNibble = lq.Type switch { 2 => 0x41, 3 => 0x46, 4 => 0x43, _ => 0x44 };
        for (int i = 0; i < 64; i++)
            b[8 + 648 + i] = lq.Render[i] ? typeNibble : (byte)0x0F;

        return b;
    }

    private static bool HeightsChanged(byte[] m, MapChunk chunk)
    {
        int off = (int)RU32(m, SubOffField["MCVT"]);
        if (off <= 0 || off + MapChunk.HeightCount * 4 > m.Length) return true;
        for (int i = 0; i < MapChunk.HeightCount; i++)
            if (BitConverter.ToSingle(m, off + i * 4) != chunk.Heights[i]) return true;
        return false;
    }

    private static byte[] GenMcnr(MapChunk chunk, byte[] tail)
    {
        var n = TerrainNormal.Compute(chunk);
        var b = new byte[MapChunk.HeightCount * 3 + tail.Length];
        for (int i = 0; i < MapChunk.HeightCount; i++)
        {
            b[i * 3 + 0] = EncN(n[i].X);
            b[i * 3 + 1] = EncN(n[i].Y);
            b[i * 3 + 2] = EncN(n[i].Z);
        }
        if (tail.Length > 0) Array.Copy(tail, 0, b, MapChunk.HeightCount * 3, tail.Length);
        return b;
    }

    private static byte EncN(float v) =>
        unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(Math.Clamp(v, -1f, 1f) * 127f), -127, 127));

    private static byte[] GenMcly(MapChunk chunk)
    {
        int n = Math.Clamp(chunk.NLayers, 0, MapChunk.MaxLayers);
        var b = new byte[n * 16];
        for (int l = 0; l < n; l++)
        {
            uint flags = chunk.Layers[l].Flags;
            if (l == 0) flags &= ~0x300u;
            else flags = (flags & ~0x200u) | 0x100u;
            WU32(b, l * 16, chunk.Layers[l].TextureIndex);
            WU32(b, l * 16 + 4, flags);
            WU32(b, l * 16 + 8, (uint)(l == 0 ? 0 : (l - 1) * 2048));
            WU32(b, l * 16 + 12, (uint)chunk.Layers[l].EffectId);
        }
        return b;
    }

    private static byte[] GenMcal(MapChunk chunk)
    {
        int n = Math.Clamp(chunk.NLayers, 0, MapChunk.MaxLayers);
        if (n <= 1) return [];
        var b = new byte[(n - 1) * 2048];
        for (int l = 1; l < n; l++)
        {
            var map = chunk.AlphaMaps[l - 1];
            int dst = (l - 1) * 2048;
            for (int i = 0; i < 2048; i++)
            {
                int v0 = map != null ? map[i * 2] : 0;
                int v1 = map != null ? map[i * 2 + 1] : 0;
                int lo = Math.Min(15, (v0 + 8) / 17);
                int hi = Math.Min(15, (v1 + 8) / 17);
                b[dst + i] = (byte)(lo | (hi << 4));
            }
        }
        return b;
    }

    private static byte[] GenMcrf(List<int> doodadRefs, List<int> wmoRefs)
    {
        var b = new byte[(doodadRefs.Count + wmoRefs.Count) * 4];
        int o = 0;
        foreach (int d in doodadRefs) { WU32(b, o, (uint)d); o += 4; }
        foreach (int w in wmoRefs) { WU32(b, o, (uint)w); o += 4; }
        return b;
    }

    private static byte[] BuildMtex(AdtFile edited)
    {
        using var ms = new MemoryStream();
        foreach (var t in edited.Textures) { var s = Encoding.ASCII.GetBytes(t); ms.Write(s); ms.WriteByte(0); }
        return ms.ToArray();
    }

    private static (byte[] mmdx, byte[] mmid, byte[] mddf) BuildDoodadChunks(AdtFile edited)
    {
        var paths = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in edited.Doodads)
        {
            if (string.IsNullOrEmpty(d.ModelPath) || index.ContainsKey(d.ModelPath)) continue;
            index[d.ModelPath] = paths.Count; paths.Add(d.ModelPath);
        }

        using var mmdxMs = new MemoryStream();
        var offsets = new List<uint>();
        foreach (var p in paths) { offsets.Add((uint)mmdxMs.Length); var s = Encoding.ASCII.GetBytes(p); mmdxMs.Write(s); mmdxMs.WriteByte(0); }

        var mmid = new byte[offsets.Count * 4];
        for (int i = 0; i < offsets.Count; i++) WU32(mmid, i * 4, offsets[i]);

        using var mddfMs = new MemoryStream();
        using (var w = new BinaryWriter(mddfMs, Encoding.UTF8, leaveOpen: true))
        foreach (var d in edited.Doodads)
        {
            int nameId = !string.IsNullOrEmpty(d.ModelPath) && index.TryGetValue(d.ModelPath, out var id) ? id : 0;
            w.Write((uint)nameId);
            w.Write(d.UniqueId);
            w.Write(d.Position.X); w.Write(d.Position.Y); w.Write(d.Position.Z);
            w.Write(d.Rotation.X); w.Write(d.Rotation.Y); w.Write(d.Rotation.Z);
            w.Write((ushort)Math.Clamp((int)MathF.Round(d.Scale * 1024f), 0, 65535));
            w.Write(d.Flags);
        }
        return (mmdxMs.ToArray(), mmid, mddfMs.ToArray());
    }

    private static (byte[] mwmo, byte[] mwid, byte[] modf) BuildWmoChunks(AdtFile edited)
    {
        var paths = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var wmo in edited.Wmos)
        {
            if (string.IsNullOrEmpty(wmo.ModelPath) || index.ContainsKey(wmo.ModelPath)) continue;
            index[wmo.ModelPath] = paths.Count; paths.Add(wmo.ModelPath);
        }

        using var mwmoMs = new MemoryStream();
        var offsets = new List<uint>();
        foreach (var p in paths) { offsets.Add((uint)mwmoMs.Length); var s = Encoding.ASCII.GetBytes(p); mwmoMs.Write(s); mwmoMs.WriteByte(0); }

        var mwid = new byte[offsets.Count * 4];
        for (int i = 0; i < offsets.Count; i++) WU32(mwid, i * 4, offsets[i]);

        using var modfMs = new MemoryStream();
        using (var w = new BinaryWriter(modfMs, Encoding.UTF8, leaveOpen: true))
        foreach (var wmo in edited.Wmos)
        {
            int nameId = !string.IsNullOrEmpty(wmo.ModelPath) && index.TryGetValue(wmo.ModelPath, out var id) ? id : 0;
            w.Write((uint)nameId);
            w.Write(wmo.UniqueId);
            w.Write(wmo.Position.X); w.Write(wmo.Position.Y); w.Write(wmo.Position.Z);
            w.Write(wmo.Rotation.X); w.Write(wmo.Rotation.Y); w.Write(wmo.Rotation.Z);
            w.Write(wmo.Position.X - 500f); w.Write(wmo.Position.Y - 500f); w.Write(wmo.Position.Z - 500f);
            w.Write(wmo.Position.X + 500f); w.Write(wmo.Position.Y + 500f); w.Write(wmo.Position.Z + 500f);
            w.Write(wmo.Flags);
            w.Write(wmo.DoodadSet);
            w.Write((ushort)0);
            w.Write((ushort)0);
        }
        return (mwmoMs.ToArray(), mwid, modfMs.ToArray());
    }

    private static List<TopChunk> ParseTopLevel(byte[] data)
    {
        var list = new List<TopChunk>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            var magic = new[] { data[pos + 3], data[pos + 2], data[pos + 1], data[pos] };
            string m = Encoding.ASCII.GetString(magic);
            uint size = RU32(data, pos + 4);
            int dataStart = pos + 8;
            if (dataStart + (long)size > data.Length) break;
            list.Add(new TopChunk { Magic = m, MagicPos = pos, Data = data[dataStart..(dataStart + (int)size)] });
            pos = dataStart + (int)size;
        }
        return list;
    }

    private static bool DetectSubChunkHeaders(List<TopChunk> orig)
    {
        var mcnk = orig.Find(c => c.Magic == "MCNK");
        if (mcnk == null) return true;
        int offMcvt = (int)RU32(mcnk.Data, 20);
        if (offMcvt < 8 || offMcvt > mcnk.Data.Length) return true;
        int h = offMcvt - 8;
        return mcnk.Data[h] == (byte)'T' && mcnk.Data[h + 1] == (byte)'V' &&
               mcnk.Data[h + 2] == (byte)'C' && mcnk.Data[h + 3] == (byte)'M';
    }

    private static uint RU32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    private static void WU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    private static void WU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WriteF(byte[] b, int o, float v) { var bytes = BitConverter.GetBytes(v); b[o] = bytes[0]; b[o + 1] = bytes[1]; b[o + 2] = bytes[2]; b[o + 3] = bytes[3]; }
    private static void WriteU32(MemoryStream ms, uint v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 24)); }
    private static void WriteMagicBytes(MemoryStream ms, string magic) { ms.WriteByte((byte)magic[3]); ms.WriteByte((byte)magic[2]); ms.WriteByte((byte)magic[1]); ms.WriteByte((byte)magic[0]); }
    private static void WriteMagic(BinaryWriter w, string magic) { w.Write((byte)magic[3]); w.Write((byte)magic[2]); w.Write((byte)magic[1]); w.Write((byte)magic[0]); }
}
