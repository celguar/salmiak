using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Salmiak.Core.IO;

public static class MpqArchiveWriter
{
    private const uint FlagExists   = 0x80000000;
    private const uint FlagCompress = 0x00000200;
    private const ushort SectorShift = 3;
    private const int SectorSize = 512 << SectorShift;

    private static readonly uint[] CryptTable = BuildCryptTable();

    private static uint[] BuildCryptTable()
    {
        var t = new uint[0x500];
        uint seed = 0x00100001;
        for (int i = 0; i < 0x100; i++)
        for (int j = i, k = 0; k < 5; k++, j += 0x100)
        {
            seed = (seed * 125 + 3) % 0x2AAAAB;
            uint hi = (seed & 0xFFFF) << 16;
            seed = (seed * 125 + 3) % 0x2AAAAB;
            t[j] = hi | (seed & 0xFFFF);
        }
        return t;
    }

    private static uint HashString(string s, uint hashType)
    {
        uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
        foreach (char c in s.ToUpperInvariant())
        {
            uint ch = (uint)c;
            seed1 = CryptTable[hashType * 256 + ch] ^ (seed1 + seed2);
            seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
        }
        return seed1;
    }

    private static void EncryptBlock(uint[] data, uint key)
    {
        uint seed = 0xEEEEEEEE;
        for (int i = 0; i < data.Length; i++)
        {
            seed += CryptTable[0x400 + (key & 0xFF)];
            uint plain = data[i];
            data[i] = plain ^ (key + seed);
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed = plain + seed + (seed << 5) + 3;
        }
    }

    private struct Block { public uint Offset, CompSize, RawSize, Flags; }

    public static (int Files, List<string> Conflicts) Merge(IReadOnlyList<string> sources, string outputPath)
    {
        var merged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();

        foreach (var src in sources)
        {
            using var arc = new WowMpqArchive(src);
            foreach (var name in arc.ListFiles())
            {
                if (string.Equals(name, "(listfile)", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] data;
                try
                {
                    using var s = arc.OpenFile(name);
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
                catch { continue; }
                if (merged.ContainsKey(name)) conflicts.Add(name);
                merged[name] = data;
            }
        }

        if (merged.Count == 0)
            throw new InvalidOperationException("No files found to merge. The source MPQs need a (listfile).");

        Write(outputPath, merged);
        return (merged.Count, conflicts);
    }

    public static void Write(string outputPath, IReadOnlyDictionary<string, byte[]> files)
    {
        var entries = new List<KeyValuePair<string, byte[]>>(files);
        var listSb = new StringBuilder();
        foreach (var kv in files) listSb.Append(kv.Key.Replace('/', '\\')).Append("\r\n");
        entries.Add(new KeyValuePair<string, byte[]>("(listfile)", Encoding.ASCII.GetBytes(listSb.ToString())));

        int hashCount = 16;
        while (hashCount < entries.Count * 2) hashCount <<= 1;

        var blocks = new List<Block>(entries.Count);
        var names = new List<string>(entries.Count);
        var body = new MemoryStream();

        const int headerSize = 32;
        foreach (var kv in entries)
        {
            string name = kv.Key.Replace('/', '\\');
            byte[] raw = kv.Value;
            uint fileOffset = (uint)(headerSize + body.Length);

            byte[] stored = PackFile(raw);
            body.Write(stored, 0, stored.Length);

            blocks.Add(new Block
            {
                Offset = fileOffset,
                CompSize = (uint)stored.Length,
                RawSize = (uint)raw.Length,
                Flags = FlagExists | FlagCompress,
            });
            names.Add(name);
        }

        uint hashTableOffset = (uint)(headerSize + body.Length);
        uint blockTableOffset = (uint)(hashTableOffset + hashCount * 16);
        uint archiveSize = (uint)(blockTableOffset + blocks.Count * 16);

        byte[] hashTable = BuildHashTable(names, hashCount);
        byte[] blockTable = BuildBlockTable(blocks);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(0x1A51504D);
        w.Write((uint)headerSize);
        w.Write(archiveSize);
        w.Write((ushort)0);
        w.Write(SectorShift);
        w.Write(hashTableOffset);
        w.Write(blockTableOffset);
        w.Write((uint)hashCount);
        w.Write((uint)blocks.Count);

        body.Position = 0;
        body.CopyTo(fs);
        w.Write(hashTable);
        w.Write(blockTable);
    }

    private static byte[] PackFile(byte[] raw)
    {
        int numSectors = Math.Max(1, (raw.Length + SectorSize - 1) / SectorSize);
        var sectors = new List<byte[]>(numSectors);
        for (int s = 0; s < numSectors; s++)
        {
            int off = s * SectorSize;
            int len = Math.Min(SectorSize, raw.Length - off);
            var slice = new byte[len];
            Array.Copy(raw, off, slice, 0, len);

            byte[] comp = ZlibCompress(slice);
            if (comp.Length + 1 < len)
            {
                var withMask = new byte[comp.Length + 1];
                withMask[0] = 0x02;
                Array.Copy(comp, 0, withMask, 1, comp.Length);
                sectors.Add(withMask);
            }
            else
            {
                sectors.Add(slice);
            }
        }

        int tableBytes = (numSectors + 1) * 4;
        var offsets = new uint[numSectors + 1];
        offsets[0] = (uint)tableBytes;
        for (int s = 0; s < numSectors; s++) offsets[s + 1] = offsets[s] + (uint)sectors[s].Length;

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var o in offsets) w.Write(o);
            foreach (var sec in sectors) w.Write(sec);
        }
        return ms.ToArray();
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] BuildHashTable(List<string> names, int hashCount)
    {
        var raw = new uint[hashCount * 4];
        for (int i = 0; i < raw.Length; i++) raw[i] = 0xFFFFFFFF;

        for (int b = 0; b < names.Count; b++)
        {
            string name = names[b];
            uint start = HashString(name, 0) % (uint)hashCount;
            uint hashA = HashString(name, 1);
            uint hashB = HashString(name, 2);
            uint i = start;
            do
            {
                if (raw[i * 4 + 3] == 0xFFFFFFFF)
                {
                    raw[i * 4] = hashA;
                    raw[i * 4 + 1] = hashB;
                    raw[i * 4 + 2] = 0;
                    raw[i * 4 + 3] = (uint)b;
                    break;
                }
                i = (i + 1) % (uint)hashCount;
            } while (i != start);
        }

        EncryptBlock(raw, HashString("(hash table)", 3));
        return ToBytes(raw);
    }

    private static byte[] BuildBlockTable(List<Block> blocks)
    {
        var raw = new uint[blocks.Count * 4];
        for (int i = 0; i < blocks.Count; i++)
        {
            raw[i * 4] = blocks[i].Offset;
            raw[i * 4 + 1] = blocks[i].CompSize;
            raw[i * 4 + 2] = blocks[i].RawSize;
            raw[i * 4 + 3] = blocks[i].Flags;
        }
        EncryptBlock(raw, HashString("(block table)", 3));
        return ToBytes(raw);
    }

    private static byte[] ToBytes(uint[] arr)
    {
        var b = new byte[arr.Length * 4];
        Buffer.BlockCopy(arr, 0, b, 0, b.Length);
        return b;
    }
}
