using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Salmiak.Core.Formats;

public static class WmoGroupWriter
{
    private static readonly string[] SubOrder =
        { "MOPY", "MOVI", "MOVT", "MONR", "MOTV", "MOBA", "MOLR", "MODR", "MOBN", "MOBR", "MOCV", "MLIQ" };

    private const uint FlagHasDoodads = 0x800;
    private const int MogpHeaderSize = 68;
    private const int FlagsOfs = 8, BoxOfs = 12;

    public static (uint Flags, Vector3 BoxMin, Vector3 BoxMax)? ReadInfo(byte[] group)
    {
        int pos = FindMogp(group);
        if (pos < 0 || pos + MogpHeaderSize > group.Length) return null;
        uint flags = BitConverter.ToUInt32(group, pos + FlagsOfs);
        var mn = new Vector3(BitConverter.ToSingle(group, pos + BoxOfs),
                             BitConverter.ToSingle(group, pos + BoxOfs + 4),
                             BitConverter.ToSingle(group, pos + BoxOfs + 8));
        var mx = new Vector3(BitConverter.ToSingle(group, pos + BoxOfs + 12),
                             BitConverter.ToSingle(group, pos + BoxOfs + 16),
                             BitConverter.ToSingle(group, pos + BoxOfs + 20));
        return (flags, mn, mx);
    }

    public static byte[]? PatchDoodadRefs(byte[] group, int shiftFrom, int shiftBy, IReadOnlyList<ushort> addRefs)
    {
        int mogpData = FindMogp(group);
        if (mogpData < 0) return null;
        int mogpSize = (int)BitConverter.ToUInt32(group, mogpData - 4);
        int mogpEnd = Math.Min(mogpData + mogpSize, group.Length);

        var subs = new List<(string Magic, byte[] Data)>();
        int p = mogpData + MogpHeaderSize;
        while (p + 8 <= mogpEnd)
        {
            string m = Encoding.ASCII.GetString(new[] { group[p + 3], group[p + 2], group[p + 1], group[p] });
            int sz = (int)BitConverter.ToUInt32(group, p + 4);
            if (p + 8 + sz > mogpEnd) break;
            subs.Add((m, group[(p + 8)..(p + 8 + sz)]));
            p += 8 + sz;
        }

        int modr = subs.FindIndex(s => s.Magic == "MODR");
        bool changed = false;

        if (modr >= 0 && shiftBy != 0)
        {
            var data = subs[modr].Data;
            for (int i = 0; i + 2 <= data.Length; i += 2)
            {
                ushort v = BitConverter.ToUInt16(data, i);
                if (v >= shiftFrom)
                {
                    ushort nv = (ushort)(v + shiftBy);
                    data[i] = (byte)nv; data[i + 1] = (byte)(nv >> 8);
                    changed = true;
                }
            }
        }

        bool createdModr = false;
        if (addRefs is { Count: > 0 })
        {
            byte[] extra = new byte[addRefs.Count * 2];
            for (int i = 0; i < addRefs.Count; i++) { extra[i * 2] = (byte)addRefs[i]; extra[i * 2 + 1] = (byte)(addRefs[i] >> 8); }
            if (modr >= 0)
            {
                var merged = new byte[subs[modr].Data.Length + extra.Length];
                Buffer.BlockCopy(subs[modr].Data, 0, merged, 0, subs[modr].Data.Length);
                Buffer.BlockCopy(extra, 0, merged, subs[modr].Data.Length, extra.Length);
                subs[modr] = ("MODR", merged);
            }
            else
            {
                int rank = Array.IndexOf(SubOrder, "MODR");
                int at = subs.Count;
                for (int i = 0; i < subs.Count; i++)
                    if (Array.IndexOf(SubOrder, subs[i].Magic) > rank) { at = i; break; }
                subs.Insert(at, ("MODR", extra));
                createdModr = true;
            }
            changed = true;
        }

        if (!changed) return null;

        using var ms = new MemoryStream();
        ms.Write(group, 0, mogpData);
        var header = group[mogpData..(mogpData + MogpHeaderSize)];
        if (createdModr)
        {
            uint fl = BitConverter.ToUInt32(header, FlagsOfs) | FlagHasDoodads;
            header[FlagsOfs] = (byte)fl; header[FlagsOfs + 1] = (byte)(fl >> 8);
            header[FlagsOfs + 2] = (byte)(fl >> 16); header[FlagsOfs + 3] = (byte)(fl >> 24);
        }
        ms.Write(header, 0, header.Length);
        foreach (var (magic, data) in subs)
        {
            ms.WriteByte((byte)magic[3]); ms.WriteByte((byte)magic[2]); ms.WriteByte((byte)magic[1]); ms.WriteByte((byte)magic[0]);
            var sz = BitConverter.GetBytes((uint)data.Length); ms.Write(sz, 0, 4);
            ms.Write(data, 0, data.Length);
        }
        if (mogpEnd < group.Length) ms.Write(group, mogpEnd, group.Length - mogpEnd);

        var outBytes = ms.ToArray();
        int newSize = (int)(outBytes.Length - mogpData - (group.Length - mogpEnd));
        var szb = BitConverter.GetBytes((uint)newSize);
        Buffer.BlockCopy(szb, 0, outBytes, mogpData - 4, 4);
        return outBytes;
    }

    private static int FindMogp(byte[] file)
    {
        int pos = 0;
        while (pos + 8 <= file.Length)
        {
            string m = Encoding.ASCII.GetString(new[] { file[pos + 3], file[pos + 2], file[pos + 1], file[pos] });
            uint size = BitConverter.ToUInt32(file, pos + 4);
            if (m == "MOGP") return pos + 8;
            pos += 8 + (int)size;
        }
        return -1;
    }
}
