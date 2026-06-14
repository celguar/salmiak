using OpenTK.Mathematics;

namespace Salmiak.Rendering;

public readonly struct Frustum
{
    private readonly Vector4 _l, _r, _b, _t, _n, _f;

    public Frustum(Matrix4 m)
    {
        var c0 = new Vector4(m.Row0.X, m.Row1.X, m.Row2.X, m.Row3.X);
        var c1 = new Vector4(m.Row0.Y, m.Row1.Y, m.Row2.Y, m.Row3.Y);
        var c2 = new Vector4(m.Row0.Z, m.Row1.Z, m.Row2.Z, m.Row3.Z);
        var c3 = new Vector4(m.Row0.W, m.Row1.W, m.Row2.W, m.Row3.W);

        _l = Normalize(c3 + c0);
        _r = Normalize(c3 - c0);
        _b = Normalize(c3 + c1);
        _t = Normalize(c3 - c1);
        _n = Normalize(c3 + c2);
        _f = Normalize(c3 - c2);
    }

    private static Vector4 Normalize(Vector4 p)
    {
        float len = new Vector3(p.X, p.Y, p.Z).Length;
        return len > 1e-6f ? p / len : p;
    }

    public bool IntersectsBox(Vector3 min, Vector3 max)
    {
        return Test(_l, min, max) && Test(_r, min, max) && Test(_b, min, max)
            && Test(_t, min, max) && Test(_n, min, max) && Test(_f, min, max);
    }

    public bool IntersectsSphere(Vector3 center, float radius)
    {
        return Dist(_l, center) >= -radius && Dist(_r, center) >= -radius
            && Dist(_b, center) >= -radius && Dist(_t, center) >= -radius
            && Dist(_n, center) >= -radius && Dist(_f, center) >= -radius;
    }

    private static bool Test(Vector4 p, Vector3 min, Vector3 max)
    {
        float x = p.X >= 0 ? max.X : min.X;
        float y = p.Y >= 0 ? max.Y : min.Y;
        float z = p.Z >= 0 ? max.Z : min.Z;
        return p.X * x + p.Y * y + p.Z * z + p.W >= 0;
    }

    private static float Dist(Vector4 p, Vector3 c) => p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W;
}
