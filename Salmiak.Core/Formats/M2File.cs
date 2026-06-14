using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class M2File
{
    public const int VertexStride = 48;

    public float[] Vertices { get; private set; } = [];

    public uint[] Indices { get; private set; } = [];

    public List<string> Textures { get; } = new();

    public List<uint> TextureTypes { get; } = new();

    public List<M2Submesh> Submeshes { get; } = new();

    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    public static M2File? Parse(Stream stream)
    {
        var m2 = new M2File();
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        stream.Position = 0;
        uint magic = r.ReadUInt32();
        if (magic != 0x3032444D) return null;
        uint version = r.ReadUInt32();
        if (version is not (256 or 257)) return null;

        uint U(int idx) { stream.Position = idx * 4; return r.ReadUInt32(); }

        uint nVertices  = U(17), ofsVertices = U(18);
        uint nViews     = U(19), ofsViews    = U(20);
        uint nTextures  = U(23), ofsTextures = U(24);
        uint nTexLookup = U(37), ofsTexLookup = U(38);

        if (nVertices == 0 || nViews == 0) return null;

        var texLookup = new ushort[nTexLookup];
        stream.Position = ofsTexLookup;
        for (int i = 0; i < nTexLookup; i++) texLookup[i] = r.ReadUInt16();

        var verts = new float[nVertices * 8];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < nVertices; i++)
        {
            stream.Position = ofsVertices + (long)i * VertexStride;
            float px = r.ReadSingle(), py = r.ReadSingle(), pz = r.ReadSingle();
            r.ReadUInt32(); r.ReadUInt32();
            float nx = r.ReadSingle(), ny = r.ReadSingle(), nz = r.ReadSingle();
            float u = r.ReadSingle(), v = r.ReadSingle();

            int o = i * 8;
            verts[o + 0] = px; verts[o + 1] = py; verts[o + 2] = pz;
            verts[o + 3] = nx; verts[o + 4] = ny; verts[o + 5] = nz;
            verts[o + 6] = u;  verts[o + 7] = v;

            min = Vector3.Min(min, new Vector3(px, py, pz));
            max = Vector3.Max(max, new Vector3(px, py, pz));
        }
        m2.Vertices = verts;
        m2.BoundsMin = min;
        m2.BoundsMax = max;

        stream.Position = ofsViews;
        uint nIndex = r.ReadUInt32(), ofsIndex = r.ReadUInt32();
        uint nTris  = r.ReadUInt32(), ofsTris  = r.ReadUInt32();
        r.ReadUInt32(); r.ReadUInt32();
        uint nSub   = r.ReadUInt32(), ofsSub   = r.ReadUInt32();
        uint nTexU  = r.ReadUInt32(), ofsTexU  = r.ReadUInt32();

        var lookup = new ushort[nIndex];
        stream.Position = ofsIndex;
        for (int i = 0; i < nIndex; i++) lookup[i] = r.ReadUInt16();

        var indices = new uint[nTris];
        stream.Position = ofsTris;
        for (int i = 0; i < nTris; i++)
        {
            ushort local = r.ReadUInt16();
            indices[i] = local < lookup.Length ? lookup[local] : (uint)0;
        }
        m2.Indices = indices;

        for (int i = 0; i < nTextures; i++)
        {
            stream.Position = ofsTextures + (long)i * 16;
            uint type    = r.ReadUInt32();
            r.ReadUInt32();
            uint lenName = r.ReadUInt32();
            uint ofsName = r.ReadUInt32();
            if (type == 0 && lenName > 1 && ofsName > 0 && ofsName + lenName <= stream.Length)
            {
                stream.Position = ofsName;
                var bytes = r.ReadBytes((int)lenName);
                int end = Array.IndexOf(bytes, (byte)0);
                m2.Textures.Add(Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end));
            }
            else m2.Textures.Add("");
            m2.TextureTypes.Add(type);
        }

        var submeshTexture = new Dictionary<int, int>();
        for (int i = 0; i < nTexU; i++)
        {
            stream.Position = ofsTexU + (long)i * 24 + 4;
            int submeshIndex = r.ReadUInt16();
            stream.Position = ofsTexU + (long)i * 24 + 16;
            int textureId = r.ReadUInt16();
            submeshTexture.TryAdd(submeshIndex, textureId);
        }

        for (int i = 0; i < nSub; i++)
        {
            stream.Position = ofsSub + (long)i * 32;
            int meshId = r.ReadUInt16();
            stream.Position = ofsSub + (long)i * 32 + 8;
            int triStart = r.ReadUInt16();
            int triCount = r.ReadUInt16();
            int lookupId = submeshTexture.TryGetValue(i, out var t) ? t : 0;
            int texId = lookupId >= 0 && lookupId < texLookup.Length ? texLookup[lookupId] : lookupId;
            string path = texId >= 0 && texId < m2.Textures.Count ? m2.Textures[texId] : "";
            m2.Submeshes.Add(new M2Submesh
            {
                MeshId = meshId,
                IndexStart = triStart,
                IndexCount = triCount,
                TextureIndex = texId,
                TexturePath = path,
                TextureType = texId >= 0 && texId < m2.TextureTypes.Count ? m2.TextureTypes[texId] : 0u,
            });
        }

        return m2;
    }
}

public struct M2Submesh
{
    public int MeshId;
    public int IndexStart;
    public int IndexCount;
    public int TextureIndex;
    public string TexturePath;
    public uint TextureType;
}
