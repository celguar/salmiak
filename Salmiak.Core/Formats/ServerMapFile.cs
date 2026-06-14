using System;
using System.IO;

namespace Salmiak.Core.Formats;

public static class ServerMapFile
{
    private const float MinHeight = -500f;

    public static string FileName(int mapId, int tileX, int tileY) => $"{mapId:D3}{tileY:D2}{tileX:D2}.map";

    public static byte[] Build(AdtFile adt, Func<int, ushort> areaFlagOf)
    {
        const int cells = 16, grid = 128;

        var area = new ushort[cells, cells];
        for (int i = 0; i < cells; i++)
        for (int j = 0; j < cells; j++)
        {
            var c = adt.Chunks[i, j];
            area[i, j] = c != null && c.AreaId > 0 ? areaFlagOf(c.AreaId) : (ushort)0xFFFF;
        }
        bool fullArea = false;
        for (int i = 0; i < cells && !fullArea; i++)
        for (int j = 0; j < cells; j++)
            if (area[i, j] != area[0, 0]) { fullArea = true; break; }

        var v9 = new float[grid + 1, grid + 1];
        var v8 = new float[grid, grid];
        for (int i = 0; i < cells; i++)
        for (int j = 0; j < cells; j++)
        {
            var c = adt.Chunks[i, j];
            float basez = c?.Position.Z ?? 0f;
            for (int y = 0; y <= 8; y++)
            for (int x = 0; x <= 8; x++)
                v9[i * 8 + y, j * 8 + x] = basez + (c?.Heights[y * 17 + x] ?? 0f);
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                v8[i * 8 + y, j * 8 + x] = basez + (c?.Heights[y * 17 + 9 + x] ?? 0f);
        }
        float hMin = 20000f, hMax = -20000f;
        foreach (var h in v9) { if (h < hMin) hMin = h; if (h > hMax) hMax = h; }
        foreach (var h in v8) { if (h < hMin) hMin = h; if (h > hMax) hMax = h; }
        if (hMin < MinHeight)
        {
            for (int y = 0; y <= grid; y++) for (int x = 0; x <= grid; x++) if (v9[y, x] < MinHeight) v9[y, x] = MinHeight;
            for (int y = 0; y < grid; y++) for (int x = 0; x < grid; x++) if (v8[y, x] < MinHeight) v8[y, x] = MinHeight;
            hMin = MathF.Max(hMin, MinHeight);
            hMax = MathF.Max(hMax, MinHeight);
        }
        bool flatHeight = hMax == hMin;

        var liqShow = new bool[grid, grid];
        var liqHeight = new float[grid + 1, grid + 1];
        var liqEntry = new ushort[cells, cells];
        var liqFlags = new byte[cells, cells];
        for (int y = 0; y <= grid; y++) for (int x = 0; x <= grid; x++) liqHeight[y, x] = MinHeight;
        for (int i = 0; i < cells; i++)
        for (int j = 0; j < cells; j++)
        {
            var lq = adt.Chunks[i, j]?.Liquid;
            if (lq == null) continue;
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (lq.Render[y * 8 + x])
                    liqShow[i * 8 + y, j * 8 + x] = true;
            switch (lq.Type)
            {
                case 2: liqEntry[i, j] = 2; liqFlags[i, j] |= 0x02; break;
                case 3: case 4: liqEntry[i, j] = 3; liqFlags[i, j] |= 0x01; break;
                default: liqEntry[i, j] = 1; liqFlags[i, j] |= 0x08; break;
            }
            for (int y = 0; y <= 8; y++)
            for (int x = 0; x <= 8; x++)
                liqHeight[i * 8 + y, j * 8 + x] = lq.Heights[y * 9 + x];
        }
        bool fullLiqType = false;
        for (int i = 0; i < cells && !fullLiqType; i++)
        for (int j = 0; j < cells; j++)
            if (liqEntry[i, j] != liqEntry[0, 0] || liqFlags[i, j] != liqFlags[0, 0]) { fullLiqType = true; break; }
        bool anyLiquid = fullLiqType || liqFlags[0, 0] != 0;

        int minX = 255, minY = 255, maxX = 0, maxY = 0;
        float lqMin = 20000f, lqMax = -20000f;
        if (anyLiquid)
            for (int y = 0; y < grid; y++)
            for (int x = 0; x < grid; x++)
            {
                if (liqShow[y, x])
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    float h = liqHeight[y, x];
                    if (h < lqMin) lqMin = h;
                    if (h > lqMax) lqMax = h;
                }
                else
                {
                    liqHeight[y, x] = MinHeight;
                    if (lqMin > MinHeight) lqMin = MinHeight;
                }
            }
        int lqW = anyLiquid ? maxX - minX + 2 : 0;
        int lqH = anyLiquid ? maxY - minY + 2 : 0;
        bool flatLiquid = anyLiquid && lqMax == lqMin;

        const int fileHdr = 40, areaHdr = 8, heightHdr = 16, liquidHdr = 16;
        int areaSize = areaHdr + (fullArea ? cells * cells * 2 : 0);
        int heightSize = heightHdr + (flatHeight ? 0 : 4 * ((grid + 1) * (grid + 1) + grid * grid));
        int liquidSize = !anyLiquid ? 0
            : liquidHdr + (fullLiqType ? cells * cells * 3 : 0) + (flatLiquid ? 0 : 4 * lqW * lqH);
        int areaOfs = fileHdr;
        int heightOfs = areaOfs + areaSize;
        int liquidOfs = anyLiquid ? heightOfs + heightSize : 0;
        int holesOfs = (anyLiquid ? liquidOfs + liquidSize : heightOfs + heightSize);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Magic(string s) { w.Write((byte)s[0]); w.Write((byte)s[1]); w.Write((byte)s[2]); w.Write((byte)s[3]); }

        Magic("MAPS"); Magic("z1.4");
        w.Write((uint)areaOfs); w.Write((uint)areaSize);
        w.Write((uint)heightOfs); w.Write((uint)heightSize);
        w.Write((uint)liquidOfs); w.Write((uint)liquidSize);
        w.Write((uint)holesOfs); w.Write((uint)(cells * cells * 2));

        Magic("AREA");
        w.Write((ushort)(fullArea ? 0 : 1));
        w.Write(fullArea ? (ushort)0 : area[0, 0]);
        if (fullArea)
            for (int i = 0; i < cells; i++)
            for (int j = 0; j < cells; j++)
                w.Write(area[i, j]);

        Magic("MHGT");
        w.Write((uint)(flatHeight ? 1 : 0));
        w.Write(hMin); w.Write(hMax);
        if (!flatHeight)
        {
            for (int y = 0; y <= grid; y++) for (int x = 0; x <= grid; x++) w.Write(v9[y, x]);
            for (int y = 0; y < grid; y++) for (int x = 0; x < grid; x++) w.Write(v8[y, x]);
        }

        if (anyLiquid)
        {
            Magic("MLIQ");
            byte lflags = 0;
            if (flatLiquid) lflags |= 0x02;
            if (!fullLiqType) lflags |= 0x01;
            w.Write(lflags);
            w.Write(!fullLiqType ? liqFlags[0, 0] : (byte)0);
            w.Write(!fullLiqType ? liqEntry[0, 0] : (ushort)0);
            w.Write((byte)minX); w.Write((byte)minY);
            w.Write((byte)lqW); w.Write((byte)lqH);
            w.Write(lqMin);
            if (fullLiqType)
            {
                for (int i = 0; i < cells; i++) for (int j = 0; j < cells; j++) w.Write(liqEntry[i, j]);
                for (int i = 0; i < cells; i++) for (int j = 0; j < cells; j++) w.Write(liqFlags[i, j]);
            }
            if (!flatLiquid)
                for (int y = 0; y < lqH; y++)
                for (int x = 0; x < lqW; x++)
                    w.Write(liqHeight[y + minY, x + minX]);
        }

        for (int i = 0; i < cells; i++)
        for (int j = 0; j < cells; j++)
            w.Write(adt.Chunks[i, j]?.Holes ?? (ushort)0);

        return ms.ToArray();
    }
}
