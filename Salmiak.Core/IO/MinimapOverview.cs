using System;
using System.Collections.Generic;
using System.IO;

namespace Salmiak.Core.IO;

public sealed class MinimapOverview
{
    public byte[] Rgba = [];
    public int Width, Height;
    public int MinTx, MinTy;
    public int TilesX, TilesY;
    public int TilePx;

    public static MinimapOverview? Build(MpqManager mpq, string map, int tilePx = 32)
    {
        const string trsPath = @"textures\Minimap\md5translate.trs";
        if (!mpq.FileExists(trsPath)) return null;
        string trs;
        try { using var s = mpq.OpenFile(trsPath); using var r = new StreamReader(s); trs = r.ReadToEnd(); }
        catch { return null; }

        string prefix = map + @"\map";
        var tiles = new Dictionary<(int tx, int ty), string>();
        int minTx = int.MaxValue, minTy = int.MaxValue, maxTx = int.MinValue, maxTy = int.MinValue;
        foreach (var raw in trs.Replace("\r\n", "\n").Split('\n'))
        {
            int tab = raw.IndexOf('\t');
            if (tab <= 0) continue;
            string left = raw.Substring(0, tab).Trim();
            string hash = raw[(tab + 1)..].Trim();
            if (hash.Length == 0 || !left.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string coords = left[prefix.Length..];
            int dot = coords.IndexOf('.'); if (dot >= 0) coords = coords[..dot];
            int us = coords.IndexOf('_'); if (us <= 0) continue;
            if (!int.TryParse(coords[..us], out int tx) || !int.TryParse(coords[(us + 1)..], out int ty)) continue;
            tiles[(tx, ty)] = hash;
            if (tx < minTx) minTx = tx; if (tx > maxTx) maxTx = tx;
            if (ty < minTy) minTy = ty; if (ty > maxTy) maxTy = ty;
        }
        if (tiles.Count == 0) return null;

        int tilesX = maxTx - minTx + 1, tilesY = maxTy - minTy + 1;
        int w = tilesX * tilePx, h = tilesY * tilePx;
        var rgba = new byte[w * h * 4];

        foreach (var ((tx, ty), hash) in tiles)
        {
            byte[] src; int sw, sh;
            try { using var s = mpq.OpenFile(@"textures\Minimap\" + hash); (src, sw, sh) = BlpReader.Decode(s); }
            catch { continue; }
            if (sw <= 0 || sh <= 0) continue;
            int ox = (tx - minTx) * tilePx, oy = (ty - minTy) * tilePx;
            for (int py = 0; py < tilePx; py++)
            for (int px = 0; px < tilePx; px++)
            {
                int sx0 = px * sw / tilePx, sx1 = Math.Max(sx0 + 1, (px + 1) * sw / tilePx);
                int sy0 = py * sh / tilePx, sy1 = Math.Max(sy0 + 1, (py + 1) * sh / tilePx);
                long r = 0, g = 0, b = 0; int n = 0;
                for (int sy = sy0; sy < sy1 && sy < sh; sy++)
                for (int sx = sx0; sx < sx1 && sx < sw; sx++)
                { int si = (sy * sw + sx) * 4; r += src[si]; g += src[si + 1]; b += src[si + 2]; n++; }
                if (n == 0) continue;
                int di = ((oy + py) * w + (ox + px)) * 4;
                rgba[di] = (byte)(r / n); rgba[di + 1] = (byte)(g / n); rgba[di + 2] = (byte)(b / n); rgba[di + 3] = 255;
            }
        }

        return new MinimapOverview { Rgba = rgba, Width = w, Height = h, MinTx = minTx, MinTy = minTy, TilesX = tilesX, TilesY = tilesY, TilePx = tilePx };
    }
}
