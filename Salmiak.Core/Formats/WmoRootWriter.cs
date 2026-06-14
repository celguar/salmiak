using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public static class WmoRootWriter
{
    public readonly record struct DoodadPlacement(
        string Path, float PosX, float PosY, float PosZ,
        float RotX, float RotY, float RotZ, float RotW, float Scale, uint Color = 0xFFFFFFFF);

    private sealed class Chunk { public string Magic = ""; public byte[] Data = []; }

    private const int MohdNDoodadNames = 16, MohdNDoodadDefs = 20, MohdNDoodadSets = 24;
    private const int ModdStride = 40, ModsStride = 32, ModsStart = 20, ModsCount = 24;

    public static byte[]? AddDoodads(byte[] original, IReadOnlyList<DoodadPlacement> doodads, int targetSet = 0)
        => AddDoodads(original, doodads, targetSet, out _);

    public static byte[]? AddDoodads(byte[] original, IReadOnlyList<DoodadPlacement> doodads, int targetSet, out int insertIndex)
    {
        insertIndex = 0;
        if (doodads.Count == 0) return null;
        var chunks = Parse(original);

        Chunk? mohd = chunks.Find(c => c.Magic == "MOHD");
        Chunk? modn = chunks.Find(c => c.Magic == "MODN");
        Chunk? modd = chunks.Find(c => c.Magic == "MODD");
        Chunk? mods = chunks.Find(c => c.Magic == "MODS");

        byte[] origNames = modn?.Data ?? [];
        byte[] origDefs = modd?.Data ?? [];
        int defCount = origDefs.Length / ModdStride;

        int modsSetCount = mods is { Data.Length: >= ModsStride } ? mods.Data.Length / ModsStride : 0;
        int ts = modsSetCount > 0 ? Math.Clamp(targetSet, 0, modsSetCount - 1) : 0;
        int insertAt = defCount;
        if (modsSetCount > 0)
            insertAt = (int)RU32(mods!.Data, ts * ModsStride + ModsStart) + (int)RU32(mods.Data, ts * ModsStride + ModsCount);
        insertAt = Math.Clamp(insertAt, 0, defCount);
        insertIndex = insertAt;

        var nameOffsets = IndexNames(origNames);
        using var extraNames = new MemoryStream();
        var newDefs = new byte[doodads.Count * ModdStride];
        int appendedNames = 0;
        for (int i = 0; i < doodads.Count; i++)
        {
            var d = doodads[i];
            string path = d.Path ?? "";
            if (!nameOffsets.TryGetValue(path, out int ofs))
            {
                ofs = origNames.Length + (int)extraNames.Length;
                var bytes = Encoding.ASCII.GetBytes(path);
                extraNames.Write(bytes, 0, bytes.Length);
                extraNames.WriteByte(0);
                nameOffsets[path] = ofs;
                appendedNames++;
            }
            int o = i * ModdStride;
            WU32(newDefs, o, (uint)ofs & 0xFFFFFFu);
            WF(newDefs, o + 4, d.PosX); WF(newDefs, o + 8, d.PosY); WF(newDefs, o + 12, d.PosZ);
            WF(newDefs, o + 16, d.RotX); WF(newDefs, o + 20, d.RotY); WF(newDefs, o + 24, d.RotZ); WF(newDefs, o + 28, d.RotW);
            WF(newDefs, o + 32, d.Scale <= 0f ? 1f : d.Scale);
            WU32(newDefs, o + 36, d.Color == 0 ? 0xFFFFFFFFu : d.Color);
        }

        byte[] newModd = new byte[origDefs.Length + newDefs.Length];
        int cut = insertAt * ModdStride;
        Buffer.BlockCopy(origDefs, 0, newModd, 0, cut);
        Buffer.BlockCopy(newDefs, 0, newModd, cut, newDefs.Length);
        Buffer.BlockCopy(origDefs, cut, newModd, cut + newDefs.Length, origDefs.Length - cut);

        byte[] newModn = new byte[origNames.Length + (int)extraNames.Length];
        Buffer.BlockCopy(origNames, 0, newModn, 0, origNames.Length);
        extraNames.Position = 0;
        extraNames.Read(newModn, origNames.Length, (int)extraNames.Length);

        int k = doodads.Count;
        byte[] newMods;
        int setCount;
        if (modsSetCount > 0)
        {
            newMods = (byte[])mods!.Data.Clone();
            setCount = modsSetCount;
            WU32(newMods, ts * ModsStride + ModsCount, RU32(newMods, ts * ModsStride + ModsCount) + (uint)k);
            for (int s = 0; s < setCount; s++)
            {
                if (s == ts) continue;
                int baseOff = s * ModsStride;
                if (RU32(newMods, baseOff + ModsStart) >= insertAt)
                    WU32(newMods, baseOff + ModsStart, RU32(newMods, baseOff + ModsStart) + (uint)k);
            }
        }
        else
        {
            newMods = new byte[ModsStride];
            Encoding.ASCII.GetBytes("Default").CopyTo(newMods, 0);
            WU32(newMods, ModsStart, 0);
            WU32(newMods, ModsCount, (uint)(defCount + k));
            setCount = 1;
        }

        if (mohd is { Data.Length: >= 28 })
        {
            WU32(mohd.Data, MohdNDoodadNames, RU32(mohd.Data, MohdNDoodadNames) + (uint)appendedNames);
            WU32(mohd.Data, MohdNDoodadDefs, (uint)(defCount + k));
            WU32(mohd.Data, MohdNDoodadSets, (uint)setCount);
        }

        if (modd != null) modd.Data = newModd; else chunks.Add(new Chunk { Magic = "MODD", Data = newModd });
        if (modn != null) modn.Data = newModn; else InsertBefore(chunks, "MODN", newModn, "MODD");
        if (mods != null) mods.Data = newMods; else InsertBefore(chunks, "MODS", newMods, modn != null ? "MODN" : "MODD");

        return Serialize(chunks);
    }

    public static byte[]? AddDoodadSet(byte[] original, string name)
    {
        var chunks = Parse(original);
        Chunk? mohd = chunks.Find(c => c.Magic == "MOHD");
        Chunk? modd = chunks.Find(c => c.Magic == "MODD");
        Chunk? mods = chunks.Find(c => c.Magic == "MODS");
        int defCount = (modd?.Data.Length ?? 0) / ModdStride;

        byte[] Entry(string nm, uint start, uint count)
        {
            var e = new byte[ModsStride];
            var nb = Encoding.ASCII.GetBytes(nm ?? "");
            Array.Copy(nb, e, Math.Min(nb.Length, 19));
            WU32(e, ModsStart, start);
            WU32(e, ModsCount, count);
            return e;
        }

        var newEntry = Entry(name ?? "", (uint)defCount, 0);
        int setCount;
        if (mods is { Data.Length: >= ModsStride })
        {
            var grown = new byte[mods.Data.Length + ModsStride];
            Buffer.BlockCopy(mods.Data, 0, grown, 0, mods.Data.Length);
            Buffer.BlockCopy(newEntry, 0, grown, mods.Data.Length, ModsStride);
            mods.Data = grown;
            setCount = grown.Length / ModsStride;
        }
        else
        {
            var data = new byte[ModsStride * 2];
            Buffer.BlockCopy(Entry("Global", 0, (uint)defCount), 0, data, 0, ModsStride);
            Buffer.BlockCopy(newEntry, 0, data, ModsStride, ModsStride);
            if (mods != null) mods.Data = data;
            else InsertBefore(chunks, "MODS", data, chunks.Exists(c => c.Magic == "MODN") ? "MODN" : "MODD");
            setCount = 2;
        }

        if (mohd is { Data.Length: >= 28 }) WU32(mohd.Data, MohdNDoodadSets, (uint)setCount);
        return Serialize(chunks);
    }

    private static Dictionary<string, int> IndexNames(byte[] blob)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int start = 0;
        for (int i = 0; i < blob.Length; i++)
        {
            if (blob[i] != 0) continue;
            if (i > start)
            {
                string s = Encoding.ASCII.GetString(blob, start, i - start);
                if (!map.ContainsKey(s)) map[s] = start;
            }
            start = i + 1;
        }
        return map;
    }

    private static void InsertBefore(List<Chunk> chunks, string magic, byte[] data, string beforeMagic)
    {
        int idx = chunks.FindIndex(c => c.Magic == beforeMagic);
        var chunk = new Chunk { Magic = magic, Data = data };
        if (idx < 0) chunks.Add(chunk); else chunks.Insert(idx, chunk);
    }

    private static List<Chunk> Parse(byte[] data)
    {
        var list = new List<Chunk>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            string m = Encoding.ASCII.GetString(new[] { data[pos + 3], data[pos + 2], data[pos + 1], data[pos] });
            uint size = RU32(data, pos + 4);
            int dataStart = pos + 8;
            if (dataStart + (long)size > data.Length) break;
            list.Add(new Chunk { Magic = m, Data = data[dataStart..(dataStart + (int)size)] });
            pos = dataStart + (int)size;
        }
        return list;
    }

    private static byte[] Serialize(List<Chunk> chunks)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var c in chunks)
        {
            w.Write((byte)c.Magic[3]); w.Write((byte)c.Magic[2]); w.Write((byte)c.Magic[1]); w.Write((byte)c.Magic[0]);
            w.Write((uint)c.Data.Length);
            w.Write(c.Data);
        }
        return ms.ToArray();
    }

    private static uint RU32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    private static void WU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    private static void WF(byte[] b, int o, float v) { var x = BitConverter.GetBytes(v); b[o] = x[0]; b[o + 1] = x[1]; b[o + 2] = x[2]; b[o + 3] = x[3]; }
}
