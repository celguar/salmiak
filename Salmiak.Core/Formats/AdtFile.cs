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
        //No edge fix here, unlike the layer alpha maps above: MCSH is a plain 64x64 grid of cells,
        //so its last row and column are real shadow the neighbouring chunk does not already have.
        //WoW Viewer's own MCSH reader leaves them alone, and duplicating them wipes a shadow ring
        //one texel wide along the east and south border of every chunk.
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

    // Everything that shadow baking must NOT have touched. Used to prove that an
    // injected .adt (original bytes + patched shadow maps) still matches the tile as
    // it was edited in the viewport; if anything else differs the injector is not
    // usable and the tile has to go through the full writer instead. The one tolerated
    // exception is the chunk shadow flag, which a chunk needs when it gains its first
    // MCSH (see SameChunk).
    public bool SameExceptShadows(AdtFile other)
    {
        if (!SameStrings(Textures, other.Textures)) return false;
        if (!SameStrings(DoodadModels, other.DoodadModels)) return false;
        if (!SameStrings(WmoModels, other.WmoModels)) return false;
        if (!SameUInts(DoodadModelOffsets, other.DoodadModelOffsets)) return false;
        if (!SameUInts(WmoModelOffsets, other.WmoModelOffsets)) return false;
        if (!SameDoodads(Doodads, other.Doodads)) return false;
        if (!SameWmos(Wmos, other.Wmos)) return false;

        for (int y = 0; y < ChunksPerSide; y++)
            for (int x = 0; x < ChunksPerSide; x++)
                if (!SameChunk(Chunks[y, x], other.Chunks[y, x]))
                    return false;
        return true;
    }

    private static bool SameChunk(MapChunk? a, MapChunk? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.IndexX != b.IndexX || a.IndexY != b.IndexY || a.AreaId != b.AreaId) return false;
        if (a.NLayers != b.NLayers) return false;
        // The shadow flag (bit 0) may differ: injecting an MCSH into a chunk that had none
        // sets it. Every other flag must match, or the patch touched something else.
        if ((a.Flags | 1u) != (b.Flags | 1u)) return false;
        if (a.Holes != b.Holes) return false;
        if (a.Position != b.Position) return false;
        for (int i = 0; i < MapChunk.HeightCount; i++)
            if (a.Heights[i] != b.Heights[i])
                return false;
        if (a.McalSize != b.McalSize || a.McalIs4Bit != b.McalIs4Bit) return false;
        for (int i = 0; i < MapChunk.MaxLayers; i++)
        {
            var la = a.Layers[i];
            var lb = b.Layers[i];
            if (la.TextureIndex != lb.TextureIndex || la.Flags != lb.Flags
                || la.OffsetInMCAL != lb.OffsetInMCAL || la.EffectId != lb.EffectId)
                return false;
        }
        for (int i = 0; i < 3; i++)
            if (!SameBytes(a.AlphaMaps[i], b.AlphaMaps[i]))
                return false;
        if (!SameLiquid(a.Liquid, b.Liquid)) return false;
        if (a.Normals is not null && b.Normals is not null && a.Normals.Length == b.Normals.Length)
        {
            for (int i = 0; i < a.Normals.Length; i++)
                if (a.Normals[i] != b.Normals[i]) return false;
        }
        return true;   // shadow maps deliberately excluded
    }

    private static bool SameLiquid(LiquidLayer? a, LiquidLayer? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Type != b.Type || a.MinHeight != b.MinHeight || a.MaxHeight != b.MaxHeight) return false;
        for (int i = 0; i < a.Heights.Length; i++)
            if (a.Heights[i] != b.Heights[i]) return false;
        for (int i = 0; i < a.Render.Length; i++)
            if (a.Render[i] != b.Render[i]) return false;
        return true;
    }

    private static bool SameBytes(byte[]? a, byte[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static bool SameStrings(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    private static bool SameUInts(List<uint> a, List<uint> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    private static bool SameDoodads(List<DoodadDef> a, List<DoodadDef> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.ModelPath != y.ModelPath || x.Position != y.Position || x.Rotation != y.Rotation
                || x.Scale != y.Scale || x.UniqueId != y.UniqueId || x.Flags != y.Flags)
                return false;
        }
        return true;
    }

    private static bool SameWmos(List<WmoDef> a, List<WmoDef> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.ModelPath != y.ModelPath || x.Position != y.Position || x.Rotation != y.Rotation
                || x.UniqueId != y.UniqueId || x.Flags != y.Flags || x.DoodadSet != y.DoodadSet)
                return false;
        }
        return true;
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

    // Shadow maps are 64x64 CELLS, one byte per texel (0 or 255), and each cell covers ChunkSize/64
    // of ground: texel (row, col) spans z = -Position.X + row*ChunkSize/64 up to one cell further
    //south and x = -Position.Y + col*ChunkSize/64 up to one cell further east. The client samples
    //this grid with the alpha-map matrix whose scale is 1/ChunkSize, so the value is read at the
    //middle of the cell, and that is the point the rays are traced from.
    public const int TexelsPerSide = 64;
    public const int ShadowTexels  = TexelsPerSide * TexelsPerSide;

    public bool ShadowIsCleared()
    {
        if (ShadowMap is not byte[] m) return true;
        for (int i = 0; i < m.Length; i++)
            if (m[i] != 0)
                return false;
        return true;
    }

    public int ShadowTexelsSet()
    {
        if (ShadowMap is not byte[] m) return 0;
        var n = 0;
        for (int i = 0; i < m.Length; i++)
            if (m[i] != 0)
                n++;
        return n;
    }

    public byte[]? ShadowSnapshot()
    {
        if (ShadowMap is not byte[] m) return null;
        var c = new byte[m.Length];
        Array.Copy(m, 0, c, 0, m.Length);
        return c;
    }

    public void RestoreShadowMap(byte[]? snap)
    {
        ShadowMap = snap is byte[] s ? s : null;
    }
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
