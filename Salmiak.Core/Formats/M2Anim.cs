using System.Numerics;

namespace Salmiak.Core.Formats;

// Classic (v.256/257) M2 animation records.
// Header pairs: 0x1C nSequences / 0x20 ofsSequences, 0x34 nBones / 0x38 ofsBones,
// 0x3C nKeyBones / 0x40 ofsKeyBones, 0x8C boneLookupTable count/offset.
// Sequence record = 0x44 bytes, bone record = 0x6C bytes, track block = 0x1C bytes.

public struct M2Sequence
{
    public uint AnimId;
    public uint TimeStart;
    public uint TimeEnd;
    public float MoveSpeed;
    public uint LoopType;
    public uint Flags;
    public uint Unknown1;
    public uint Unknown2;
    public uint PlaySpeed;
    public Vector3 BoxMin;
    public Vector3 BoxMax;
    public float Radius;
    public short SubId;
    public short Unknown3;
}

public struct M2Bone
{
    public int KeyBoneId;   // -1 for regular bones
    public uint Flags;
    public short Parent;    // -1 for roots
    public short Geoset;
    public M2Track Translation;
    public M2Track Rotation;
    public M2Track Scaling;
    public Vector3 Pivot;

    public bool IgnoresParentTranslation => (Flags & 0x1u) != 0;
    public bool IgnoresParentScale => (Flags & 0x2u) != 0;
    public bool IgnoresParentRotation => (Flags & 0x4u) != 0;
}

public struct M2Track
{
    public int InterpolationType;   // 0 = none, 1 = linear, 2 = hermite
    public int GlobalSequence;      // -1 = applies to every sequence
    public int RangeCount;
    public int RangeOffset;
    public int TimeCount;
    public int TimeOffset;
    public int KeyCount;
    public int KeyOffset;

    public bool Present => KeyCount > 0 && TimeCount > 0;
}

// A texture animation is three track blocks (uv offset, rotation and scale) and so is 84 bytes
// wide; a texture unit's texanim field indexes the model's texAnimLookup table, which in turn
// selects one of these records. Measured on the 1.12 client's models, whose texture animations
// carry their key data in the gap after the record array, which is why the record width is
// stated here rather than inferred from the gap between sections.
public struct M2TextureAnim
{
    public M2Track Translation;
    public M2Track Rotation;
    public M2Track Scaling;
}
