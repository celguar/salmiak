using System;
using System.IO;
using System.Numerics;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class WdtFile
{
    public const int GridSize = 64;

    public bool[,] TileExists { get; } = new bool[GridSize, GridSize];

    public bool IsWmoOnly { get; private set; }

    public string? GlobalWmo { get; private set; }
    public WmoDef? GlobalWmoDef { get; private set; }

    public static byte[] BuildNew(bool[,] tiles)
    {
        var b = new byte[12 + 40 + 8 + GridSize * GridSize * 8 + 8];
        int p = 0;
        void Chunk(string magic, int size)
        {
            b[p] = (byte)magic[3]; b[p + 1] = (byte)magic[2]; b[p + 2] = (byte)magic[1]; b[p + 3] = (byte)magic[0];
            BitConverter.GetBytes(size).CopyTo(b, p + 4);
            p += 8;
        }
        Chunk("MVER", 4); BitConverter.GetBytes(18u).CopyTo(b, p); p += 4;
        Chunk("MPHD", 32); p += 32;
        Chunk("MAIN", GridSize * GridSize * 8);
        for (int y = 0; y < GridSize; y++)
        for (int x = 0; x < GridSize; x++)
        {
            if (tiles[y, x]) b[p] = 0x01;
            p += 8;
        }
        Chunk("MWMO", 0);
        return b;
    }

    public static byte[] PatchMain(byte[] original, bool[,] tiles)
    {
        var b = (byte[])original.Clone();
        int pos = 0;
        while (pos + 8 <= b.Length)
        {
            string magic = new string(new[] { (char)b[pos + 3], (char)b[pos + 2], (char)b[pos + 1], (char)b[pos] });
            int size = BitConverter.ToInt32(b, pos + 4);
            if (size < 0 || pos + 8 + size > b.Length) break;
            if (magic == "MAIN")
            {
                for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                {
                    int o = pos + 8 + (y * GridSize + x) * 8;
                    if (o + 4 <= b.Length && tiles[y, x]) b[o] |= 0x01;
                }
                break;
            }
            pos += 8 + size;
        }
        return b;
    }

    public static WdtFile Parse(Stream stream)
    {
        var wdt = new WdtFile();
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        while (stream.Position < stream.Length - 8)
        {
            var magicBytes = reader.ReadBytes(4);
            Array.Reverse(magicBytes);
            var magic = Encoding.ASCII.GetString(magicBytes);
            var size  = reader.ReadUInt32();
            var start = stream.Position;

            switch (magic)
            {
                case "MPHD":
                    var flags = reader.ReadUInt32();
                    wdt.IsWmoOnly = (flags & 0x01) != 0;
                    break;

                case "MAIN":
                    for (int y = 0; y < GridSize; y++)
                    for (int x = 0; x < GridSize; x++)
                    {
                        var tileFlags = reader.ReadUInt32();
                        reader.ReadUInt32();
                        wdt.TileExists[y, x] = (tileFlags & 0x01) != 0;
                    }
                    break;

                case "MWMO":
                {
                    var bytes = reader.ReadBytes((int)size);
                    int end = Array.IndexOf(bytes, (byte)0);
                    if (end < 0) end = bytes.Length;
                    if (end > 0) wdt.GlobalWmo = Encoding.UTF8.GetString(bytes, 0, end);
                    break;
                }

                case "MODF":
                    if (size >= 64)
                    {
                        reader.ReadUInt32();
                        uint uniqueId = reader.ReadUInt32();
                        float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
                        float rx = reader.ReadSingle(), ry = reader.ReadSingle(), rz = reader.ReadSingle();
                        reader.ReadBytes(24);
                        ushort fl = reader.ReadUInt16();
                        ushort doodadSet = reader.ReadUInt16();
                        reader.ReadUInt16();
                        reader.ReadUInt16();
                        wdt.GlobalWmoDef = new WmoDef
                        {
                            ModelPath = wdt.GlobalWmo ?? "",
                            Position = new Vector3(px, py, pz),
                            Rotation = new Vector3(rx, ry, rz),
                            UniqueId = uniqueId,
                            Flags = fl,
                            DoodadSet = doodadSet,
                        };
                    }
                    break;
            }

            stream.Position = start + size;
        }

        return wdt;
    }
}
