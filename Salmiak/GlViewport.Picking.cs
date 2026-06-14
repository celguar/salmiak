using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;
using Salmiak.Rendering;

namespace Salmiak;

public sealed partial class GlViewport
{
    private static float PointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared;
        if (len2 < 1e-4f) return (p - a).Length;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + ab * t)).Length;
    }

    private (Vector3 origin, Vector3 dir) RayFromMouse(int mx, int my)
    {
        float aspect = Width > 0 && Height > 0 ? (float)Width / Height : 1f;
        float nx = 2f * mx / Math.Max(1, Width) - 1f;
        float ny = 1f - 2f * my / Math.Max(1, Height);
        float tanHalf = MathF.Tan(MathHelper.DegreesToRadians(60f) * 0.5f);
        var fwd = _camera.Forward;
        var right = _camera.Right;
        var up = Vector3.Normalize(Vector3.Cross(right, fwd));
        var dir = Vector3.Normalize(fwd + nx * tanHalf * aspect * right + ny * tanHalf * up);
        return (_camera.Position, dir);
    }

    private bool PickPlacement(int mx, int my, out Vector3 hit) =>
        (DoodadCollision || WmoOnly) ? PickSurface(mx, my, out hit) : PickTerrain(mx, my, out hit);

    private bool PickSurface(int mx, int my, out Vector3 hit)
    {
        hit = default;
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return false;
        if (mx < 0 || my < 0 || mx >= Width || my >= Height) return false;
        MakeCurrent();
        var depth = new float[1];
        GL.ReadPixels(mx, Height - 1 - my, 1, 1, PixelFormat.DepthComponent, PixelType.Float, depth);
        if (depth[0] >= 1f) return false;

        float aspect = (float)Width / Height;
        var inv = (_camera.View * _camera.Projection(aspect)).Inverted();
        float nx = 2f * mx / Width - 1f, ny = 1f - 2f * my / Height, nz = depth[0] * 2f - 1f;
        var h = Vector4.TransformRow(new Vector4(nx, ny, nz, 1f), inv);
        if (MathF.Abs(h.W) < 1e-9f) return false;
        hit = h.Xyz / h.W;
        return true;
    }

    private bool PickTerrain(int mx, int my, out Vector3 hit)
    {
        var (origin, dir) = RayFromMouse(mx, my);
        const float maxT = 6000f, step = 4f;
        float lo = 0f;
        for (float t = step; t < maxT; t += step)
        {
            var p = origin + dir * t;
            float h = HeightAtGL(p.X, p.Z);
            if (float.IsNaN(h)) { lo = t; continue; }
            if (p.Y <= h)
            {
                float hi = t;
                for (int i = 0; i < 10; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    var pm = origin + dir * mid;
                    float hm = HeightAtGL(pm.X, pm.Z);
                    if (!float.IsNaN(hm) && pm.Y <= hm) hi = mid; else lo = mid;
                }
                hit = origin + dir * hi;
                return true;
            }
            lo = t;
        }
        hit = default;
        return false;
    }

    private float HeightAtGL(float glx, float glz)
    {
        const float ts = AdtFile.TileSize;
        const float step = AdtFile.ChunkSize / 8f;
        int tileX = (int)MathF.Floor(32f + glx / ts);
        int tileY = (int)MathF.Floor(32f + glz / ts);
        if (!_tileCache.TryGetValue((tileX, tileY), out var adt) || adt == null) return float.NaN;

        float originGLx = -(32 - tileX) * ts;
        float originGLz = -(32 - tileY) * ts;
        float c = (glx - originGLx) / step;
        float r = (glz - originGLz) / step;
        if (c < 0 || c >= 128 || r < 0 || r >= 128) return float.NaN;

        int chunkCol = (int)(c / 8), chunkRow = (int)(r / 8);
        var chunk = adt.Chunks[chunkRow, chunkCol];
        if (chunk == null) return float.NaN;

        int cc = Math.Min(7, (int)c - chunkCol * 8);
        int cr = Math.Min(7, (int)r - chunkRow * 8);
        float fx = c - chunkCol * 8 - cc;
        float fz = r - chunkRow * 8 - cr;
        float baseZ = chunk.Position.Z;
        float h00 = baseZ + chunk.Heights[cr * 17 + cc];
        float h10 = baseZ + chunk.Heights[cr * 17 + cc + 1];
        float h01 = baseZ + chunk.Heights[(cr + 1) * 17 + cc];
        float h11 = baseZ + chunk.Heights[(cr + 1) * 17 + cc + 1];
        return Lerp(Lerp(h00, h10, fx), Lerp(h01, h11, fx), fz);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private bool TryGetChunkAt(Vector3 hit, out AdtFile adt, out MapChunk chunk,
                               out int tileX, out int tileY, out int chunkCol, out int chunkRow)
    {
        adt = null!; chunk = null!; chunkCol = chunkRow = 0;
        const float ts = AdtFile.TileSize, step = AdtFile.ChunkSize / 8f;
        tileX = (int)MathF.Floor(32f + hit.X / ts);
        tileY = (int)MathF.Floor(32f + hit.Z / ts);
        if (!_tileCache.TryGetValue((tileX, tileY), out var a) || a == null) return false;
        float c = (hit.X + (32 - tileX) * ts) / step;
        float r = (hit.Z + (32 - tileY) * ts) / step;
        if (c < 0 || c >= 128 || r < 0 || r >= 128) return false;
        chunkCol = (int)(c / 8); chunkRow = (int)(r / 8);
        var ch = a.Chunks[chunkRow, chunkCol];
        if (ch == null) return false;
        adt = a; chunk = ch; return true;
    }

    private bool WorldToScreen(Vector3 w, out Vector2 sp)
    {
        sp = default;
        float aspect = Width > 0 && Height > 0 ? (float)Width / Height : 1f;
        var clip = Vector4.TransformRow(new Vector4(w, 1f), _camera.View * _camera.Projection(aspect));
        if (clip.W <= 1e-5f) return false;
        float nx = clip.X / clip.W, ny = clip.Y / clip.W;
        sp = new Vector2((nx * 0.5f + 0.5f) * Width, (1f - (ny * 0.5f + 0.5f)) * Height);
        return true;
    }

    private bool TryGetChunkGridAt(int mx, int my, out (int Col, int Row) grid)
    {
        grid = default;
        if (!PickTerrain(mx, my, out var hit)) return false;
        if (!TryGetChunkAt(hit, out _, out _, out int tileX, out int tileY, out int cCol, out int cRow)) return false;
        grid = (tileX * AdtFile.ChunksPerSide + cCol, tileY * AdtFile.ChunksPerSide + cRow);
        return true;
    }
}
