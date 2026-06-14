using System;
using System.Collections.Generic;
using System.Numerics;

namespace Salmiak.Core.Formats;

public static class BlankMap
{
    public static AdtFile BuildTile(int tileX, int tileY, float height, string baseTexture)
    {
        var adt = new AdtFile();
        adt.Textures.Add(baseTexture);

        for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
        for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
        {
            float posX = (32 - tileY) * AdtFile.TileSize - cy * AdtFile.ChunkSize;
            float posY = (32 - tileX) * AdtFile.TileSize - cx * AdtFile.ChunkSize;

            var chunk = new MapChunk
            {
                IndexX   = cx,
                IndexY   = cy,
                Position = new Vector3(posX, posY, height),
                AreaId   = 0,
                Holes    = 0,
                NLayers  = 1,
                Flags    = 0,
            };
            chunk.Layers[0] = new TextureLayer { TextureIndex = 0, Flags = 0, OffsetInMCAL = 0, EffectId = 0 };

            adt.Chunks[cy, cx] = chunk;
        }
        return adt;
    }

    public static (WdtFile Wdt, Dictionary<(int X, int Y), AdtFile> Tiles) Build(
        int wTiles, int hTiles, float height, string baseTexture)
    {
        wTiles = Math.Clamp(wTiles, 1, WdtFile.GridSize);
        hTiles = Math.Clamp(hTiles, 1, WdtFile.GridSize);
        int ox = Math.Clamp(32 - wTiles / 2, 0, WdtFile.GridSize - wTiles);
        int oy = Math.Clamp(32 - hTiles / 2, 0, WdtFile.GridSize - hTiles);

        var wdt = new WdtFile();
        var tiles = new Dictionary<(int X, int Y), AdtFile>();
        for (int ty = oy; ty < oy + hTiles; ty++)
        for (int tx = ox; tx < ox + wTiles; tx++)
        {
            wdt.TileExists[ty, tx] = true;
            tiles[(tx, ty)] = BuildTile(tx, ty, height, baseTexture);
        }
        return (wdt, tiles);
    }
}
