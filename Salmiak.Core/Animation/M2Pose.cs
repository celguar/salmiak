using System.Numerics;
using System.Collections.Generic;
using Salmiak.Core.Formats;

namespace Salmiak.Core.Animation;

// Classic (v.256/257) bone-pose evaluation. A bone's local transform is the pivot
// rotation/scaling around the pivot plus the sampled translation:
// T(-pivot) * S * R * T(pivot) * T(translation), then composed with the parent bone.
// Matrices are returned in System.Numerics' row-vector convention; their elements happen to
// sit in the order GL's column-major uniforms expect, so the renderer uploads them straight
// through without transposing (validated by the pose probe).
public sealed class M2Pose
{
    // How many bone slots one model may use, which is the width of the shader's uBones array.
    // Measured over the 7480 classic models of the 1.12 client's model.MPQ whose vertices name
    // bones at all, counting the distinct bones a vertex binds once the vertex bytes are taken as
    // bones themselves: the busiest skeleton is transportship_sails.m2 with 176 slots, and only
    // five models pass 128 at all (transportship_sails 176, FloorCandlesCorner01 in its three
    // colours at 148 and Ghost.m2 at 129), so 192 covers the whole archive and a model over the
    // cap keeps the bind pose the shadow baker ray-traced instead of a wrong pose.
    private const int MaxBoneSlots = 192;

    private readonly M2File _m2;
    private readonly M2TrackSampler _sampler;
    private readonly List<M2Bone> _bones;
    private readonly Matrix4x4[] _world;
    private readonly bool[] _done;
    private readonly bool[] _visiting;
    private readonly float[] _weights;
    private int[] _slot = [];   // bone -> uBones slot, -1 when the mesh never uses it
    private int[] _bone = [];   // slot -> bone
    private int[] _resolved = []; // vertex influence -> bone, the bone its byte names
    private int _slotCount;

    public M2Pose(M2File m2)
    {
        _m2 = m2;
        _bones = m2.Bones;
        _sampler = new M2TrackSampler(_bones, m2.Raw);
        _world = new Matrix4x4[_bones.Count];
        for (int i = 0; i < _world.Length; i++) _world[i] = Matrix4x4.Identity;
        _done = new bool[_bones.Count];
        _visiting = new bool[_bones.Count];
        _weights = m2.BoneWeights;
        ResolveVertexBones(m2);
        BuildBoneSlots();
    }

    public int BoneCount() => _bones.Count;

    public int SequenceCount() => _m2.SequenceCount();

    public uint SequenceAnimId(int sequence)
        => sequence >= 0 && sequence < _m2.SequenceCount() ? _m2.Sequences[sequence].AnimId : 0u;

    public bool Animates() => _sampler.HasKeys() && _slotCount > 0;

    // One playback cycle of a sequence in milliseconds, 0 when the record is empty.
    public int SequenceDurationMs(int sequence)
    {
        if (sequence < 0 || sequence >= _m2.SequenceCount()) return 0;
        var s = _m2.Sequences[sequence];
        long duration = (long)s.TimeEnd - (long)s.TimeStart;
        return duration > 0 && duration < int.MaxValue ? (int)duration : 0;
    }

    public void Evaluate(int sequence, int elapsedMs)
    {
        if (_bones.Count == 0) return;

        int start = 0;
        if (sequence >= 0 && sequence < _m2.SequenceCount())
            start = (int)_m2.Sequences[sequence].TimeStart;

        var duration = SequenceDurationMs(sequence);
        var time = elapsedMs;
        if (duration > 0)
        {
            time %= duration;
            if (time < 0) time += duration;
        }
        time += start;

        // The per-bone cache only holds for one pose, so clear it before every evaluation.
        for (int bone = 0; bone < _bones.Count; bone++) { _done[bone] = false; _visiting[bone] = false; }
        for (int bone = 0; bone < _bones.Count; bone++) Solve(bone, sequence, time);
    }

    public Matrix4x4 World(int bone) => bone >= 0 && bone < _world.Length ? _world[bone] : Matrix4x4.Identity;

    public Matrix4x4[] Matrices() => _world;

    // Bones the mesh actually references, packed into uBones slots.
    public int SlotCount() => _slotCount;

    public int BoneOfSlot(int slot) => slot >= 0 && slot < _slotCount ? _bone[slot] : -1;

    public int SlotOfBone(int bone) => bone >= 0 && bone < _slot.Length ? _slot[bone] : -1;

    // The vertex influence's bone: its byte names it directly, and only a byte outside the
    // model's bones goes through the submesh's window.
    public int ResolvedBone(int influence) =>
        influence >= 0 && influence < _resolved.Length ? _resolved[influence] : -1;

    // The vertex record's bone byte names the bone itself; the submesh's palette window
    // (BoneLookupTable [BoneOffset, BoneOffset + BoneCount)) only renumbers the bones into the
    // shader's slots. Benilla's records.md calls the vertex field "four bone lookup bytes", shared.md
    // says the client replaces the header's vertex record with the view's palette-resolved one when
    // it builds a copy's vertex buffer, and render.md loads the palette for submesh +0xe of +0xc
    // bones and indexes it with the vertex's bone bytes. That makes the byte the slot, not the bone.
    // The data says which way to go: over the 2194 classic models in the 1.12 client's model.MPQ
    // that have more than one bone and a view, every vertex bone byte with a nonzero weight names
    // a bone the model has and is listed in the window of the submesh that draws it, so a slot for
    // it always exists and the window is only a renaming. And
    // DurotarPalm01's 56 trunk vertices all carry byte 0, which is the model's unkeyed bone 0, so
    // the byte keeps the trunk still and moves only the 90 frond vertices whose bytes name the keyed
    // bones 1..12; reading the byte through the window instead gives every vertex some keyed bone
    // from bone 1 up and makes the whole tree sway from its base, which the client does not do.
    private void ResolveVertexBones(M2File m2)
    {
        var lut = m2.BoneLookupTable;
        var indices = m2.BoneIndices;
        int influences = Math.Max(0, Math.Min(indices.Length, m2.BoneWeights.Length));
        _resolved = new int[influences];
        for (int i = 0; i < influences; i++) _resolved[i] = -1;
        if (influences == 0 || _bones.Count == 0) return;

        // The bytes are bones, so nothing but a byte that is past the model's bones needs the
        // submesh's window. Because that is rare, the owner array that maps a vertex to the
        // submesh whose index range draws it is built only when such a byte appears.
        for (int i = 0; i < influences; i++)
        {
            int local = indices[i];
            if (local >= 0 && local < _bones.Count) _resolved[i] = local;
        }

        int outside = 0;
        for (int i = 0; i < influences; i++) if (_resolved[i] < 0 && indices[i] >= 0) outside++;
        if (outside == 0) return;

        int vertices = influences / 4;
        var owner = new int[vertices];
        for (int i = 0; i < vertices; i++) owner[i] = -1;
        for (int s = 0; s < m2.Submeshes.Count; s++)
        {
            var sm = m2.Submeshes[s];
            int end = Math.Min(Math.Max(0, sm.IndexStart + sm.IndexCount), m2.Indices.Length);
            for (int j = Math.Max(0, sm.IndexStart); j < end; j++)
            {
                int v = (int)m2.Indices[j];
                if (v >= 0 && v < vertices && owner[v] < 0) owner[v] = s;
            }
        }

        for (int i = 0; i < influences; i++)
        {
            if (_resolved[i] >= 0) continue;
            int local = indices[i];
            if (local < 0) continue;
            // A byte outside the model's bones read through the submesh's window, then through the
            // table at the byte itself: the owner of a vertex comes from the index range every
            // submesh draws, because this reader keeps the header's own vertex order while a stored
            // range counts into the view's lookup order.
            int bone = -1;
            int v = i / 4;
            int s = v < owner.Length ? owner[v] : -1;
            if (s >= 0)
            {
                var sm = m2.Submeshes[s];
                int entry = sm.BoneOffset + local;
                if (entry >= 0 && entry < lut.Length) bone = lut[entry];
            }
            if (bone < 0 && local < lut.Length) bone = lut[local];
            _resolved[i] = bone >= 0 && bone < _bones.Count ? bone : -1;
        }
    }

    private void BuildBoneSlots()
    {
        _slot = new int[Math.Max(0, _bones.Count)];
        for (int i = 0; i < _slot.Length; i++) _slot[i] = -1;
        _bone = new int[Math.Min(MaxBoneSlots, Math.Max(0, _bones.Count))];
        _slotCount = 0;
        if (_resolved.Length == 0) return;

        // More influences than the shader's uBones array means the mesh cannot be skinned
        // correctly; leaving every slot at -1 keeps it in the bind pose we baked. Measured over the
        // 1.12 client's models in model.MPQ with the bytes bound as bones, five of the 7480 whose
        // vertices name bones need more than 128 slots and the busiest needs 176, so the cap of
        // 192 covers the whole archive.
        if (CountUsed() > _bone.Length) return;

        for (int i = 0; i < _resolved.Length; i++)
        {
            if (i >= _weights.Length || _weights[i] <= 0f) continue;
            int b = _resolved[i];
            if (b < 0 || b >= _bones.Count || _slot[b] >= 0) continue;
            _slot[b] = _slotCount;
            _bone[_slotCount++] = b;
        }
    }

    private int CountUsed()
    {
        var seen = new bool[Math.Max(1, _bones.Count)];
        int used = 0;
        for (int i = 0; i < _resolved.Length; i++)
        {
            if (i >= _weights.Length || _weights[i] <= 0f) continue;
            int b = _resolved[i];
            if (b < 0 || b >= _bones.Count || seen[b]) continue;
            seen[b] = true;
            used++;
        }
        return used;
    }

    private void Solve(int bone, int sequence, int timeMs)
    {
        if (bone < 0 || bone >= _bones.Count || _done[bone]) return;
        if (_visiting[bone]) { _done[bone] = true; return; } // breaks malformed parent cycles
        _visiting[bone] = true;

        var b = _bones[bone];
        var parent = Matrix4x4.Identity;
        if (b.Parent >= 0 && b.Parent < _bones.Count)
        {
            Solve(b.Parent, sequence, timeMs);
            parent = _world[b.Parent];
        }
        _visiting[bone] = false;

        var pivot = b.Pivot;
        var translation = _sampler.Translation(bone, sequence, timeMs);
        var rotation = _sampler.Rotation(bone, sequence, timeMs);
        var scaling = _sampler.Scaling(bone, sequence, timeMs);

        var local = Matrix4x4.CreateTranslation(new(-pivot.X, -pivot.Y, -pivot.Z))
            * Matrix4x4.CreateScale(scaling)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(pivot)
            * Matrix4x4.CreateTranslation(translation);

        _world[bone] = local * FilterParentTransform(parent, b);
        _done[bone] = true;
    }

    // Bone flags let a bone skip part of its parent's transform (bit 1 translation,
    // bit 2 scaling, bit 4 rotation).
    private static Matrix4x4 FilterParentTransform(Matrix4x4 parentWorld, M2Bone bone)
    {
        if (bone.Parent < 0) return Matrix4x4.Identity;
        if (!bone.IgnoresParentTranslation && !bone.IgnoresParentScale && !bone.IgnoresParentRotation)
            return parentWorld;

        if (!Matrix4x4.Decompose(parentWorld, out Vector3 parentScale, out Quaternion parentRotation, out Vector3 parentTranslation))
            return parentWorld;

        if (bone.IgnoresParentScale) parentScale = Vector3.One;
        if (bone.IgnoresParentRotation) parentRotation = Quaternion.Identity;
        if (bone.IgnoresParentTranslation) parentTranslation = Vector3.Zero;

        return Matrix4x4.CreateScale(parentScale)
            * Matrix4x4.CreateFromQuaternion(parentRotation)
            * Matrix4x4.CreateTranslation(parentTranslation);
    }
}
