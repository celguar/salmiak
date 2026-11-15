using System;
using System.Numerics;
using System.Collections.Generic;
using Salmiak.Core.Formats;

namespace Salmiak.Core.Animation;

// Classic (v.256/257) M2 track sampling. A classic bone track owns one flat key list:
// an array of uint32 millisecond timestamps next to the key values, plus optional
// (firstKey,lastKey) ranges that select the subset of keys belonging to each sequence
// (last index inclusive). Interpolation type 2 (hermite) stores three elements per key:
// value, in-tangent, out-tangent. Keys are read straight out of the model's raw bytes, so
// nothing is expanded at load time and animation cost stays proportional to what is drawn.
public sealed class M2TrackSampler
{
    private sealed class Track
    {
        public int Interp;       // 0 = step, 1 = linear, 2 = hermite
        public int GlobalSeq;    // global sequence table index, -1 = sequence-local
        public int ElementSize;  // 12 for a Vec3 key, 16 for a float quaternion key
        public int KeyCount;
        public int[] Times = [];      // milliseconds
        public int KeyOffset;
        public int[] Ranges = [];     // raw (first,last) pairs, one per sequence
        public bool Live;
    }

    private readonly byte[] _raw;
    private readonly List<Track> _tracks = new();
    private int _boneCount;
    private bool _keys;

    public M2TrackSampler(List<M2Bone> bones, byte[] raw)
    {
        _raw = raw ?? [];
        if (bones == null) return;
        foreach (var bone in bones)
        {
            _tracks.Add(ReadTrack(bone.Translation, 12));
            _tracks.Add(ReadTrack(bone.Rotation, 16));
            _tracks.Add(ReadTrack(bone.Scaling, 12));
        }
        _boneCount = _tracks.Count / 3;
        foreach (var t in _tracks) if (t.Live) { _keys = true; break; }
    }

    public int BoneCount() => _boneCount;

    // Whether any bone or texture track has key data: models without keys are static, and giving
    // them a pose or a uv animation would only cost memory the renderer never uses.
    public bool HasKeys() => _keys;

    // Adds texture-animation tracks after the bone tracks and returns the index of the first one so
    // a caller can find an animation's tracks again. The caller adds the three tracks of each
    // animation in the order the family's state uses them, with the element size each one needs.
    public int AddTracks(List<M2Track> tracks, int elementSize)
    {
        int first = _tracks.Count;
        if (tracks == null) return first;
        foreach (var t in tracks)
        {
            _tracks.Add(ReadTrack(t, elementSize));
            if (_tracks[_tracks.Count - 1].Live) _keys = true;
        }
        return first;
    }

    // Texture-track entry point: a missing track yields its un-animated value instead.
    public Vector3 TrackAt(int index, int sequence, int timeMs, Vector3 fallback)
        => index >= 0 && index < _tracks.Count ? SampleVector(_tracks[index], sequence, timeMs, fallback) : fallback;

    // The quaternion form of the same sampling, for the rotation tracks of the families whose
    // keys are quaternions (bones and texture transforms).
    public Quaternion TrackQuatAt(int index, int sequence, int timeMs, Quaternion fallback)
        => index >= 0 && index < _tracks.Count ? SampleQuat(_tracks[index], sequence, timeMs, fallback) : fallback;

    // Bone-track entry points. A missing track yields its bind-pose value instead.
    public Vector3 Translation(int bone, int sequence, int timeMs)
        => _boneCount == 0 ? Vector3.Zero : SampleVector(_tracks[ClampBone(bone) * 3], sequence, timeMs, Vector3.Zero);

    public Quaternion Rotation(int bone, int sequence, int timeMs)
        => _boneCount == 0 ? Quaternion.Identity : SampleQuat(_tracks[ClampBone(bone) * 3 + 1], sequence, timeMs, Quaternion.Identity);

    public Vector3 Scaling(int bone, int sequence, int timeMs)
        => _boneCount == 0 ? Vector3.One : SampleVector(_tracks[ClampBone(bone) * 3 + 2], sequence, timeMs, Vector3.One);

    private static int ClampBone(int bone) => bone < 0 ? 0 : bone;

    private Track ReadTrack(M2Track t, int elementSize)
    {
        var track = new Track
        {
            Interp = t.InterpolationType,
            GlobalSeq = t.GlobalSequence,
            ElementSize = elementSize,
            KeyCount = 0,
            KeyOffset = t.KeyOffset,
            Times = [],
            Ranges = [],
            Live = false,
        };
        if (t.KeyCount <= 0 || t.TimeCount <= 0) return track;

        long timeOfs = (long)t.TimeOffset, keyOfs = (long)t.KeyOffset;
        if (timeOfs < 0 || keyOfs < 0 || timeOfs + (long)t.TimeCount * 4 > _raw.Length) return track;

        int perKey = track.Interp == 2 ? 3 : 1;
        int stride = elementSize * perKey;
        long room = keyOfs < _raw.Length ? _raw.Length - keyOfs : 0;
        int keys = Math.Min(Math.Min(t.KeyCount, t.TimeCount), (int)Math.Min(room / stride, int.MaxValue));
        if (keys <= 0) return track;

        track.KeyCount = keys;
        track.KeyOffset = (int)keyOfs;
        track.Times = new int[keys];
        for (int i = 0; i < keys; i++) track.Times[i] = ReadInt(timeOfs + (long)i * 4);
        track.Live = true;

        if (t.RangeCount > 0 && t.RangeOffset >= 0
            && (long)t.RangeOffset + (long)t.RangeCount * 8 <= _raw.Length)
        {
            track.Ranges = new int[t.RangeCount * 2];
            for (int i = 0; i < track.Ranges.Length; i++)
                track.Ranges[i] = ReadInt((long)t.RangeOffset + (long)i * 4);
        }
        return track;
    }

    // Bracketing key pair for a track at one time; the range records decide which keys
    // belong to the sequence, and a track outside its range does not animate in it.
    private bool FindKeys(Track t, int sequence, int timeMs, out int first, out int last, out float factor)
    {
        first = last = -1;
        factor = 0f;
        if (!t.Live || t.Times.Length == 0) return false;

        int lo = 0, hi = t.KeyCount - 1;
        if (t.Ranges.Length >= 2)
        {
            int pair = sequence >= 0 && (sequence + 1) * 2 <= t.Ranges.Length ? sequence * 2 : 0;
            int a = t.Ranges[pair], b = t.Ranges[pair + 1];
            if (a > b || a > hi) return false;
            lo = a < 0 ? 0 : a;
            hi = b < hi ? b : hi;
            if (lo > hi) return false;
        }

        // A global track loops over its own key window regardless of the sequence.
        if (t.GlobalSeq != -1 && t.Times[hi] > t.Times[lo])
        {
            long period = (long)t.Times[hi] - t.Times[lo];
            long rel = (long)timeMs - t.Times[lo];
            rel %= period;
            if (rel < 0) rel += period;
            timeMs = (int)(t.Times[lo] + rel);
        }

        if (timeMs <= t.Times[lo]) { first = last = lo; return true; }
        if (timeMs >= t.Times[hi]) { first = last = hi; return true; }

        int low = lo, high = hi;
        while (high - low > 1)
        {
            int mid = low + (high - low) / 2;
            if (t.Times[mid] <= timeMs) low = mid;
            else high = mid;
        }

        first = low;
        last = high;
        long interval = (long)t.Times[high] - t.Times[low];
        factor = interval > 0 ? Clamp01((float)(timeMs - t.Times[low]) / (float)interval) : 0f;
        return true;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    private static int ClampPart(int part) => part < 0 ? 0 : part > 2 ? 2 : part;

    // One element of a key: 0 = value, 1 = in-tangent, 2 = out-tangent (hermite keys).
    private static long ElementAt(int keyOffset, int elementSize, int perKey, int key, int part)
        => (long)keyOffset + ((long)key * perKey + ClampPart(part)) * elementSize;

    private Vector3 Element(Track t, int key, int part)
    {
        if (key < 0) return Vector3.Zero;
        int perKey = t.Interp == 2 ? 3 : 1;
        long at = ElementAt(t.KeyOffset, t.ElementSize, perKey, key, part);
        return new(ReadFloat(at), ReadFloat(at + 4), ReadFloat(at + 8));
    }

    private Quaternion QuaternionAt(Track t, int key, int part)
    {
        if (key < 0) return Quaternion.Identity;
        int perKey = t.Interp == 2 ? 3 : 1;
        long at = ElementAt(t.KeyOffset, t.ElementSize, perKey, key, part);
        var q = new Quaternion(ReadFloat(at), ReadFloat(at + 4), ReadFloat(at + 8), ReadFloat(at + 12));
        return q.LengthSquared() > 0.000001f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    private Vector3 SampleVector(Track t, int sequence, int timeMs, Vector3 fallback)
    {
        if (!FindKeys(t, sequence, timeMs, out int a, out int b, out float f)) return fallback;
        if (t.Interp == 2 && a != b)
            return Hermite(Element(t, a, 0), Element(t, a, 2), Element(t, b, 1), Element(t, b, 0), f);
        return Lerp(Element(t, a, 0), Element(t, b, 0), f);
    }

    private Quaternion SampleQuat(Track t, int sequence, int timeMs, Quaternion fallback)
    {
        if (!FindKeys(t, sequence, timeMs, out int a, out int b, out float f)) return fallback;
        var start = QuaternionAt(t, a, 0);
        if (a == b) return start;

        var end = QuaternionAt(t, b, 0);
        if (Quaternion.Dot(start, end) < 0f)
            start = new Quaternion(-start.X, -start.Y, -start.Z, -start.W);
        var q = Quaternion.Slerp(start, end, f);
        return q.LengthSquared() > 0.000001f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float f)
        => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);

    // Hermite (cubic, tangent-driven) blend used by interpolation type 2 keys.
    private static Vector3 Hermite(Vector3 a, Vector3 ao, Vector3 bi, Vector3 b, float f)
    {
        float f2 = f * f, f3 = f2 * f;
        return a * (1f - 3f * f2 + 2f * f3) + ao * (f - 2f * f2 + f3)
             + bi * (f3 - f2) + b * (3f * f2 - 2f * f3);
    }

    private int ReadInt(long at)
    {
        if (at < 0 || at + 4 > _raw.Length) return 0;
        var i = (int)at;
        return _raw[i] | (_raw[i + 1] << 8) | (_raw[i + 2] << 16) | (_raw[i + 3] << 24);
    }

    private float ReadFloat(long at)
    {
        if (at < 0 || at + 4 > _raw.Length) return 0f;
        return BitConverter.Int32BitsToSingle(ReadInt(at));
    }
}
