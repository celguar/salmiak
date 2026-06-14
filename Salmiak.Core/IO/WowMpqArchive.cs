using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Salmiak.Core.IO;

public sealed class WowMpqArchive : IDisposable
{
    private static readonly uint[] CryptTable = BuildCryptTable();

    private static uint[] BuildCryptTable()
    {
        var t = new uint[0x500];
        uint seed = 0x00100001;
        for (int i = 0; i < 0x100; i++)
        {
            for (int j = i, k = 0; k < 5; k++, j += 0x100)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                uint hi = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                t[j] = hi | (seed & 0xFFFF);
            }
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

    private static void DecryptBlock(uint[] data, uint key)
    {
        uint seed = 0xEEEEEEEE;
        for (int i = 0; i < data.Length; i++)
        {
            seed += CryptTable[0x400 + (key & 0xFF)];
            uint plain = data[i] ^ (key + seed);
            key    = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed   = plain + seed + (seed << 5) + 3;
            data[i] = plain;
        }
    }

    private const uint MPQ_FILE_IMPLODE    = 0x00000100;
    private const uint MPQ_FILE_COMPRESS   = 0x00000200;
    private const uint MPQ_FILE_ENCRYPTED  = 0x00010000;
    private const uint MPQ_FILE_FIX_KEY    = 0x00020000;
    private const uint MPQ_FILE_SINGLE_UNIT= 0x01000000;
    private const uint MPQ_FILE_EXISTS     = 0x80000000;

    private const uint HASH_EMPTY   = 0xFFFFFFFF;
    private const uint HASH_DELETED = 0xFFFFFFFE;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HashEntry { public uint HashA, HashB; public ushort Locale, Platform; public uint BlockIndex; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlockEntry { public uint FileOffset, CompressedSize, UncompressedSize, Flags; }

    private readonly FileStream  _file;
    private readonly long        _archiveOffset;
    private readonly uint        _sectorSize;
    private readonly HashEntry[] _hashTable;
    private readonly BlockEntry[] _blockTable;

    public WowMpqArchive(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _archiveOffset = FindMpqHeader(_file);

        using var r = new BinaryReader(_file, Encoding.UTF8, leaveOpen: true);
        _file.Position = _archiveOffset;

        uint magic         = r.ReadUInt32();
        uint headerSize    = r.ReadUInt32();
        uint archiveSize   = r.ReadUInt32();
        ushort fmtVer      = r.ReadUInt16();
        ushort sectorShift = r.ReadUInt16();
        uint hashOffset    = r.ReadUInt32();
        uint blockOffset   = r.ReadUInt32();
        uint hashCount     = r.ReadUInt32();
        uint blockCount    = r.ReadUInt32();

        if (magic != 0x1A51504D)
            throw new InvalidDataException("Not a valid MPQ archive.");

        _sectorSize = 512u << sectorShift;

        _hashTable  = ReadHashTable(r, hashOffset, hashCount);
        _blockTable = ReadBlockTable(r, blockOffset, blockCount);
    }

    private long FindMpqHeader(FileStream file)
    {
        var buf = new byte[4];
        file.Position = 0;
        file.ReadExactly(buf);
        if (buf[0] == 0x4D && buf[1] == 0x50 && buf[2] == 0x51 && buf[3] == 0x1A)
            return 0;
        throw new InvalidDataException("MPQ header not found at offset 0.");
    }

    private HashEntry[] ReadHashTable(BinaryReader r, uint offset, uint count)
    {
        _file.Position = _archiveOffset + offset;
        var raw = new uint[count * 4];
        for (int i = 0; i < raw.Length; i++) raw[i] = r.ReadUInt32();
        DecryptBlock(raw, HashString("(hash table)", 3));

        var table = new HashEntry[count];
        for (int i = 0; i < (int)count; i++)
            table[i] = new HashEntry
            {
                HashA      = raw[i * 4],
                HashB      = raw[i * 4 + 1],
                Locale     = (ushort)(raw[i * 4 + 2] & 0xFFFF),
                Platform   = (ushort)(raw[i * 4 + 2] >> 16),
                BlockIndex = raw[i * 4 + 3],
            };
        return table;
    }

    private BlockEntry[] ReadBlockTable(BinaryReader r, uint offset, uint count)
    {
        _file.Position = _archiveOffset + offset;
        var raw = new uint[count * 4];
        for (int i = 0; i < raw.Length; i++) raw[i] = r.ReadUInt32();
        DecryptBlock(raw, HashString("(block table)", 3));

        var table = new BlockEntry[count];
        for (int i = 0; i < (int)count; i++)
            table[i] = new BlockEntry
            {
                FileOffset       = raw[i * 4],
                CompressedSize   = raw[i * 4 + 1],
                UncompressedSize = raw[i * 4 + 2],
                Flags            = raw[i * 4 + 3],
            };
        return table;
    }

    public string Diagnostics(string testFile = "(listfile)")
    {
        int nonEmpty = 0;
        for (int i = 0; i < _hashTable.Length; i++)
            if (_hashTable[i].BlockIndex != HASH_EMPTY && _hashTable[i].BlockIndex != HASH_DELETED)
                nonEmpty++;

        var first = _hashTable.Length > 0 ? _hashTable[0] : new HashEntry();
        string norm = NormalizePath(testFile);
        uint hA = HashString(norm, 1), hB = HashString(norm, 2);
        uint bucket = HashString(norm, 0) % (uint)_hashTable.Length;

        return $"tbl={_hashTable.Length} used={nonEmpty} | {testFile}: hA={hA:X8} hB={hB:X8} bucket={bucket} | entry0: hA={first.HashA:X8} hB={first.HashB:X8} blk={first.BlockIndex:X8}";
    }

    public void PrintDiagnostics(string testFile = "World\\Maps\\Azeroth\\Azeroth.wdt")
    {
        Console.WriteLine($"  Hash table size : {_hashTable.Length}");
        Console.WriteLine($"  Block table size: {_blockTable.Length}");

        int used = 0, empty = 0, deleted = 0;
        for (int i = 0; i < _hashTable.Length; i++)
        {
            var bi = _hashTable[i].BlockIndex;
            if      (bi == HASH_EMPTY)   empty++;
            else if (bi == HASH_DELETED) deleted++;
            else                         used++;
        }
        Console.WriteLine($"  Entries used={used} empty={empty} deleted={deleted}");

        uint key3 = HashString("(hash table)", 3);
        Console.WriteLine($"  Hash table decrypt key: {key3:X8}");

        string norm = NormalizePath(testFile);
        uint hA = HashString(norm, 1), hB = HashString(norm, 2);
        uint bucket = HashString(norm, 0) % (uint)_hashTable.Length;
        Console.WriteLine($"  Lookup '{testFile}': hA={hA:X8} hB={hB:X8} bucket={bucket}");

        Console.WriteLine($"  Hash table entries at bucket ±2:");
        for (int d = -2; d <= 2; d++)
        {
            int idx = (int)((bucket + _hashTable.Length + d) % (uint)_hashTable.Length);
            var e = _hashTable[idx];
            Console.WriteLine($"    [{idx}] hA={e.HashA:X8} hB={e.HashB:X8} blk={e.BlockIndex:X8}");
        }

        Console.WriteLine("  First 5 non-empty entries:");
        int shown = 0;
        for (int i = 0; i < _hashTable.Length && shown < 5; i++)
        {
            var e = _hashTable[i];
            if (e.BlockIndex == HASH_EMPTY || e.BlockIndex == HASH_DELETED) continue;
            Console.WriteLine($"    [{i}] hA={e.HashA:X8} hB={e.HashB:X8} blk={e.BlockIndex:X8}");
            shown++;
        }

        Console.WriteLine("  Raw first 16 bytes of hash table (before decryption):");
        using var rr = new BinaryReader(_file, System.Text.Encoding.UTF8, leaveOpen: true);
        _file.Position = _archiveOffset;
        rr.ReadUInt32();
        rr.ReadUInt32();
        rr.ReadUInt32();
        rr.ReadUInt16();
        rr.ReadUInt16();
        uint hashOff = rr.ReadUInt32();
        _file.Position = _archiveOffset + hashOff;
        for (int i = 0; i < 4; i++)
            Console.Write($"  {rr.ReadUInt32():X8}");
        Console.WriteLine();
    }

    public bool FileExists(string mpqPath)
    {
        mpqPath = NormalizePath(mpqPath);
        uint hashA = HashString(mpqPath, 1);
        uint hashB = HashString(mpqPath, 2);
        uint start = HashString(mpqPath, 0) % (uint)_hashTable.Length;
        uint i = start;
        do
        {
            var e = _hashTable[i];
            if (e.BlockIndex == HASH_EMPTY) return false;
            if (e.BlockIndex != HASH_DELETED && e.HashA == hashA && e.HashB == hashB)
                return (_blockTable[e.BlockIndex].Flags & MPQ_FILE_EXISTS) != 0;
            i = (i + 1) % (uint)_hashTable.Length;
        } while (i != start);
        return false;
    }

    public IEnumerable<string> ListFiles()
    {
        if (!FileExists("(listfile)")) yield break;
        using var s = OpenFile("(listfile)");
        using var r = new StreamReader(s);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length > 0) yield return line;
        }
    }

    public Stream OpenFile(string mpqPath)
    {
        mpqPath = NormalizePath(mpqPath);
        uint hashA = HashString(mpqPath, 1);
        uint hashB = HashString(mpqPath, 2);
        uint start = HashString(mpqPath, 0) % (uint)_hashTable.Length;
        uint i = start;
        do
        {
            var e = _hashTable[i];
            if (e.BlockIndex == HASH_EMPTY) break;
            if (e.BlockIndex != HASH_DELETED && e.HashA == hashA && e.HashB == hashB)
            {
                var block = _blockTable[e.BlockIndex];
                if ((block.Flags & MPQ_FILE_EXISTS) == 0) break;
                return ReadFile(mpqPath, block);
            }
            i = (i + 1) % (uint)_hashTable.Length;
        } while (i != start);
        throw new FileNotFoundException($"File not found in MPQ: {mpqPath}");
    }

    private MemoryStream ReadFile(string mpqPath, BlockEntry block)
    {
        using var r = new BinaryReader(_file, Encoding.UTF8, leaveOpen: true);

        long filePos = _archiveOffset + block.FileOffset;

        uint encKey = 0;
        if ((block.Flags & MPQ_FILE_ENCRYPTED) != 0)
        {
            string name = System.IO.Path.GetFileName(mpqPath);
            encKey = HashString(name, 3);
            if ((block.Flags & MPQ_FILE_FIX_KEY) != 0)
                encKey = (encKey + block.FileOffset) ^ block.UncompressedSize;
        }

        var output = new MemoryStream((int)block.UncompressedSize);

        if ((block.Flags & MPQ_FILE_SINGLE_UNIT) != 0)
        {
            _file.Position = filePos;
            var data = r.ReadBytes((int)block.CompressedSize);
            if ((block.Flags & MPQ_FILE_ENCRYPTED) != 0)
            {
                var uints = ToUintArray(data);
                DecryptBlock(uints, encKey);
                data = ToByteArray(uints);
            }
            if ((block.Flags & (MPQ_FILE_COMPRESS | MPQ_FILE_IMPLODE)) != 0)
                data = DecompressSector(data, (int)block.UncompressedSize, (block.Flags & MPQ_FILE_IMPLODE) != 0);
            output.Write(data);
        }
        else if ((block.Flags & (MPQ_FILE_COMPRESS | MPQ_FILE_IMPLODE)) == 0)
        {
            _file.Position = filePos;
            var data = r.ReadBytes((int)block.UncompressedSize);
            if ((block.Flags & MPQ_FILE_ENCRYPTED) != 0)
            {
                for (int s = 0; s * (long)_sectorSize < data.Length; s++)
                {
                    int off = s * (int)_sectorSize;
                    int len = Math.Min((int)_sectorSize, data.Length - off);
                    var chunk = new byte[len];
                    Array.Copy(data, off, chunk, 0, len);
                    var uints = ToUintArray(chunk);
                    DecryptBlock(uints, encKey + (uint)s);
                    Array.Copy(ToByteArray(uints), 0, data, off, len);
                }
            }
            output.Write(data);
        }
        else
        {
            int numSectors = (int)((block.UncompressedSize + _sectorSize - 1) / _sectorSize);
            bool hasCrc    = (block.Flags & 0x04000000) != 0;
            int numOffsets = numSectors + 1 + (hasCrc ? 1 : 0);

            _file.Position = filePos;
            var offsets = new uint[numOffsets];
            for (int j = 0; j < numOffsets; j++) offsets[j] = r.ReadUInt32();

            if ((block.Flags & MPQ_FILE_ENCRYPTED) != 0)
                DecryptBlock(offsets, encKey - 1);

            for (int s = 0; s < numSectors; s++)
            {
                uint sectorOffset = offsets[s];
                int  sectorBytes  = (int)(offsets[s + 1] - offsets[s]);

                int sectorUncomp = (int)Math.Min(_sectorSize, block.UncompressedSize - (uint)s * _sectorSize);

                _file.Position = filePos + sectorOffset;
                var sector = r.ReadBytes(sectorBytes);

                if ((block.Flags & MPQ_FILE_ENCRYPTED) != 0)
                {
                    var uints = ToUintArray(sector);
                    DecryptBlock(uints, encKey + (uint)s);
                    sector = ToByteArray(uints);
                }

                bool isImplode   = (block.Flags & MPQ_FILE_IMPLODE) != 0;
                bool isCompressed = (block.Flags & MPQ_FILE_COMPRESS) != 0;

                if ((isCompressed || isImplode) && sectorBytes < sectorUncomp)
                    sector = DecompressSector(sector, sectorUncomp, isImplode);

                output.Write(sector);
            }
        }

        output.Position = 0;
        return output;
    }

    private static byte[] DecompressSector(byte[] data, int expectedSize, bool forceImplode)
    {
        if (forceImplode)
            return Implode(data, expectedSize);

        byte mask = data[0];

        if (mask == 0x02)
        {
            var ms = new MemoryStream(expectedSize);
            using var zlib = new ZLibStream(new MemoryStream(data, 1, data.Length - 1), CompressionMode.Decompress);
            zlib.CopyTo(ms);
            return ms.ToArray();
        }
        if (mask == 0x08)
            return Implode(data[1..], expectedSize);
        if (mask == 0x10)
        {
            var ms = new MemoryStream(expectedSize);
            using var bz = new ICSharpCode.SharpZipLib.BZip2.BZip2InputStream(new MemoryStream(data, 1, data.Length - 1));
            bz.CopyTo(ms);
            return ms.ToArray();
        }
        return data[1..];
    }

    private static byte[] Implode(byte[] data, int expectedSize)
    {
        var inf = new Inflater(noHeader: true);
        inf.SetInput(data);
        var output = new byte[expectedSize];
        inf.Inflate(output);
        return output;
    }

    private static uint[] ToUintArray(byte[] bytes)
    {
        int count = (bytes.Length + 3) / 4;
        var arr = new uint[count];
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    private static byte[] ToByteArray(uint[] uints)
    {
        var arr = new byte[uints.Length * 4];
        Buffer.BlockCopy(uints, 0, arr, 0, arr.Length);
        return arr;
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\');

    public void Dispose() => _file.Dispose();
}
