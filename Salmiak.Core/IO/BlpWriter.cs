using System;
using System.IO;
using System.Text;

namespace Salmiak.Core.IO;

public static class BlpWriter
{
    public static byte[] EncodeDxt1(byte[] rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("rgba buffer too small for the given dimensions.");

        var mips = new System.Collections.Generic.List<byte[]>();
        byte[] cur = rgba; int w = width, h = height;
        while (true)
        {
            mips.Add(CompressDxt1(cur, w, h));
            if (w == 1 && h == 1) break;
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            cur = Downsample(cur, w, h, nw, nh);
            w = nw; h = nh;
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII);
        bw.Write(Encoding.ASCII.GetBytes("BLP2"));
        bw.Write((uint)1);
        bw.Write((byte)2);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((byte)1);
        bw.Write((uint)width);
        bw.Write((uint)height);

        const int paletteSize = 1024;
        int dataStart = 20 + 16 * 4 + 16 * 4 + paletteSize;
        var offsets = new uint[16];
        var sizes = new uint[16];
        int running = dataStart;
        for (int i = 0; i < mips.Count && i < 16; i++)
        {
            offsets[i] = (uint)running;
            sizes[i] = (uint)mips[i].Length;
            running += mips[i].Length;
        }
        for (int i = 0; i < 16; i++) bw.Write(offsets[i]);
        for (int i = 0; i < 16; i++) bw.Write(sizes[i]);
        bw.Write(new byte[paletteSize]);
        foreach (var mip in mips) bw.Write(mip);

        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] Downsample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int sx0 = x * sw / dw, sx1 = Math.Min(sw, (x + 1) * sw / dw);
            int sy0 = y * sh / dh, sy1 = Math.Min(sh, (y + 1) * sh / dh);
            if (sx1 <= sx0) sx1 = sx0 + 1;
            if (sy1 <= sy0) sy1 = sy0 + 1;
            int r = 0, g = 0, b = 0, a = 0, n = 0;
            for (int sy = sy0; sy < sy1; sy++)
            for (int sx = sx0; sx < sx1; sx++)
            {
                int si = (sy * sw + sx) * 4;
                r += src[si]; g += src[si + 1]; b += src[si + 2]; a += src[si + 3]; n++;
            }
            int di = (y * dw + x) * 4;
            dst[di] = (byte)(r / n); dst[di + 1] = (byte)(g / n); dst[di + 2] = (byte)(b / n); dst[di + 3] = (byte)(a / n);
        }
        return dst;
    }

    private static byte[] CompressDxt1(byte[] rgba, int w, int h)
    {
        int bx = Math.Max(1, (w + 3) / 4), by = Math.Max(1, (h + 3) / 4);
        var outBytes = new byte[bx * by * 8];
        int o = 0;
        var block = new byte[16 * 3];
        Span<int> pr = stackalloc int[4]; Span<int> pg = stackalloc int[4]; Span<int> pb = stackalloc int[4];
        for (int byi = 0; byi < by; byi++)
        for (int bxi = 0; bxi < bx; bxi++)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int ix = Math.Min(w - 1, bxi * 4 + px), iy = Math.Min(h - 1, byi * 4 + py);
                int si = (iy * w + ix) * 4;
                byte r = rgba[si], g = rgba[si + 1], b = rgba[si + 2];
                int bi = (py * 4 + px) * 3;
                block[bi] = r; block[bi + 1] = g; block[bi + 2] = b;
                if (r < minR) minR = r; if (g < minG) minG = g; if (b < minB) minB = b;
                if (r > maxR) maxR = r; if (g > maxG) maxG = g; if (b > maxB) maxB = b;
            }

            ushort c0 = Pack565(maxR, maxG, maxB);
            ushort c1 = Pack565(minR, minG, minB);
            if (c0 < c1) (c0, c1) = (c1, c0);

            var (r0, g0, b0) = Unpack565(c0);
            var (r1, g1, b1) = Unpack565(c1);
            pr[0] = r0; pg[0] = g0; pb[0] = b0;
            pr[1] = r1; pg[1] = g1; pb[1] = b1;
            if (c0 > c1)
            {
                pr[2] = (2 * r0 + r1) / 3; pg[2] = (2 * g0 + g1) / 3; pb[2] = (2 * b0 + b1) / 3;
                pr[3] = (r0 + 2 * r1) / 3; pg[3] = (g0 + 2 * g1) / 3; pb[3] = (b0 + 2 * b1) / 3;
            }
            else
            {
                pr[2] = (r0 + r1) / 2; pg[2] = (g0 + g1) / 2; pb[2] = (b0 + b1) / 2;
                pr[3] = 0; pg[3] = 0; pb[3] = 0;
            }

            uint lookup = 0;
            for (int i = 0; i < 16; i++)
            {
                int bi = i * 3;
                int r = block[bi], g = block[bi + 1], b = block[bi + 2];
                int best = 0, bestD = int.MaxValue;
                for (int ci = 0; ci < 4; ci++)
                {
                    int dr = r - pr[ci], dg = g - pg[ci], db = b - pb[ci];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bestD) { bestD = d; best = ci; }
                }
                lookup |= (uint)best << (2 * i);
            }

            outBytes[o++] = (byte)(c0 & 0xFF); outBytes[o++] = (byte)(c0 >> 8);
            outBytes[o++] = (byte)(c1 & 0xFF); outBytes[o++] = (byte)(c1 >> 8);
            outBytes[o++] = (byte)(lookup & 0xFF); outBytes[o++] = (byte)((lookup >> 8) & 0xFF);
            outBytes[o++] = (byte)((lookup >> 16) & 0xFF); outBytes[o++] = (byte)((lookup >> 24) & 0xFF);
        }
        return outBytes;
    }

    private static ushort Pack565(int r, int g, int b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    private static (int r, int g, int b) Unpack565(ushort c)
    {
        int r = (c >> 11) & 0x1F; r = (r << 3) | (r >> 2);
        int g = (c >> 5) & 0x3F; g = (g << 2) | (g >> 4);
        int b = c & 0x1F; b = (b << 3) | (b >> 2);
        return (r, g, b);
    }
}
