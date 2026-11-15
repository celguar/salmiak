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

    // Classic animation data. Bones/sequences are kept as raw bytes plus track offsets:
    // posing happens at render time (wow-viewer's lazy design), so nothing is pre-decoded here.
    public List<M2Sequence> Sequences { get; } = new();

    // The header's animation-id lookup: the entry at an animation id names the sequence record this
    // model plays for it. Benilla's fields.tsv calls the header pair at +0x24/+0x28 m_nSequenceLookup
    // and m_sequenceLookup, shared.md's header-fixup table gives 2-byte entries for that pair, and
    // animation.md's ResolveAnimationId reads entry 0 (animation 0, which the client's own name table
    // at 0x8686a8 calls Stand) and entry 0x93 while playback.md checks the value it gets against the
    // sequence count, so the entry is a sequence index and 0xffff means unmapped. Measured over the
    // 1.12 client's NPC models: Raptor.m2 maps entry 0 to sequence 2, whose own record carries
    // animation id 0, while its sequence 0 is id 5 (Run) and its sequence 1 is id 4 (Walk); NagaMale
    // maps 0 to sequence 2 as well behind a Walk first; TrollMale maps 0 to sequence 108. So the
    // sequence a creature stands on is rarely the first one, which is why playing "the first sequence
    // that has a length" has those creatures run or walk in place instead of standing as the client
    // does, while the human race models, whose sequence 0 is already Stand, look right either way.
    public ushort[] SequenceLookup { get; private set; } = [];

    public List<M2Bone> Bones { get; } = new();

    public ushort[] KeyBoneLookup { get; private set; } = [];

    public ushort[] BoneLookupTable { get; private set; } = [];

    // Per-vertex influences, 4 entries per vertex. Weights are normalised to 0..1.
    public float[] BoneWeights { get; private set; } = [];
    public byte[] BoneIndices { get; private set; } = [];

    // The view's own copy of the bone bytes, 4 per vertex in its own vertex order, which are the
    // palette slots the submesh windows name rather than bones (see the parse and M2Pose).
    public byte[] ViewBoneBytes { get; private set; } = [];

    // Texture animations (uv offset/rotation/scale), sampled at render time like the bones.
    public List<M2TextureAnim> TextureAnimations { get; } = new();

    // Loop lengths (ms) of the global sequences a track may be tied to.
    public uint[] GlobalSequences { get; private set; } = [];

    public byte[] Raw { get; private set; } = [];

    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    public int SequenceCount() => Sequences.Count;

    public int BoneCount() => Bones.Count;

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
        uint nTexAnims  = U(29), ofsTexAnims = U(30);
        uint nTexAnimLookup = U(43), ofsTexAnimLookup = U(44);

        if (nVertices == 0 || nViews == 0) return null;

        // Classic animation header (Salmiak only ever reads v.256/257 M2s, so the
        // classic field order and record sizes apply unchanged).
        uint nSequences = U(7), ofsSequences = U(8);
        uint nSeqLookup = U(9),  ofsSeqLookup = U(10);
        uint nBones     = U(13), ofsBones    = U(14);
        uint nKeyBones  = U(15), ofsKeyBones = U(16);
        uint nBoneLut   = U(35), ofsBoneLut  = U(36);
        uint nGlobalSeq = U(5),  ofsGlobalSeq = U(6);

        stream.Position = 0;
        var raw = r.ReadBytes((int)stream.Length);
        m2.Raw = raw;

        if (ofsGlobalSeq > 0 && ofsGlobalSeq + (long)nGlobalSeq * 4 <= stream.Length)
        {
            var global = new uint[nGlobalSeq];
            stream.Position = ofsGlobalSeq;
            for (int i = 0; i < nGlobalSeq; i++) global[i] = r.ReadUInt32();
            m2.GlobalSequences = global;
        }

        // One 2-byte entry per animation id, holding the sequence record to play for it.
        if (ofsSeqLookup > 0 && ofsSeqLookup + (long)nSeqLookup * 2 <= stream.Length)
        {
            var seqLookup = new ushort[nSeqLookup];
            stream.Position = ofsSeqLookup;
            for (int i = 0; i < nSeqLookup; i++) seqLookup[i] = r.ReadUInt16();
            m2.SequenceLookup = seqLookup;
        }

        var texLookup = new ushort[nTexLookup];
        stream.Position = ofsTexLookup;
        for (int i = 0; i < nTexLookup; i++) texLookup[i] = r.ReadUInt16();

        // A texture unit's texanim field indexes this table, and the table holds the index of
        // the texture animation the unit uses (-1 means the unit is not animated). Measured on
        // the 1.12 client's models, e.g. the Elwynn waterfall: two units, lookups [0, 1].
        var texAnimLookup = new ushort[nTexAnimLookup];
        if (ofsTexAnimLookup > 0 && ofsTexAnimLookup + (long)nTexAnimLookup * 2 <= stream.Length)
        {
            stream.Position = ofsTexAnimLookup;
            for (int i = 0; i < nTexAnimLookup; i++) texAnimLookup[i] = r.ReadUInt16();
        }

        var verts = new float[nVertices * 8];
        var weights = new float[nVertices * 4];
        var boneIdx = new byte[nVertices * 4];

        // The bone lookup table holds the bone-combo windows the client loads as a batch's palette:
        // render.md says the palette is "header +0x90 lookups from submesh +0xe for +0xc bones", so
        // a submesh's window is BoneLookupTable[BoneOffset, BoneOffset + BoneCount) with BoneOffset
        // read from the submesh's own first-bone field and BoneCount from the one before it. The
        // window lists the bones the submesh uses and says which shader slot each one goes to:
        // benilla's records.md calls the vertex record's four bytes "bone lookup bytes" (records.md
        // vertex +0x14) because the client overwrites the header's vertex record with the view's
        // resolved one when it builds a copy's vertex buffer (shared.md), and the view's own copy
        // (its +0x14 count of 4-byte records, +0x18 offset) holds slots: the window entry at the
        // slot is the header's byte. DurotarPalm01 shows it at one vertex: its 13-bone window is
        // the permutation 1 7 11 5 6 12 3 9 10 4 2 8 0, bone 1 sits at slot 0 and bone 0 at slot 12,
        // and its frond vertices hold header byte 1 with view byte 0 while its trunk vertices hold
        // header byte 0 with view byte 12. This reader binds the header's bytes as bones and numbers
        // its own slots, so the view's copy is kept only to show the two agree. Measured over the
        // 2194 classic models in the 1.12 client's model.MPQ that have more than one bone and a
        // view: no submesh ever stores a nonzero first bone, no window reaches past the table or
        // names a bone the model lacks, every vertex bone byte with a nonzero weight names a bone
        // the model has (largest byte 175) and is listed in the window of the submesh that draws it,
        // and 1045 of those models have a window that is a real permutation of its bones. M2Pose
        // binds the bytes as bones and BuildBoneSlots gives each bone its slot in the model's own
        // palette, which is what the shader's uBones array is indexed by.
        var boneLut = new ushort[0];
        if (ofsBoneLut > 0 && ofsBoneLut + (long)nBoneLut * 2 <= stream.Length)
        {
            boneLut = new ushort[nBoneLut];
            stream.Position = ofsBoneLut;
            for (int i = 0; i < nBoneLut; i++) boneLut[i] = r.ReadUInt16();
        }
        m2.BoneLookupTable = boneLut;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < nVertices; i++)
        {
            stream.Position = ofsVertices + (long)i * VertexStride;
            float px = r.ReadSingle(), py = r.ReadSingle(), pz = r.ReadSingle();
            for (int k = 0; k < 4; k++) weights[i * 4 + k] = r.ReadByte() / 255f;
            for (int k = 0; k < 4; k++) boneIdx[i * 4 + k] = r.ReadByte();
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
        uint nProps = r.ReadUInt32(), ofsProps = r.ReadUInt32();
        uint nSub   = r.ReadUInt32(), ofsSub   = r.ReadUInt32();
        uint nTexU  = r.ReadUInt32(), ofsTexU  = r.ReadUInt32();

        var lookup = new ushort[nIndex];
        stream.Position = ofsIndex;
        for (int i = 0; i < nIndex; i++) lookup[i] = r.ReadUInt16();

        // Classic vertex records carry the bone influences inline (boneWeight[4]/boneIndex[4]
        // at offsets 12..19), so the per-view array here is not skinning data on top of them:
        // fixtures store it as 4 bytes per vertex (BurntStoneTree07: 113 vertices, 464 bytes for
        // the array), which is the view's own copy of the bone bytes the draw reads.

        // The view keeps its own copy of the bone bytes, four per vertex, and these are the palette
        // slots rather than the bones: the entry of the submesh's window at the slot is the header's
        // bone byte for that vertex, so a slot for every bone always exists (DurotarPalm01: its
        // lookup table is the 13-bone permutation 1 7 11 5 6 12 3 9 10 4 2 8 0 repeated four times,
        // one window per submesh, bone 1 sits at slot 0 and bone 0 at slot 12, and the view's bytes
        // for the vertices whose header byte is 1 and 0 are exactly 0 and 12). This reader binds the
        // header's bytes as bones and numbers its own slots, so the copy is kept only to show that
        // reading the byte through the window renames bones instead of placing them, which is what
        // made the palm's whole trunk sway from its base instead of only its fronds.
        var viewBones = new byte[0];
        if (ofsProps > 0 && ofsProps + nProps * 4 <= stream.Length)
        {
            viewBones = new byte[(int)nProps * 4];
            stream.Position = ofsProps;
            for (int i = 0; i < viewBones.Length; i++) viewBones[i] = r.ReadByte();
        }
        m2.ViewBoneBytes = viewBones;

        // A triangle's ushort index counts into the view's vertex array, whose entries name the
        // header's vertex record to draw. Measured over all 7480 classic models of the 1.12 client's
        // model.MPQ whose views carry one: the array is the identity in every one of them, so the
        // draw reads the header's own vertex order and the positions, the weights and the bone bytes
        // this reader keeps in that order need no reordering of their own.

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
        var submeshUvAnim = new Dictionary<int, int>();
        for (int i = 0; i < nTexU; i++)
        {
            stream.Position = ofsTexU + (long)i * 24 + 4;
            int submeshIndex = r.ReadUInt16();
            stream.Position = ofsTexU + (long)i * 24 + 16;
            int textureId = r.ReadUInt16();
            submeshTexture.TryAdd(submeshIndex, textureId);

            // A unit's texture animation is looked up twice: the unit's texanim field indexes the
            // texAnimLookup table, whose entry is the index of the animation record. A unit whose
            // flags carry bit 16 is explicitly not animated, and a lookup of -1 also means none.
            stream.Position = ofsTexU + (long)i * 24;
            int unitFlags = r.ReadUInt16();
            stream.Position = ofsTexU + (long)i * 24 + 22;
            int animLookup = r.ReadInt16();
            var lookupId = animLookup >= 0 && animLookup < texAnimLookup.Length ? texAnimLookup[animLookup] : -1;
            int anim = lookupId == 0xFFFF ? -1 : lookupId;
            if ((unitFlags & 16) == 0 && anim >= 0) submeshUvAnim.TryAdd(submeshIndex, anim);
        }

        for (int i = 0; i < nSub; i++)
        {
            stream.Position = ofsSub + (long)i * 32;
            int meshId = r.ReadUInt16();
            r.ReadUInt16();
            int vtxStart = r.ReadUInt16();
            int vtxCount = r.ReadUInt16();
            int triStart = r.ReadUInt16();
            int triCount = r.ReadUInt16();
            int boneCount = r.ReadUInt16();
            int boneOffset = r.ReadUInt16();
            int lookupId = submeshTexture.TryGetValue(i, out var t) ? t : 0;
            int texId = lookupId >= 0 && lookupId < texLookup.Length ? texLookup[lookupId] : lookupId;
            string path = texId >= 0 && texId < m2.Textures.Count ? m2.Textures[texId] : "";
            m2.Submeshes.Add(new M2Submesh
            {
                MeshId = meshId,
                IndexStart = triStart,
                IndexCount = triCount,
                VertexStart = vtxStart,
                VertexCount = vtxCount,
                BoneCount = boneCount,
                BoneOffset = boneOffset,
                TextureIndex = texId,
                TexturePath = path,
                TextureType = texId >= 0 && texId < m2.TextureTypes.Count ? m2.TextureTypes[texId] : 0u,
                UvAnim = submeshUvAnim.TryGetValue(i, out var uvAnim) ? uvAnim : -1,
            });
        }

        m2.BoneWeights = weights;
        m2.BoneIndices = boneIdx;

        for (int i = 0; i < nSequences; i++)
        {
            stream.Position = ofsSequences + (long)i * 0x44;
            m2.Sequences.Add(new M2Sequence
            {
                AnimId = r.ReadUInt32(),
                TimeStart = r.ReadUInt32(),
                TimeEnd = r.ReadUInt32(),
                MoveSpeed = r.ReadSingle(),
                LoopType = r.ReadUInt32(),
                Flags = r.ReadUInt32(),
                Unknown1 = r.ReadUInt32(),
                Unknown2 = r.ReadUInt32(),
                PlaySpeed = r.ReadUInt32(),
                BoxMin = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                BoxMax = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Radius = r.ReadSingle(),
                SubId = r.ReadInt16(),
                Unknown3 = r.ReadInt16(),
            });
        }

        M2Track Track(long ofs)
        {
            stream.Position = ofs;
            return new M2Track
            {
                InterpolationType = r.ReadInt16(),
                GlobalSequence = r.ReadInt16(),
                RangeCount = (int)r.ReadUInt32(),
                RangeOffset = (int)r.ReadUInt32(),
                TimeCount = (int)r.ReadUInt32(),
                TimeOffset = (int)r.ReadUInt32(),
                KeyCount = (int)r.ReadUInt32(),
                KeyOffset = (int)r.ReadUInt32(),
            };
        }

        for (int i = 0; i < nBones; i++)
        {
            long bone = ofsBones + (long)i * 0x6C;
            stream.Position = bone;
            int keyBoneId = r.ReadInt32();
            uint flags = r.ReadUInt32();
            short parent = r.ReadInt16();
            short geoset = r.ReadInt16();
            var translation = Track(bone + 0x0C);
            var rotation = Track(bone + 0x28);
            var scaling = Track(bone + 0x44);
            stream.Position = bone + 0x60;
            m2.Bones.Add(new M2Bone
            {
                KeyBoneId = keyBoneId,
                Flags = flags,
                Parent = parent,
                Geoset = geoset,
                Translation = translation,
                Rotation = rotation,
                Scaling = scaling,
                Pivot = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            });
        }

        if (ofsKeyBones + (long)nKeyBones * 2 <= stream.Length)
        {
            m2.KeyBoneLookup = new ushort[nKeyBones];
            stream.Position = ofsKeyBones;
            for (int i = 0; i < nKeyBones; i++) m2.KeyBoneLookup[i] = r.ReadUInt16();
        }

        // Texture animations are three track blocks each (the uv offset, rotation and scale), so
        // 84 bytes per record; each track keeps its own key ranges and times like a bone track,
        // which is what makes the water scroll inside the sequence it was authored for.
        if (ofsTexAnims > 0 && ofsTexAnims + (long)nTexAnims * 84 <= stream.Length)
        {
            for (int i = 0; i < nTexAnims; i++)
            {
                long record = ofsTexAnims + (long)i * 84;
                m2.TextureAnimations.Add(new M2TextureAnim
                {
                    Translation = Track(record),
                    Rotation = Track(record + 28),
                    Scaling = Track(record + 56),
                });
            }
        }

        return m2;
    }
}

public struct M2Submesh
{
    public int MeshId;
    public int IndexStart;
    public int IndexCount;

    // Rest of the classic submesh record, kept for reference: the vertex range it draws, how
    // many bones it uses and where its bone window starts in BoneLookupTable.
    public int VertexStart;
    public int VertexCount;
    public int BoneCount;
    public int BoneOffset;

    public int TextureIndex;
    public string TexturePath;
    public uint TextureType;

    // Index of the texture animation this submesh's texture unit uses, or -1 when the unit is
    // not animated. Measured on the 1.12 client's doodads: the waterfall's two units look up
    // animations 0 and 1, whose uv offset keys scroll the water.
    public int UvAnim;
}
