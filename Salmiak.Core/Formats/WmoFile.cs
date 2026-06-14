using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Salmiak.Core.Formats;

public static class WmoFile
{
    public sealed class Root
    {
        public int GroupCount;
        public List<string> MaterialTextures = new();
        public List<uint> MaterialBlend = new();
        public List<InteriorDoodad> Doodads = new();
        public List<(int Start, int Count)> DoodadSets = new();
        public List<string> DoodadSetNames = new();
    }

    public struct InteriorDoodad
    {
        public string Path;
        public Vector3 Position;
        public Quaternion Rotation;
        public float Scale;
        public uint Color;
    }

    public sealed class Group
    {
        public float[] Vertices = [];
        public uint[] Indices = [];
        public List<Batch> Batches = new();
        public bool HasVertexColors;
    }

    public const int GroupVertexStride = 12;

    public struct Batch
    {
        public int IndexStart;
        public int IndexCount;
        public string TexturePath;
        public bool AlphaTest;
    }

    public static Root ParseRoot(Stream stream)
    {
        var root = new Root();
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        byte[] motx = [];
        byte[] modn = [];
        var materialTexOffsets = new List<uint>();
        var doodadRaw = new List<(uint nameOfs, Vector3 pos, Quaternion rot, float scale, uint color)>();

        stream.Position = 0;
        while (stream.Position < stream.Length - 8)
        {
            string magic = ReadMagic(r);
            uint size = r.ReadUInt32();
            long start = stream.Position;

            switch (magic)
            {
                case "MOHD":
                    r.ReadUInt32();
                    root.GroupCount = (int)r.ReadUInt32();
                    break;

                case "MOTX":
                    motx = r.ReadBytes((int)size);
                    break;

                case "MOMT":
                    int nMat = (int)size / 64;
                    for (int i = 0; i < nMat; i++)
                    {
                        stream.Position = start + i * 64 + 8;
                        root.MaterialBlend.Add(r.ReadUInt32());
                        materialTexOffsets.Add(r.ReadUInt32());
                    }
                    break;

                case "MODN":
                    modn = r.ReadBytes((int)size);
                    break;

                case "MODS":
                    int nSets = (int)size / 32;
                    for (int i = 0; i < nSets; i++)
                    {
                        stream.Position = start + i * 32;
                        var nameBytes = r.ReadBytes(20);
                        int z = Array.IndexOf(nameBytes, (byte)0); if (z < 0) z = 20;
                        root.DoodadSetNames.Add(Encoding.ASCII.GetString(nameBytes, 0, z));
                        int setStart = (int)r.ReadUInt32();
                        int setCount = (int)r.ReadUInt32();
                        root.DoodadSets.Add((setStart, setCount));
                    }
                    break;

                case "MODD":
                    int nDD = (int)size / 40;
                    for (int i = 0; i < nDD; i++)
                    {
                        stream.Position = start + i * 40;
                        uint nameOfs = r.ReadUInt32() & 0xFFFFFF;
                        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        float scale = r.ReadSingle();
                        uint color = r.ReadUInt32();
                        doodadRaw.Add((nameOfs, pos, rot, scale, color));
                    }
                    break;
            }
            stream.Position = start + size;
        }

        foreach (var ofs in materialTexOffsets)
            root.MaterialTextures.Add(ReadStringAt(motx, ofs));

        foreach (var (nameOfs, pos, rot, scale, color) in doodadRaw)
            root.Doodads.Add(new InteriorDoodad
            {
                Path = ReadStringAt(modn, nameOfs),
                Position = pos,
                Rotation = rot,
                Scale = scale,
                Color = color,
            });

        return root;
    }

    public static Group? ParseGroup(Stream stream, Root root)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        float[] pos = [], nrm = [], uv = [];
        ushort[] vi = [];
        byte[] cv = [];
        var batches = new List<(int start, int count, int material)>();

        stream.Position = 0;
        while (stream.Position < stream.Length - 8)
        {
            string magic = ReadMagic(r);
            uint size = r.ReadUInt32();
            long start = stream.Position;

            if (magic == "MOGP")
            {
                stream.Position = start + 68;
                while (stream.Position < start + size - 8)
                {
                    string sm = ReadMagic(r);
                    uint ss = r.ReadUInt32();
                    long sstart = stream.Position;
                    switch (sm)
                    {
                        case "MOVT":
                            pos = ReadFloats(r, (int)ss / 4);
                            break;
                        case "MONR":
                            nrm = ReadFloats(r, (int)ss / 4);
                            break;
                        case "MOTV":
                            uv = ReadFloats(r, (int)ss / 4);
                            break;
                        case "MOVI":
                            vi = new ushort[ss / 2];
                            for (int i = 0; i < vi.Length; i++) vi[i] = r.ReadUInt16();
                            break;
                        case "MOCV":
                            cv = r.ReadBytes((int)ss);
                            break;
                        case "MOBA":
                            int nB = (int)ss / 24;
                            for (int i = 0; i < nB; i++)
                            {
                                stream.Position = sstart + i * 24 + 12;
                                uint startIndex = r.ReadUInt32();
                                ushort count = r.ReadUInt16();
                                stream.Position = sstart + i * 24 + 23;
                                byte materialId = r.ReadByte();
                                batches.Add(((int)startIndex, count, materialId));
                            }
                            break;
                    }
                    stream.Position = sstart + ss;
                }
                break;
            }
            stream.Position = start + size;
        }

        if (pos.Length == 0 || vi.Length == 0) return null;

        int nVerts = pos.Length / 3;
        bool hasCv = cv.Length >= nVerts * 4;
        var verts = new float[nVerts * GroupVertexStride];
        for (int i = 0; i < nVerts; i++)
        {
            int o = i * GroupVertexStride;
            verts[o + 0] = pos[i * 3]; verts[o + 1] = pos[i * 3 + 1]; verts[o + 2] = pos[i * 3 + 2];
            if (nrm.Length >= (i * 3 + 3)) { verts[o + 3] = nrm[i * 3]; verts[o + 4] = nrm[i * 3 + 1]; verts[o + 5] = nrm[i * 3 + 2]; }
            if (uv.Length >= (i * 2 + 2)) { verts[o + 6] = uv[i * 2]; verts[o + 7] = uv[i * 2 + 1]; }
            if (hasCv)
            {
                verts[o + 8]  = cv[i * 4 + 2] / 255f;
                verts[o + 9]  = cv[i * 4 + 1] / 255f;
                verts[o + 10] = cv[i * 4 + 0] / 255f;
                verts[o + 11] = cv[i * 4 + 3] / 255f;
            }
            else { verts[o + 8] = verts[o + 9] = verts[o + 10] = verts[o + 11] = 1f; }
        }

        var indices = new uint[vi.Length];
        for (int i = 0; i < vi.Length; i++) indices[i] = vi[i];

        var g = new Group { Vertices = verts, Indices = indices, HasVertexColors = hasCv };
        foreach (var (s, c, mat) in batches)
        {
            string tex = mat >= 0 && mat < root.MaterialTextures.Count ? root.MaterialTextures[mat] : "";
            bool alphaTest = mat >= 0 && mat < root.MaterialBlend.Count && root.MaterialBlend[mat] != 0;
            g.Batches.Add(new Batch { IndexStart = s, IndexCount = c, TexturePath = tex, AlphaTest = alphaTest });
        }
        return g;
    }

    private static string ReadMagic(BinaryReader r)
    {
        var b = r.ReadBytes(4);
        Array.Reverse(b);
        return Encoding.ASCII.GetString(b);
    }

    private static float[] ReadFloats(BinaryReader r, int count)
    {
        var a = new float[count];
        for (int i = 0; i < count; i++) a[i] = r.ReadSingle();
        return a;
    }

    private static string ReadStringAt(byte[] blob, uint offset)
    {
        if (offset >= blob.Length) return "";
        int end = Array.IndexOf(blob, (byte)0, (int)offset);
        if (end < 0) end = blob.Length;
        return Encoding.UTF8.GetString(blob, (int)offset, end - (int)offset);
    }
}
