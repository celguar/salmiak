using System;
using System.Numerics;

namespace Salmiak.Core.Formats;

public static class TerrainShadow
{
    public const int Res = 64;
    private const float ChunkSize = AdtFile.ChunkSize;
    private const float Step = ChunkSize / 8f;

    public static Vector3 SunGl = new(0.6f, 1.0f, 0.4f);

    private static AdtFile? _fieldAdt;
    private static TileField? _field;

    public static byte[]? Compute(AdtFile adt, MapChunk chunk) => Compute(adt, chunk, SunGl, ChunkSize * 8f);

    public static byte[]? Compute(AdtFile adt, MapChunk chunk, Vector3 sunGl, float maxDist)
    {
        if (!ReferenceEquals(_fieldAdt, adt)) { _field = TileField.Build(adt); _fieldAdt = adt; }
        var field = _field!;

        var sun = Vector3.Normalize(sunGl);
        float horiz = MathF.Sqrt(sun.X * sun.X + sun.Z * sun.Z);
        if (horiz < 1e-4f) return null;
        float hx = sun.X / horiz, hz = sun.Z / horiz;
        float slope = sun.Y / horiz;

        float baseX = -chunk.Position.Y, baseZ = -chunk.Position.X;
        const float ds = Step * 0.5f;
        const float eps = 0.1f;

        var mask = new byte[Res * Res];
        bool any = false;
        for (int mrow = 0; mrow < Res; mrow++)
        for (int mcol = 0; mcol < Res; mcol++)
        {
            float fx = mcol / (float)(Res - 1), fz = mrow / (float)(Res - 1);
            float x0 = baseX + fx * ChunkSize, z0 = baseZ + fz * ChunkSize;
            float h0 = field.Height(x0, z0);
            for (float d = ds; d <= maxDist; d += ds)
            {
                float rayH = h0 + slope * d;
                if (rayH > field.MaxHeight) break;
                if (field.Height(x0 + hx * d, z0 + hz * d) > rayH + eps)
                {
                    mask[mrow * Res + mcol] = 1; any = true; break;
                }
            }
        }
        return any ? Pack(mask) : null;
    }

    private static byte[] Pack(byte[] mask)
    {
        var packed = new byte[Res * Res / 8];
        for (int row = 0; row < Res; row++)
        for (int col = 0; col < Res; col++)
            if (mask[row * Res + col] != 0)
                packed[row * 8 + (col >> 3)] |= (byte)(1 << (col & 7));
        return packed;
    }

    private sealed class TileField
    {
        private const int N = 16 * 8 + 1;
        private readonly float[] _h = new float[N * N];
        private float _x0, _z0;
        public float MaxHeight { get; private set; }

        public static TileField Build(AdtFile adt)
        {
            var f = new TileField();
            MapChunk? origin = null; int ocy = 0, ocx = 0;
            for (int cy = 0; cy < 16 && origin == null; cy++)
            for (int cx = 0; cx < 16 && origin == null; cx++)
                if (adt.Chunks[cy, cx] != null) { origin = adt.Chunks[cy, cx]; ocy = cy; ocx = cx; }
            if (origin == null) return f;
            f._x0 = -origin.Position.Y - ocx * ChunkSize;
            f._z0 = -origin.Position.X - ocy * ChunkSize;

            float max = float.NegativeInfinity;
            for (int cy = 0; cy < 16; cy++)
            for (int cx = 0; cx < 16; cx++)
            {
                var ch = adt.Chunks[cy, cx];
                if (ch == null) continue;
                for (int r = 0; r <= 8; r++)
                for (int c = 0; c <= 8; c++)
                {
                    float h = ch.Position.Z + ch.Heights[r * 17 + c];
                    int gr = cy * 8 + r, gc = cx * 8 + c;
                    f._h[gr * N + gc] = h;
                    if (h > max) max = h;
                }
            }
            f.MaxHeight = max == float.NegativeInfinity ? 0f : max;
            return f;
        }

        public float Height(float glx, float glz)
        {
            float gcf = Math.Clamp((glx - _x0) / Step, 0, N - 1.001f);
            float grf = Math.Clamp((glz - _z0) / Step, 0, N - 1.001f);
            int gc = (int)gcf, gr = (int)grf;
            float tx = gcf - gc, tz = grf - gr;
            float h00 = _h[gr * N + gc], h10 = _h[gr * N + gc + 1];
            float h01 = _h[(gr + 1) * N + gc], h11 = _h[(gr + 1) * N + gc + 1];
            return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
        }
    }
}
