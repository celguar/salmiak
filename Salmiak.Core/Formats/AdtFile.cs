using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class AdtFile
{
    public const int ChunksPerSide  = 16;
    public const float TileSize     = 533.333333f;
    public const float ChunkSize    = TileSize / ChunksPerSide;

    public MapChunk[,] Chunks { get; } = new MapChunk[ChunksPerSide, ChunksPerSide];

    public List<string> Textures { get; } = new();

    public List<string> DoodadModels { get; } = new();

    public List<uint> DoodadModelOffsets { get; } = new();

    public List<DoodadDef> Doodads { get; } = new();

    public List<string> WmoModels { get; } = new();

    public List<uint> WmoModelOffsets { get; } = new();

    public List<WmoDef> Wmos { get; } = new();

    private readonly Dictionary<uint, string> _mmdxByOffset = new();
    private readonly Dictionary<uint, string> _mwmoByOffset = new();

    public static AdtFile Parse(Stream stream)
    {
        var adt = new AdtFile();
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var mcinOffsets = new uint[ChunksPerSide * ChunksPerSide];

        stream.Position = 0;
        while (stream.Position < stream.Length - 8)
        {
            var magicBytes = reader.ReadBytes(4);
            Array.Reverse(magicBytes);
            var magic = Encoding.ASCII.GetString(magicBytes);
            var size  = reader.ReadUInt32();
            var start = stream.Position;

            switch (magic)
            {
                case "MTEX":
                    ParseTextures(reader, (int)size, adt.Textures);
                    break;

                case "MMDX":
                    ParseStringList(reader, (int)size, adt.DoodadModels, adt._mmdxByOffset);
                    break;

                case "MMID":
                    for (int i = 0; i < size / 4; i++)
                        adt.DoodadModelOffsets.Add(reader.ReadUInt32());
                    break;

                case "MDDF":
                    ParseDoodadDefs(reader, (int)size, adt);
                    break;

                case "MWMO":
                    ParseStringList(reader, (int)size, adt.WmoModels, adt._mwmoByOffset);
                    break;

                case "MWID":
                    for (int i = 0; i < size / 4; i++)
                        adt.WmoModelOffsets.Add(reader.ReadUInt32());
                    break;

                case "MODF":
                    ParseWmoDefs(reader, (int)size, adt);
                    break;

                case "MCIN":
                    for (int i = 0; i < ChunksPerSide * ChunksPerSide; i++)
                    {
                        mcinOffsets[i] = reader.ReadUInt32();
                        reader.ReadUInt32();
                        reader.ReadUInt32();
                        reader.ReadUInt32();
                    }
                    break;

                case "MCNK":
                    try { ParseMcnk(reader, size, adt); }
                    catch { }
                    break;
            }

            stream.Position = start + size;
        }

        return adt;
    }

    private static byte[] Expand4Bit(byte[] src, int offset)
    {
        var dst = new byte[4096];
        int available = Math.Min(2048, src.Length - offset);
        for (int i = 0; i < available; i++)
        {
            byte b = src[offset + i];
            dst[i * 2]     = (byte)((b & 0x0F) * 17);
            dst[i * 2 + 1] = (byte)((b >> 4)   * 17);
        }
        FixAlphaEdges(dst);
        return dst;
    }

    private static void FixAlphaEdges(byte[] map)
    {
        for (int y = 0; y < 64; y++) map[y * 64 + 63] = map[y * 64 + 62];
        for (int x = 0; x < 64; x++) map[63 * 64 + x] = map[62 * 64 + x];
    }

    private static byte[]? ExpandShadow(byte[] src)
    {
        var dst = new byte[4096];
        bool any = false;
        for (int row = 0; row < 64; row++)
        for (int col = 0; col < 64; col++)
        {
            int byteIdx = row * 8 + (col >> 3);
            if (byteIdx >= src.Length) continue;
            if (((src[byteIdx] >> (col & 7)) & 1) != 0) { dst[row * 64 + col] = 255; any = true; }
        }
        if (!any) return null;
        FixAlphaEdges(dst);
        return dst;
    }

    private static byte[] SafeSlice(byte[] src, int offset, int len)
    {
        int available = Math.Min(len, src.Length - offset);
        if (available <= 0) return new byte[len];
        var dst = new byte[len];
        Array.Copy(src, offset, dst, 0, available);
        return dst;
    }

    private static void ParseTextures(BinaryReader reader, int size, List<string> textures)
    {
        var bytes = reader.ReadBytes(size);
        int pos = 0;
        while (pos < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)0, pos);
            if (end < 0) end = bytes.Length;
            if (end > pos)
                textures.Add(Encoding.UTF8.GetString(bytes, pos, end - pos));
            pos = end + 1;
        }
    }

    private static void ParseStringList(BinaryReader reader, int size, List<string> list, Dictionary<uint, string> offsetMap)
    {
        var bytes = reader.ReadBytes(size);
        int pos = 0;
        while (pos < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)0, pos);
            if (end < 0) end = bytes.Length;
            if (end > pos)
            {
                var name = Encoding.UTF8.GetString(bytes, pos, end - pos);
                list.Add(name);
                offsetMap[(uint)pos] = name;
            }
            pos = end + 1;
        }
    }

    private static void ParseDoodadDefs(BinaryReader reader, int size, AdtFile adt)
    {
        int count = size / 36;
        for (int i = 0; i < count; i++)
        {
            uint nameId   = reader.ReadUInt32();
            uint uniqueId = reader.ReadUInt32();
            float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
            float rx = reader.ReadSingle(), ry = reader.ReadSingle(), rz = reader.ReadSingle();
            ushort scale = reader.ReadUInt16();
            ushort flags = reader.ReadUInt16();

            string path = "";
            if (nameId < adt.DoodadModelOffsets.Count)
                adt._mmdxByOffset.TryGetValue(adt.DoodadModelOffsets[(int)nameId], out path!);

            adt.Doodads.Add(new DoodadDef
            {
                ModelPath = path ?? "",
                Position  = new Vector3(px, py, pz),
                Rotation  = new Vector3(rx, ry, rz),
                Scale     = scale / 1024f,
                UniqueId  = uniqueId,
                Flags     = flags,
            });
        }
    }

    private static void ParseWmoDefs(BinaryReader reader, int size, AdtFile adt)
    {
        int count = size / 64;
        for (int i = 0; i < count; i++)
        {
            uint nameId   = reader.ReadUInt32();
            uint uniqueId = reader.ReadUInt32();
            float px = reader.ReadSingle(), py = reader.ReadSingle(), pz = reader.ReadSingle();
            float rx = reader.ReadSingle(), ry = reader.ReadSingle(), rz = reader.ReadSingle();
            reader.ReadBytes(24);
            ushort flags     = reader.ReadUInt16();
            ushort doodadSet = reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadUInt16();

            string path = "";
            if (nameId < adt.WmoModelOffsets.Count)
                adt._mwmoByOffset.TryGetValue(adt.WmoModelOffsets[(int)nameId], out path!);

            adt.Wmos.Add(new WmoDef
            {
                ModelPath = path ?? "",
                Position  = new Vector3(px, py, pz),
                Rotation  = new Vector3(rx, ry, rz),
                UniqueId  = uniqueId,
                Flags     = flags,
                DoodadSet = doodadSet,
            });
        }
    }

    private static void ParseMcnk(BinaryReader reader, uint chunkSize, AdtFile adt)
    {
        var chunkStart = reader.BaseStream.Position;

        var flags  = reader.ReadUInt32();
        var indexX = reader.ReadUInt32();
        var indexY = reader.ReadUInt32();
        var nLayers = reader.ReadUInt32();
        var nDoodadRefs = reader.ReadUInt32();
        var offMCVT = reader.ReadUInt32();
        var offMCNR = reader.ReadUInt32();
        var offMCLY = reader.ReadUInt32();
        var offMCRF = reader.ReadUInt32();
        var offMCAL = reader.ReadUInt32();
        var sizeMCAL = reader.ReadUInt32();
        var offMCSH = reader.ReadUInt32();
        var sizeMCSH = reader.ReadUInt32();
        var areaID   = reader.ReadUInt32();
        var nMapObjRefs = reader.ReadUInt32();
        var holes    = reader.ReadUInt16();
        reader.ReadUInt16();
        reader.ReadBytes(16);
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        var offMCLQ  = reader.ReadUInt32();
        var sizeMCLQ = reader.ReadUInt32();
        var posX = reader.ReadSingle();
        var posY = reader.ReadSingle();
        var posZ = reader.ReadSingle();

        var chunk = new MapChunk
        {
            IndexX  = (int)indexX,
            IndexY  = (int)indexY,
            Position = new Vector3(posX, posY, posZ),
            AreaId  = (int)areaID,
            Holes   = holes,
            NLayers = (int)nLayers,
            Flags   = flags,
        };

        if (offMCVT > 0)
        {
            reader.BaseStream.Position = chunkStart + offMCVT;
            for (int i = 0; i < MapChunk.HeightCount; i++)
                chunk.Heights[i] = reader.ReadSingle();
        }

        if (offMCNR > 0)
        {
            reader.BaseStream.Position = chunkStart + offMCNR;
            var nb = reader.ReadBytes(MapChunk.HeightCount * 3);
            if (nb.Length == MapChunk.HeightCount * 3)
            {
                var sn = new sbyte[nb.Length];
                for (int i = 0; i < nb.Length; i++) sn[i] = unchecked((sbyte)nb[i]);
                chunk.Normals = sn;
            }
        }

        if (offMCLY > 0 && nLayers > 0)
        {
            reader.BaseStream.Position = chunkStart + offMCLY;
            for (int i = 0; i < nLayers; i++)
            {
                chunk.Layers[i] = new TextureLayer
                {
                    TextureIndex = reader.ReadUInt32(),
                    Flags        = reader.ReadUInt32(),
                    OffsetInMCAL = reader.ReadUInt32(),
                    EffectId     = reader.ReadInt32(),
                };
            }
        }

        if (offMCAL > 0 && sizeMCAL > 0 && nLayers > 1)
        {
            try
            {
                reader.BaseStream.Position = chunkStart + offMCAL;
                var mcalData = reader.ReadBytes((int)sizeMCAL);

                int nAlpha = (int)nLayers - 1;
                int bytesPerAlpha = (int)sizeMCAL / nAlpha;
                bool is4bit = bytesPerAlpha < 3072;
                chunk.McalSize = (int)sizeMCAL;
                chunk.McalIs4Bit = is4bit;

                for (int l = 1; l < (int)nLayers; l++)
                {
                    int src = (int)chunk.Layers[l].OffsetInMCAL;
                    if (src < 0 || src >= mcalData.Length) continue;
                    chunk.AlphaMaps[l - 1] = is4bit
                        ? Expand4Bit(mcalData, src)
                        : SafeSlice(mcalData, src, 4096);
                }
            }
            catch { }
        }

        if (offMCSH > 0 && sizeMCSH >= 512 && (flags & 0x1) != 0)
        {
            try
            {
                reader.BaseStream.Position = chunkStart + offMCSH;
                chunk.ShadowMap = ExpandShadow(reader.ReadBytes((int)sizeMCSH));
            }
            catch { }
        }

        if (offMCLQ > 0 && sizeMCLQ >= 720 && (flags & MapChunk.FlagHasLiquid) != 0)
        {
            try
            {
                reader.BaseStream.Position = chunkStart + offMCLQ;
                var liquid = new LiquidLayer
                {
                    Type = LiquidTypeFromFlags(flags),
                    MinHeight = reader.ReadSingle(),
                    MaxHeight = reader.ReadSingle(),
                };
                for (int i = 0; i < 9 * 9; i++)
                {
                    reader.ReadUInt32();
                    liquid.Heights[i] = reader.ReadSingle();
                }
                for (int i = 0; i < 8 * 8; i++)
                    liquid.Render[i] = (reader.ReadByte() & 0x0F) != 0x0F;

                chunk.Liquid = liquid;
            }
            catch { }
        }

        adt.Chunks[(int)indexY, (int)indexX] = chunk;
    }

    private static int LiquidTypeFromFlags(uint flags)
    {
        if ((flags & 0x04) != 0) return 1;
        if ((flags & 0x08) != 0) return 2;
        if ((flags & 0x10) != 0) return 3;
        if ((flags & 0x20) != 0) return 4;
        return 0;
    }
}

public sealed class MapChunk
{
    public const int HeightCount  = 9 * 9 + 8 * 8;
    public const int MaxLayers    = 4;

    public const uint FlagHasLiquid = 0x04 | 0x08 | 0x10 | 0x20;

    public int IndexX { get; init; }
    public int IndexY { get; init; }
    public Vector3 Position { get; init; }
    public int AreaId  { get; set; }
    public ushort Holes { get; set; }
    public int NLayers { get; set; }
    public uint Flags  { get; init; }

    public int McalSize { get; set; }
    public bool McalIs4Bit { get; set; }

    public float[] Heights { get; } = new float[HeightCount];
    public sbyte[]? Normals { get; set; }
    public TextureLayer[] Layers { get; } = new TextureLayer[MaxLayers];

    public byte[]?[] AlphaMaps { get; } = new byte[]?[3];

    public byte[]? ShadowMap { get; set; }

    public LiquidLayer? Liquid { get; set; }
}

public sealed class LiquidLayer
{
    public int Type { get; init; }
    public float MinHeight { get; init; }
    public float MaxHeight { get; init; }
    public float[] Heights { get; } = new float[9 * 9];
    public bool[] Render { get; } = new bool[8 * 8];
}

public struct TextureLayer
{
    public uint TextureIndex;
    public uint Flags;
    public uint OffsetInMCAL;
    public int  EffectId;
}

public struct DoodadDef
{
    public string ModelPath;
    public Vector3 Position;
    public Vector3 Rotation;
    public float Scale;
    public uint UniqueId;
    public ushort Flags;
}

public struct WmoDef
{
    public string ModelPath;
    public Vector3 Position;
    public Vector3 Rotation;
    public uint UniqueId;
    public ushort Flags;
    public ushort DoodadSet;
}
