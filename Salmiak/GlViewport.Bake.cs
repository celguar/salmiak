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
    //Bake chunks overlay: pick which chunks get ray-baked shadows (same gestures as Zone editing).
    public bool BakeChunksMode;
    private readonly List<(int Col, int Row)> _bakeChunks = new();
    private (int Col, int Row)? _bakeRectAnchor;
    private (int Col, int Row) _bakeRectCur;
    private MapChunk? _bakeHoverChunk;
    private float[] _bakeFill = new float[4 * 3];
    private bool _prevBake;
    private bool _prevBakeClear;
    private int _bakeTileX = -1;
    private int _bakeTileY = -1;

    public bool ToggleBakeChunks()
    {
        if (DoodadMode || WmoMode) { Notify?.Invoke("Exit doodad/WMO mode to use bake chunks mode."); return false; }
        BakeChunksMode = !BakeChunksMode;
        _bakeChunks.Clear();
        _bakeRectAnchor = null;
        _bakeHoverChunk = null;
        //The shadow brush and the chunk picker are one mode: entering the picker disarms the brush.
        ShadowMode = false;
        if (BakeChunksMode && !EditMode) SetPrimaryMode(EditorMode.Edit);
        if (BakeChunksMode) UpdateBakeHover(_mouseX, _mouseY);
        Notify?.Invoke(BakeChunksMode
            ? "Bake chunks mode - click chunks to add, Shift+drag a block, Shift+C every chunk of this tile, C to reset, Ctrl+I again or Esc leaves the Shadows tool."
            : "Bake chunks mode off.");
        ModeChanged?.Invoke(ModeLabel());
        return BakeChunksMode;
    }

    public int BakedChunkCount => _bakeChunks.Count;

    //Bake progress: fired once per chunk handled, so the shadow sidebar can track it.
    public event Action<int, int>? ShadowBakeProgress;
    public event Action? ShadowBakeProgressReset;
    //Fired while the bake's models are still being gathered, which is the one part of a bake that
    //costs a lot but owns no chunk yet: the sidebar labels it "Preparing models" so an emptied bar
    //is never mistaken for a finished one.
    public event Action? ShadowBakePreparing;

    //Picks every chunk of the tile under the cursor, or of every loaded tile when allTiles.
    public bool SelectAllBakeChunks(bool allTiles)
    {
        if (!BakeChunksMode) return false;
        if (!allTiles && _bakeTileX < 0) { Notify?.Invoke("Bake selection: point at a tile first, then press Shift+C."); return false; }
        _bakeChunks.Clear();
        _bakeRectAnchor = null;
        int wantX = _bakeTileX, wantY = _bakeTileY;
        foreach (var kv in _tileCache)
        {
            if (kv.Value == null) continue;
            var (tx, ty) = kv.Key;
            if (!allTiles && (tx != wantX || ty != wantY)) continue;
            for (int row = 0; row < AdtFile.ChunksPerSide; row++)
                for (int col = 0; col < AdtFile.ChunksPerSide; col++)
                    _bakeChunks.Add((tx * AdtFile.ChunksPerSide + col, ty * AdtFile.ChunksPerSide + row));
            if (!allTiles) break;
        }
        Notify?.Invoke(allTiles ? $"Chunks of every loaded tile picked: { _bakeChunks.Count} (the sidebar Bake button bakes them, C resets the list)."
                       : $"Every chunk of this tile picked: { _bakeChunks.Count} (the sidebar Bake button bakes them, C resets the list).");
        return true;
    }

    public void ClearBakeChunks()
    {
        _bakeChunks.Clear();
        _bakeRectAnchor = null;
        Notify?.Invoke(BakeChunksMode
            ? "Bake selection cleared - click chunks again, Shift+C for the whole tile, Ctrl+I leaves the Shadows tool."
            : "Bake selection cleared.");
    }

    public void CancelBakeRect() { _bakeRectAnchor = null; }

    public void ExitBakeChunks()
    {
        _bakeRectAnchor = null;
        ToggleBakeChunks();
    }

    private static int BakeChunkTile(int coord) => (int)Math.Floor(coord / (float)AdtFile.ChunksPerSide);
    private static int BakeChunkLocal(int coord) => coord - BakeChunkTile(coord) * AdtFile.ChunksPerSide;

    public bool AddBakeChunkAt(int mx, int my)
    {
        if (!PickTerrain(mx, my, out var hit)) return false;
        if (!TryGetChunkAt(hit, out _, out _, out int tileX, out int tileY, out int localCol, out int localRow)) return false;
        return AppendBakeChunk(tileX * AdtFile.ChunksPerSide + localCol,
                               tileY * AdtFile.ChunksPerSide + localRow);
    }

    private bool AppendBakeChunk(int col, int row)
    {
        foreach (var k in _bakeChunks)
            if (k.Col == col && k.Row == row) return false;
        _bakeChunks.Add((col, row));
        return true;
    }

    public void UpdateBakeRect(int mx, int my)
    {
        if (TryGetChunkGridAt(mx, my, out var cur)) _bakeRectCur = cur;
    }

    public void CommitBakeRect()
    {
        if (_bakeRectAnchor is { } a)
        {
            if (AppendBakeRect(a, _bakeRectCur)) Notify?.Invoke("Chunks added to the bake list.");
        }
        _bakeRectAnchor = null;
    }

    public bool AppendBakeRect((int Col, int Row) a, (int Col, int Row) b)
    {
        int c0 = Math.Min(a.Col, b.Col), c1 = Math.Max(a.Col, b.Col);
        int r0 = Math.Min(a.Row, b.Row), r1 = Math.Max(a.Row, b.Row);
        bool added = false;
        for (int row = r0; row <= r1; row++)
            for (int col = c0; col <= c1; col++)
                added |= AppendBakeChunk(col, row);
        return added;
    }

    public void UpdateBakeHover(int mx, int my)
    {
        _bakeHoverChunk = null;
        _bakeTileX = -1;
        _bakeTileY = -1;
        if (PickTerrain(mx, my, out var hit) &&
            TryGetChunkAt(hit, out _, out var ch, out _bakeTileX, out _bakeTileY, out _, out _)) _bakeHoverChunk = ch;
    }

    public void BeginBakeRect(int col, int row)
    {
        _bakeRectAnchor = (col, row);
        _bakeRectCur = (col, row);
    }

    public int SelectedBakeChunks() => _bakeChunks.Count;
    public int SelectedBakeChunkCol(int i) => _bakeChunks[i].Col;
    public int SelectedBakeChunkRow(int i) => _bakeChunks[i].Row;

    public void RenderBakeOverlay(float aspect)
    {
        if (!BakeChunksMode || _brush == null) return;
        const float cs = AdtFile.ChunkSize, half = 32f * AdtFile.TileSize;
        foreach (var k in _bakeChunks)
        {
            int tileX = BakeChunkTile(k.Col), tileY = BakeChunkTile(k.Row);
            if (!_tileCache.TryGetValue((tileX, tileY), out var adt) || adt == null) continue;
            var ch = adt.Chunks[BakeChunkLocal(k.Row), BakeChunkLocal(k.Col)];
            if (ch == null) continue;
            float x0 = k.Col * cs - half, z0 = k.Row * cs - half;
            _bakeFill[0] = x0; _bakeFill[1] = 0f; _bakeFill[2] = z0;
            _bakeFill[3] = x0 + cs; _bakeFill[4] = 0f; _bakeFill[5] = z0;
            _bakeFill[6] = x0 + cs; _bakeFill[7] = 0f; _bakeFill[8] = z0 + cs;
            _bakeFill[9] = x0; _bakeFill[10] = 0f; _bakeFill[11] = z0 + cs;
            for (int i = 0; i < 4; i++)
            {
                float hY = HeightAtGL(_bakeFill[i * 3], _bakeFill[i * 3 + 2]);
                _bakeFill[i * 3 + 1] = (float.IsNaN(hY) ? _camera.Position.Y - 50f : hY) + 1f;
            }
            _brush.RenderColored(_camera, aspect, _bakeFill, 4, PrimitiveType.TriangleFan, 0.25f, 0.75f, 0.45f, 0.22f);
            _brush.RenderColored(_camera, aspect, _outline, BuildChunkOutline(ch), PrimitiveType.LineLoop, 0.25f, 0.9f, 0.5f, 0.9f);
        }
        if (_bakeRectAnchor is { } a)
        {
            int c0 = Math.Min(a.Col, _bakeRectCur.Col), c1 = Math.Max(a.Col, _bakeRectCur.Col) + 1;
            int r0 = Math.Min(a.Row, _bakeRectCur.Row), r1 = Math.Max(a.Row, _bakeRectCur.Row) + 1;
            _bakeFill[0] = c0 * cs - half; _bakeFill[1] = 0f; _bakeFill[2] = r0 * cs - half;
            _bakeFill[3] = c1 * cs - half; _bakeFill[4] = 0f; _bakeFill[5] = r0 * cs - half;
            _bakeFill[6] = c1 * cs - half; _bakeFill[7] = 0f; _bakeFill[8] = r1 * cs - half;
            _bakeFill[9] = c0 * cs - half; _bakeFill[10] = 0f; _bakeFill[11] = r1 * cs - half;
            for (int i = 0; i < 4; i++)
            {
                float hY = HeightAtGL(_bakeFill[i * 3], _bakeFill[i * 3 + 2]);
                _bakeFill[i * 3 + 1] = (float.IsNaN(hY) ? _camera.Position.Y - 50f : hY) + 1f;
            }
            _brush.RenderColored(_camera, aspect, _bakeFill, 4, PrimitiveType.LineLoop, 1f, 1f, 1f);
        }
        else if (_bakeHoverChunk != null)
            _brush.RenderColored(_camera, aspect, _outline, BuildChunkOutline(_bakeHoverChunk),
                                 PrimitiveType.LineLoop, 0.35f, 1f, 0.6f, 1f);
    }
}
