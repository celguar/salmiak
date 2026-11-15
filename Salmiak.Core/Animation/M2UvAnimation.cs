using System.Numerics;
using System.Collections.Generic;
using Salmiak.Core.Formats;
using Salmiak.Core.Animation;

namespace Salmiak.Core.Animation;

// Classic (v.256) texture animation: a texture unit's uv coordinates can be offset, rotated and
// scaled over time, which is what makes the 1.12 client's waterfall doodads read as flowing
// water. The three tracks of such an animation are sampled exactly like bone tracks, so all this
// adds is the composition into the matrix the shader multiplies the uv coordinates by.
//
// Measured over the whole 1.12 client (model.MPQ and patch.MPQ): 88 and 98 doodads carry texture
// animations at all, and of those only one has uv rotation keys and one has uv scale keys, so the
// uv offset is what actually animates water, foliage and torch flames. The rotation keys are quaternions
// and the rotation happens about the middle of the texture, which is what the 1.12.1 client's own
// matrix build says and what the scrying bowl's water does: it turns a quarter circle about the
// centre of its texture, which it would only do by accident when the rotation is taken about the uv
// origin.
public sealed class M2UvAnimation
{
    private readonly List<M2TextureAnim> _anims = new();
    private readonly List<M2Sequence> _seqs = new();
    private readonly M2TrackSampler _sampler;
    private int _first;
    private readonly bool _animated;

    public M2UvAnimation(List<M2TextureAnim> anims, List<M2Sequence> sequences, byte[] raw)
    {
        _anims = anims ?? new();
        _seqs = sequences ?? new();
        _sampler = new M2TrackSampler(new(), raw);
        if (_anims.Count > 0)
        {
            // Added family by family rather than animation by animation, so one element size can
            // serve every track of a family: the translation and scale keys are vectors, the
            // rotation keys quaternions. The index of a track is then the family's start plus the
            // animation's number.
            var translation = new List<M2Track>(_anims.Count);
            var rotation = new List<M2Track>(_anims.Count);
            var scaling = new List<M2Track>(_anims.Count);
            foreach (var a in _anims)
            {
                translation.Add(a.Translation);
                rotation.Add(a.Rotation);
                scaling.Add(a.Scaling);
            }
            _first = _sampler.AddTracks(translation, 3 * sizeof(float));
            _sampler.AddTracks(rotation, 4 * sizeof(float));
            _sampler.AddTracks(scaling, 3 * sizeof(float));
        }
        _animated = _sampler.HasKeys();
    }

    // False when nothing carries keys, in which case the renderer leaves the uv coordinates alone.
    public bool Animates() => _animated;

    // The uv transform one sequence plays at one elapsed time. The elapsed time is wrapped into
    // the sequence's own window because classic key times are absolute inside the file, which is
    // the same mapping M2Pose applies to the bones so a waterfall's uv scroll matches its mesh.
    public Matrix4x4 Evaluate(int anim, int sequence, int elapsedMs)
    {
        if (anim < 0 || anim >= _anims.Count) return Matrix4x4.Identity;
        var time = SequenceTimeMs(sequence, elapsedMs);
        var n = _anims.Count;

        var t = _anims[anim].Translation.Present ? _sampler.TrackAt(_first + anim, sequence, time, Vector3.Zero) : Vector3.Zero;
        var q = _anims[anim].Rotation.Present ? _sampler.TrackQuatAt(_first + n + anim, sequence, time, Quaternion.Identity) : Quaternion.Identity;
        var s = _anims[anim].Scaling.Present ? _sampler.TrackAt(_first + 2 * n + anim, sequence, time, Vector3.One) : Vector3.One;

        // The classic client builds this matrix from identity and the result for the row vector
        // (u, v, 0, 1) is ((uv + t - p) * s) * Q + p, with p the centre (0.5, 0.5, 0) and Q the
        // sampled quaternion as a matrix: the offset is applied first, the uv coordinates are
        // scaled and rotated about the middle of the texture, and put back. The same order in
        // System.Numerics' row-vector multiplication is translate, scale, rotate, translate.
        return Matrix4x4.CreateTranslation(t.X - 0.5f, t.Y - 0.5f, t.Z)
             * Matrix4x4.CreateScale(s.X, s.Y, s.Z)
             * QuaternionMatrix(q)
             * Matrix4x4.CreateTranslation(0.5f, 0.5f, 0f);
    }

    // The rotation matrix of a quaternion, as the classic client writes it: the rows are the
    // quaternion's own multiplication, so multiplying a row vector by it rotates the vector.
    private static Matrix4x4 QuaternionMatrix(Quaternion q)
    {
        var x = q.X; var y = q.Y; var z = q.Z; var w = q.W;
        return new(1f - 2f * (y * y + z * z), 2f * (x * y + z * w), 2f * (x * z - y * w), 0f,
                   2f * (x * y - z * w), 1f - 2f * (x * x + z * z), 2f * (y * z + x * w), 0f,
                   2f * (x * z + y * w), 2f * (y * z - x * w), 1f - 2f * (x * x + y * y), 0f,
                   0f, 0f, 0f, 1f);
    }

    private int SequenceTimeMs(int sequence, int elapsedMs)
    {
        if (sequence < 0 || sequence >= _seqs.Count) return elapsedMs;
        var s = _seqs[sequence];
        long duration = (long)s.TimeEnd - (long)s.TimeStart;
        var time = elapsedMs;
        if (duration > 0 && duration < int.MaxValue)
        {
            time %= (int)duration;
            if (time < 0) time += (int)duration;
        }
        return (int)s.TimeStart + time;
    }

}
