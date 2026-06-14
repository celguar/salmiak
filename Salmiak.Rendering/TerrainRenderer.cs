using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Salmiak.Core.Formats;

namespace Salmiak.Rendering;

public sealed class TerrainRenderer : IDisposable
{
    private const float ChunkStep = AdtFile.ChunkSize / 8f;
    private const int   Stride    = 8 * sizeof(float);

    private int _shader;
    private bool _ready;

    public int DebugMode { get; set; }

    public int IsolateLayerIndex { get; set; } = -1;

    public bool FlatLight { get; set; }

    private int _uMVP, _uNLayers, _uDebug, _uHasShadow, _uIsolate;
    private int _uTex0, _uTex1, _uTex2, _uTex3;
    private int _uAlpha1, _uAlpha2, _uAlpha3, _uShadow;
    private int _uCamPos, _uFogColor, _uFogRange, _uLightTint, _uFlatLight;

    private struct ChunkDraw
    {
        public int  Ebo;
        public int  IndexCount;
        public int  NLayers;
        public int  Cr, Cc;
        public int  Tex0, Tex1, Tex2, Tex3;
        public int  Alpha1, Alpha2, Alpha3;
        public int  Shadow;
    }
    private int _fallbackTex;

    private int _waterShader, _uWaterMVP, _uWaterCamPos, _uWaterFogColor, _uWaterFogRange, _uWaterHighlight;

    public bool HighlightLiquids { get; set; }
    private const int WaterStride = 4 * sizeof(float);

    private sealed class Tile
    {
        public int Vao, Vbo;
        public ChunkDraw[] Draws = [];
        public int WaterVao, WaterVbo, WaterEbo, WaterIndexCount;
        public Vector3 BoundsMin, BoundsMax;
    }
    private readonly Dictionary<AdtFile, Tile> _tiles = new();

    public int SetVisible(IReadOnlyCollection<AdtFile> adts, TextureCache? tex, int maxBuilds = int.MaxValue)
    {
        if (_shader == 0) { _shader = CreateShader(); CacheUniforms(); }
        if (_waterShader == 0)
        {
            _waterShader = CreateWaterShader();
            _uWaterMVP = GL.GetUniformLocation(_waterShader, "uMVP");
            _uWaterCamPos = GL.GetUniformLocation(_waterShader, "uCamPos");
            _uWaterFogColor = GL.GetUniformLocation(_waterShader, "uFogColor");
            _uWaterFogRange = GL.GetUniformLocation(_waterShader, "uFogRange");
            _uWaterHighlight = GL.GetUniformLocation(_waterShader, "uHighlight");
        }

        var wanted = adts as HashSet<AdtFile> ?? new HashSet<AdtFile>(adts);

        var gone = new List<AdtFile>();
        foreach (var key in _tiles.Keys) if (!wanted.Contains(key)) gone.Add(key);
        foreach (var key in gone) { FreeTile(_tiles[key]); _tiles.Remove(key); }

        int built = 0, pending = 0;
        foreach (var adt in adts)
        {
            if (_tiles.ContainsKey(adt)) continue;
            if (built < maxBuilds) { _tiles[adt] = BuildTile(adt, tex); built++; }
            else pending++;
        }

        _ready = true;
        return pending;
    }

    private Tile BuildTile(AdtFile adt, TextureCache? tex)
    {
        var verts           = new List<float>();
        var drawList        = new List<ChunkDraw>();
        var chunkIndexLists = new List<List<uint>>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
        for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
        {
            var chunk = adt.Chunks[cy, cx];
            if (chunk == null) continue;

            int baseVert = verts.Count / 8;
            var chunkIdx = new List<uint>();
            BuildChunk(chunk, verts, chunkIdx);
            chunkIndexLists.Add(chunkIdx);

            for (int v = baseVert; v < verts.Count / 8; v++)
            {
                var p = new Vector3(verts[v * 8], verts[v * 8 + 1], verts[v * 8 + 2]);
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }

            var cd = new ChunkDraw
            {
                IndexCount = chunkIdx.Count,
                NLayers    = Math.Max(1, chunk.NLayers),
                Cr = cy, Cc = cx,
            };
            cd.Tex0 = GetTex(tex, adt, chunk, 0);
            if (chunk.NLayers > 1) { cd.Tex1 = GetTex(tex, adt, chunk, 1); cd.Alpha1 = GetAlpha(tex, chunk, 0); }
            if (chunk.NLayers > 2) { cd.Tex2 = GetTex(tex, adt, chunk, 2); cd.Alpha2 = GetAlpha(tex, chunk, 1); }
            if (chunk.NLayers > 3) { cd.Tex3 = GetTex(tex, adt, chunk, 3); cd.Alpha3 = GetAlpha(tex, chunk, 2); }
            if (tex != null && chunk.ShadowMap != null) cd.Shadow = tex.UploadAlpha(chunk.ShadowMap);
            drawList.Add(cd);
        }

        var tile = new Tile { Draws = drawList.ToArray() };
        if (min.X > max.X) { min = max = Vector3.Zero; }

        tile.Vao = GL.GenVertexArray();
        GL.BindVertexArray(tile.Vao);
        tile.Vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, tile.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, Stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, Stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.BindVertexArray(0);

        for (int i = 0; i < tile.Draws.Length; i++)
        {
            var idxList = chunkIndexLists[i];
            int ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, idxList.Count * sizeof(uint), idxList.ToArray(), BufferUsageHint.StaticDraw);
            tile.Draws[i].Ebo = ebo;
        }

        BuildWater(adt, tile, ref min, ref max);

        tile.BoundsMin = min;
        tile.BoundsMax = max;
        return tile;
    }

    public void RebuildTile(AdtFile adt, TextureCache? tex)
    {
        if (_tiles.TryGetValue(adt, out var old))
        {
            FreeTile(old);
            _tiles[adt] = BuildTile(adt, tex);
        }
    }

    public void RebuildWater(AdtFile adt)
    {
        if (!_tiles.TryGetValue(adt, out var tile)) return;
        if (tile.WaterVao != 0)
        {
            GL.DeleteVertexArray(tile.WaterVao); GL.DeleteBuffer(tile.WaterVbo); GL.DeleteBuffer(tile.WaterEbo);
            tile.WaterVao = tile.WaterVbo = tile.WaterEbo = 0; tile.WaterIndexCount = 0;
        }
        var min = tile.BoundsMin; var max = tile.BoundsMax;
        BuildWater(adt, tile, ref min, ref max);
        tile.BoundsMin = min; tile.BoundsMax = max;
    }

    public void UpdateTileHeights(AdtFile adt)
    {
        if (!_tiles.TryGetValue(adt, out var tile) || tile.Vao == 0) return;

        var verts = new List<float>();
        var scratch = new List<uint>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
        for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
        {
            var chunk = adt.Chunks[cy, cx];
            if (chunk == null) continue;
            int baseVert = verts.Count / 8;
            scratch.Clear();
            BuildChunk(chunk, verts, scratch);
            for (int v = baseVert; v < verts.Count / 8; v++)
            {
                var p = new Vector3(verts[v * 8], verts[v * 8 + 1], verts[v * 8 + 2]);
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }

        GL.BindVertexArray(tile.Vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, tile.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.DynamicDraw);
        GL.BindVertexArray(0);

        if (min.X <= max.X) { tile.BoundsMin = Vector3.ComponentMin(tile.BoundsMin, min); tile.BoundsMax = Vector3.ComponentMax(tile.BoundsMax, max); }
    }

    private static void FreeTile(Tile t)
    {
        GL.DeleteVertexArray(t.Vao);
        GL.DeleteBuffer(t.Vbo);
        foreach (var d in t.Draws)
        {
            if (d.Ebo != 0) GL.DeleteBuffer(d.Ebo);
            if (d.Alpha1 != 0) GL.DeleteTexture(d.Alpha1);
            if (d.Alpha2 != 0) GL.DeleteTexture(d.Alpha2);
            if (d.Alpha3 != 0) GL.DeleteTexture(d.Alpha3);
            if (d.Shadow != 0) GL.DeleteTexture(d.Shadow);
        }
        if (t.WaterVao != 0) { GL.DeleteVertexArray(t.WaterVao); GL.DeleteBuffer(t.WaterVbo); GL.DeleteBuffer(t.WaterEbo); }
    }

    public bool TryGetChunkShadow(AdtFile adt, int cr, int cc, out int glId)
    {
        glId = 0;
        if (!_tiles.TryGetValue(adt, out var tile)) return false;
        foreach (var d in tile.Draws)
            if (d.Cr == cr && d.Cc == cc) { glId = d.Shadow; return glId != 0; }
        return false;
    }

    public bool TryGetChunkAlpha(AdtFile adt, int cr, int cc, int mapIdx, out int glId)
    {
        glId = 0;
        if (!_tiles.TryGetValue(adt, out var tile)) return false;
        foreach (var d in tile.Draws)
            if (d.Cr == cr && d.Cc == cc)
            {
                glId = mapIdx switch { 0 => d.Alpha1, 1 => d.Alpha2, 2 => d.Alpha3, _ => 0 };
                return glId != 0;
            }
        return false;
    }

    private static void BuildWater(AdtFile adt, Tile tile, ref Vector3 min, ref Vector3 max)
    {
        var verts   = new List<float>();
        var indices = new List<uint>();

        for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
        for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
        {
            var chunk = adt.Chunks[cy, cx];
            var liq = chunk?.Liquid;
            if (chunk == null || liq == null) continue;

            float type = liq.Type;
            for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
            {
                if (!liq.Render[row * 8 + col]) continue;

                uint b = (uint)(verts.Count / 4);
                EmitWater(verts, chunk, liq, col,     row,     type);
                EmitWater(verts, chunk, liq, col + 1, row,     type);
                EmitWater(verts, chunk, liq, col,     row + 1, type);
                EmitWater(verts, chunk, liq, col + 1, row + 1, type);
                indices.AddRange([b, b + 2, b + 1, b + 1, b + 2, b + 3]);
            }
        }

        tile.WaterIndexCount = indices.Count;
        if (tile.WaterIndexCount == 0) return;

        for (int v = 0; v < verts.Count / 4; v++)
        {
            var p = new Vector3(verts[v * 4], verts[v * 4 + 1], verts[v * 4 + 2]);
            min = Vector3.ComponentMin(min, p);
            max = Vector3.ComponentMax(max, p);
        }

        tile.WaterVao = GL.GenVertexArray();
        GL.BindVertexArray(tile.WaterVao);
        tile.WaterVbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, tile.WaterVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, WaterStride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, WaterStride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        tile.WaterEbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, tile.WaterEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
    }

    private static void EmitWater(List<float> v, MapChunk ch, LiquidLayer liq, int col, int row, float type)
    {
        var p = ChunkPos(ch, col, row);
        p.Y = liq.Heights[row * 9 + col];
        v.Add(p.X); v.Add(p.Y); v.Add(p.Z); v.Add(type);
    }

    public int VisibleTileCount { get; private set; }

    public void Render(Camera camera, float aspect)
    {
        if (!_ready || _tiles.Count == 0) return;

        var mvp = camera.View * camera.Projection(aspect);
        var frustum = new Frustum(mvp);

        var visible = new List<Tile>(_tiles.Count);
        foreach (var t in _tiles.Values)
            if (frustum.IntersectsBox(t.BoundsMin, t.BoundsMax)) visible.Add(t);
        VisibleTileCount = visible.Count;

        GL.UseProgram(_shader);
        GL.UniformMatrix4(_uMVP, false, ref mvp);
        GL.Uniform1(_uDebug, DebugMode);
        GL.Uniform1(_uIsolate, IsolateLayerIndex);
        GL.Uniform1(_uTex0,   0); GL.Uniform1(_uTex1,   1);
        GL.Uniform1(_uTex2,   2); GL.Uniform1(_uTex3,   3);
        GL.Uniform1(_uAlpha1, 4); GL.Uniform1(_uAlpha2, 5); GL.Uniform1(_uAlpha3, 6);
        GL.Uniform1(_uShadow, 7);
        var camPos = camera.Position;
        GL.Uniform3(_uCamPos, ref camPos);
        var fog = SceneEnv.HorizonColor;
        GL.Uniform3(_uFogColor, ref fog);
        GL.Uniform2(_uFogRange, SceneEnv.FogStart, SceneEnv.FogEnd);
        var tint = SceneEnv.LightTint;
        GL.Uniform3(_uLightTint, ref tint);
        GL.Uniform1(_uFlatLight, FlatLight ? 1 : 0);
        int fb = Fallback();

        foreach (var tile in visible)
        {
            GL.BindVertexArray(tile.Vao);
            foreach (var cd in tile.Draws)
            {
                if (cd.Ebo == 0 || cd.IndexCount == 0) continue;
                GL.Uniform1(_uNLayers, cd.NLayers);
                GL.Uniform1(_uHasShadow, cd.Shadow != 0 ? 1 : 0);
                Bind(0, cd.Tex0 != 0 ? cd.Tex0 : fb);
                Bind(1, cd.Tex1 != 0 ? cd.Tex1 : fb);
                Bind(2, cd.Tex2 != 0 ? cd.Tex2 : fb);
                Bind(3, cd.Tex3 != 0 ? cd.Tex3 : fb);
                Bind(4, cd.Alpha1);
                Bind(5, cd.Alpha2);
                Bind(6, cd.Alpha3);
                Bind(7, cd.Shadow);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, cd.Ebo);
                GL.DrawElements(PrimitiveType.Triangles, cd.IndexCount, DrawElementsType.UnsignedInt, 0);
            }
        }
        GL.BindVertexArray(0);

        if (DebugMode == 0)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.UseProgram(_waterShader);
            GL.UniformMatrix4(_uWaterMVP, false, ref mvp);
            GL.Uniform3(_uWaterCamPos, ref camPos);
            GL.Uniform3(_uWaterFogColor, ref fog);
            GL.Uniform2(_uWaterFogRange, SceneEnv.FogStart, SceneEnv.FogEnd);
            GL.Uniform1(_uWaterHighlight, HighlightLiquids ? 1 : 0);

            foreach (var tile in visible)
            {
                if (tile.WaterIndexCount == 0) continue;
                GL.BindVertexArray(tile.WaterVao);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, tile.WaterEbo);
                GL.DrawElements(PrimitiveType.Triangles, tile.WaterIndexCount, DrawElementsType.UnsignedInt, 0);
            }
            GL.BindVertexArray(0);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }
    }

    private static void Bind(int unit, int texId)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texId);
    }

    private static void BuildChunk(MapChunk chunk, List<float> verts, List<uint> indices)
    {
        uint baseIdx = (uint)(verts.Count / 8);

        var pos = new Vector3[145];
        int vi = 0;
        for (int row = 0; row < 9; row++)
        {
            for (int col = 0; col < 9; col++)
                pos[vi++] = ChunkPos(chunk, col, row);
            if (row < 8)
                for (int col = 0; col < 8; col++)
                    pos[vi++] = ChunkPos(chunk, col+.5f, row+.5f);
        }
        for (int i = 0; i < 145; i++)
            pos[i].Y += chunk.Heights[i];

        var nrm = new Vector3[145];
        for (int row = 0; row < 8; row++)
        for (int col = 0; col < 8; col++)
        {
            int tl=row*17+col, tr=tl+1, bl=(row+1)*17+col, br=bl+1, c=row*17+9+col;
            Acc(nrm,pos,tl,c,tr); Acc(nrm,pos,tr,c,br);
            Acc(nrm,pos,br,c,bl); Acc(nrm,pos,bl,c,tl);
        }
        for (int i=0;i<145;i++) { float l=nrm[i].Length; nrm[i]=l>1e-4f?nrm[i]/l:Vector3.UnitY; }

        vi = 0;
        for (int row = 0; row < 9; row++)
        {
            for (int col = 0; col < 9; col++) Emit(verts, pos[vi], nrm[vi], col/8f, row/8f, ref vi);
            if (row < 8)
                for (int col = 0; col < 8; col++) Emit(verts, pos[vi], nrm[vi], (col+.5f)/8f, (row+.5f)/8f, ref vi);
        }

        for (int row = 0; row < 8; row++)
        for (int col = 0; col < 8; col++)
        {
            uint tl=baseIdx+(uint)(row*17+col),   tr=tl+1,
                 bl=baseIdx+(uint)((row+1)*17+col), br=bl+1,
                 c =baseIdx+(uint)(row*17+9+col);

            int hRow=row/2, hCol=col/2;
            if ((chunk.Holes&(1<<(hRow*4+hCol)))!=0) continue;

            indices.AddRange([tl,c,tr, tr,c,br, br,c,bl, bl,c,tl]);
        }
    }

    private static Vector3 ChunkPos(MapChunk ch, float col, float row) =>
        new(-(ch.Position.Y - col * ChunkStep), ch.Position.Z, -(ch.Position.X - row * ChunkStep));

    private static void Emit(List<float> v, Vector3 p, Vector3 n, float u, float t, ref int vi)
    { v.Add(p.X);v.Add(p.Y);v.Add(p.Z); v.Add(n.X);v.Add(n.Y);v.Add(n.Z); v.Add(u);v.Add(t); vi++; }

    private static void Acc(Vector3[] n, Vector3[] p, int a, int b, int c)
    { var x=Vector3.Cross(p[b]-p[a],p[c]-p[a]); n[a]+=x;n[b]+=x;n[c]+=x; }

    private static void WeldBorders(List<float> verts, uint?[,] grid)
    {
        for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
        for (int cx = 0; cx < AdtFile.ChunksPerSide - 1; cx++)
        {
            if (!grid[cy, cx].HasValue || !grid[cy, cx + 1].HasValue) continue;
            int baseA = (int)grid[cy, cx]!.Value;
            int baseB = (int)grid[cy, cx + 1]!.Value;
            for (int r = 0; r <= 8; r++)
                Equalize(verts, baseA + r * 17 + 8, baseB + r * 17);
        }

        for (int cy = 0; cy < AdtFile.ChunksPerSide - 1; cy++)
        for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
        {
            if (!grid[cy, cx].HasValue || !grid[cy + 1, cx].HasValue) continue;
            int baseA = (int)grid[cy, cx]!.Value;
            int baseB = (int)grid[cy + 1, cx]!.Value;
            for (int c = 0; c <= 8; c++)
                Equalize(verts, baseA + c, baseB + 8 * 17 + c);
        }
    }

    private static void Equalize(List<float> v, int idxA, int idxB)
    {
        float avgY = (v[idxA * 8 + 1] + v[idxB * 8 + 1]) * 0.5f;
        v[idxA * 8 + 1] = avgY;
        v[idxB * 8 + 1] = avgY;

        float nx = (v[idxA * 8 + 3] + v[idxB * 8 + 3]) * 0.5f;
        float ny = (v[idxA * 8 + 4] + v[idxB * 8 + 4]) * 0.5f;
        float nz = (v[idxA * 8 + 5] + v[idxB * 8 + 5]) * 0.5f;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 1e-4f) { nx /= len; ny /= len; nz /= len; }
        v[idxA * 8 + 3] = nx; v[idxA * 8 + 4] = ny; v[idxA * 8 + 5] = nz;
        v[idxB * 8 + 3] = nx; v[idxB * 8 + 4] = ny; v[idxB * 8 + 5] = nz;
    }

    private static int GetTex(TextureCache? tex, AdtFile adt, MapChunk chunk, int layer)
    {
        if (tex == null || layer >= chunk.NLayers) return 0;
        uint idx = chunk.Layers[layer].TextureIndex;
        if (idx >= (uint)adt.Textures.Count) return 0;
        return tex.Get(adt.Textures[(int)idx]);
    }

    private static int GetAlpha(TextureCache? tex, MapChunk chunk, int mapIdx)
    {
        if (tex == null) return 0;
        var data = chunk.AlphaMaps[mapIdx];
        return data != null ? tex.UploadAlpha(data) : 0;
    }

    private int Fallback()
    {
        if (_fallbackTex != 0) return _fallbackTex;
        byte[] d = [120, 90, 60, 255];
        _fallbackTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fallbackTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 1, 1, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, d);
        return _fallbackTex;
    }

    private void CacheUniforms()
    {
        _uMVP    = GL.GetUniformLocation(_shader, "uMVP");
        _uNLayers= GL.GetUniformLocation(_shader, "uNLayers");
        _uDebug  = GL.GetUniformLocation(_shader, "uDebug");
        _uIsolate= GL.GetUniformLocation(_shader, "uIsolate");
        _uTex0   = GL.GetUniformLocation(_shader, "uTex0");
        _uTex1   = GL.GetUniformLocation(_shader, "uTex1");
        _uTex2   = GL.GetUniformLocation(_shader, "uTex2");
        _uTex3   = GL.GetUniformLocation(_shader, "uTex3");
        _uAlpha1 = GL.GetUniformLocation(_shader, "uAlpha1");
        _uAlpha2 = GL.GetUniformLocation(_shader, "uAlpha2");
        _uAlpha3 = GL.GetUniformLocation(_shader, "uAlpha3");
        _uShadow = GL.GetUniformLocation(_shader, "uShadow");
        _uHasShadow = GL.GetUniformLocation(_shader, "uHasShadow");
        _uCamPos = GL.GetUniformLocation(_shader, "uCamPos");
        _uFogColor = GL.GetUniformLocation(_shader, "uFogColor");
        _uFogRange = GL.GetUniformLocation(_shader, "uFogRange");
        _uLightTint = GL.GetUniformLocation(_shader, "uLightTint");
        _uFlatLight = GL.GetUniformLocation(_shader, "uFlatLight");
    }

    private static int CreateShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNorm;
            layout(location=2) in vec2 aUV;
            uniform mat4 uMVP;
            out vec3 vNorm;
            out vec2 vUV;
            out vec3 vWorld;
            void main() { vNorm=aNorm; vUV=aUV; vWorld=aPos; gl_Position=uMVP*vec4(aPos,1.0); }
            """;

        const string frag = """
            #version 330 core
            in vec3 vNorm;
            in vec2 vUV;
            in vec3 vWorld;
            uniform sampler2D uTex0, uTex1, uTex2, uTex3;
            uniform sampler2D uAlpha1, uAlpha2, uAlpha3;
            uniform sampler2D uShadow;
            uniform int uNLayers;
            uniform int uDebug;
            uniform int uIsolate;
            uniform int uHasShadow;
            uniform vec3 uCamPos;
            uniform vec3 uFogColor;
            uniform vec2 uFogRange;
            uniform vec3 uLightTint;
            uniform int uFlatLight;
            out vec4 FragColor;
            void main() {
                vec2 dUV = vUV * 8.0;
                vec2 aUV = (vUV * 63.0 + 0.5) / 64.0;

                if (uDebug == 1) { float a = texture(uAlpha1, aUV).r; FragColor = vec4(a, a, a, 1.0); return; }
                if (uDebug == 2) { FragColor = vec4(texture(uTex0, dUV).rgb, 1.0); return; }
                if (uDebug == 3) { FragColor = vec4(vUV, 0.0, 1.0); return; }
                if (uDebug == 4) {
                    vec3 sun=normalize(vec3(0.6,1.0,0.4));
                    float l=0.35+0.65*max(dot(normalize(vNorm),sun),0.0);
                    FragColor=vec4(vec3(l),1.0); return;
                }

                vec3 col;
                if (uIsolate >= 0) {
                    float cov = 1.0;
                    if (uIsolate == 0) col = texture(uTex0, dUV).rgb;
                    else if (uIsolate == 1 && uNLayers > 1) { col = texture(uTex1,dUV).rgb; cov = texture(uAlpha1,aUV).r; }
                    else if (uIsolate == 2 && uNLayers > 2) { col = texture(uTex2,dUV).rgb; cov = texture(uAlpha2,aUV).r; }
                    else if (uIsolate == 3 && uNLayers > 3) { col = texture(uTex3,dUV).rgb; cov = texture(uAlpha3,aUV).r; }
                    else col = vec3(0.04);
                    col *= mix(0.12, 1.0, cov);
                } else {
                    col = texture(uTex0, dUV).rgb;
                    if (uNLayers > 1) { float a=texture(uAlpha1,aUV).r; col=mix(col, texture(uTex1,dUV).rgb, a); }
                    if (uNLayers > 2) { float a=texture(uAlpha2,aUV).r; col=mix(col, texture(uTex2,dUV).rgb, a); }
                    if (uNLayers > 3) { float a=texture(uAlpha3,aUV).r; col=mix(col, texture(uTex3,dUV).rgb, a); }
                }
                if (uFlatLight == 1) {
                    vec3 nn = normalize(vNorm);
                    float nd = max(dot(nn, normalize(vec3(0.5, 1.0, 0.4))), 0.0);
                    FragColor = vec4(col * (0.80 + 0.20 * nd), 1.0);
                    return;
                }
                vec3 N = normalize(vNorm);
                vec3 sun = normalize(vec3(0.6, 1.0, 0.4));
                float ndl = max(dot(N, sun), 0.0);
                float hemi = 0.5 + 0.5 * clamp(N.y, -1.0, 1.0);
                vec3 ambient = mix(vec3(0.30,0.32,0.38), vec3(0.55,0.55,0.50), hemi);
                float shadow = (uHasShadow == 1) ? mix(1.0, 0.45, texture(uShadow, aUV).r) : 1.0;
                vec3 lit = col * (ambient + vec3(1.0,0.96,0.86) * (ndl * 0.85 * shadow));
                lit *= uLightTint;

                float dist = length(vWorld - uCamPos);
                float fog = clamp((dist - uFogRange.x) / (uFogRange.y - uFogRange.x), 0.0, 1.0);
                FragColor = vec4(mix(lit, uFogColor, fog), 1.0);
            }
            """;

        int vs=Compile(ShaderType.VertexShader,vert), fs=Compile(ShaderType.FragmentShader,frag);
        int p=GL.CreateProgram();
        GL.AttachShader(p,vs); GL.AttachShader(p,fs); GL.LinkProgram(p);
        GL.DetachShader(p,vs); GL.DetachShader(p,fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        return p;
    }

    private static int Compile(ShaderType t, string src)
    {
        int id=GL.CreateShader(t); GL.ShaderSource(id,src); GL.CompileShader(id);
        GL.GetShader(id,ShaderParameter.CompileStatus,out int ok);
        if(ok==0) throw new Exception(GL.GetShaderInfoLog(id));
        return id;
    }

    private static int CreateWaterShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in float aType;
            uniform mat4 uMVP;
            out float vType;
            out vec3 vWorld;
            void main() { vType=aType; vWorld=aPos; gl_Position=uMVP*vec4(aPos,1.0); }
            """;

        const string frag = """
            #version 330 core
            in float vType;
            in vec3 vWorld;
            uniform vec3 uCamPos;
            uniform vec3 uFogColor;
            uniform vec2 uFogRange;
            uniform int uHighlight;
            out vec4 FragColor;
            void main() {
                vec3 c; float a;
                if (vType > 2.5 && vType < 3.5)      { c = vec3(0.95, 0.35, 0.05); a = 0.95; }
                else if (vType > 3.5)                { c = vec3(0.30, 0.70, 0.15); a = 0.80; }
                else                                 { c = vec3(0.13, 0.34, 0.55); a = 0.55; }
                if (uHighlight == 1) { c = mix(c, vec3(0.4, 0.9, 1.0), 0.35); a = min(a + 0.2, 1.0); }
                float dist = length(vWorld - uCamPos);
                float fog = clamp((dist - uFogRange.x) / (uFogRange.y - uFogRange.x), 0.0, 1.0);
                FragColor = vec4(mix(c, uFogColor, fog), a);
            }
            """;

        int vs=Compile(ShaderType.VertexShader,vert), fs=Compile(ShaderType.FragmentShader,frag);
        int p=GL.CreateProgram();
        GL.AttachShader(p,vs); GL.AttachShader(p,fs); GL.LinkProgram(p);
        GL.DetachShader(p,vs); GL.DetachShader(p,fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        return p;
    }

    public void Dispose()
    {
        foreach (var t in _tiles.Values) FreeTile(t);
        _tiles.Clear();
        if (_fallbackTex != 0) GL.DeleteTexture(_fallbackTex);
        if (_shader != 0) GL.DeleteProgram(_shader);
        if (_waterShader != 0) GL.DeleteProgram(_waterShader);
        _shader = 0; _waterShader = 0;
        _ready = false;
    }
}
