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
    private bool _minimapOn;
    private int _minimapTex;
    private int _mmW, _mmH, _mmMinTx, _mmMinTy, _mmTilePx;
    private string? _mmTexMap;
    private volatile bool _mmBuilding, _mmHasPending;
    private Salmiak.Core.IO.MinimapOverview? _mmPending;
    private string? _mmPendingMap;
    private (float X, float Y, float W, float H) _mmRect;

    public void ToggleMinimap()
    {
        _minimapOn = !_minimapOn;
        if (_minimapOn) EnsureMinimap();
        Notify?.Invoke(_minimapOn ? "Minimap on." : "Minimap off.");
    }

    private void EnsureMinimap()
    {
        if (_mpq == null || string.IsNullOrEmpty(_mapName) || _mmBuilding) return;
        if (_mmTexMap == _mapName && _minimapTex != 0) return;
        _mmBuilding = true;
        var mpq = _mpq; var map = _mapName;
        System.Threading.Tasks.Task.Run(() =>
        {
            var ov = Salmiak.Core.IO.MinimapOverview.Build(mpq, map, 24);
            _mmPending = ov; _mmPendingMap = map; _mmHasPending = true; _mmBuilding = false;
        });
    }

    private void DrainMinimap()
    {
        if (!_mmHasPending) return;
        _mmHasPending = false;
        var ov = _mmPending; _mmPending = null;
        _mmTexMap = _mmPendingMap;
        if (ov == null) { Notify?.Invoke($"No minimap for {_mmPendingMap}."); return; }
        if (_minimapTex != 0) GL.DeleteTexture(_minimapTex);
        _minimapTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _minimapTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, ov.Width, ov.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, ov.Rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _mmW = ov.Width; _mmH = ov.Height; _mmMinTx = ov.MinTx; _mmMinTy = ov.MinTy; _mmTilePx = ov.TilePx;
    }

    private void DrawMinimap(int w, int h)
    {
        if (!_minimapOn || _text == null || _minimapTex == 0 || _mmTexMap != _mapName) { _mmRect = default; return; }
        const float max = 286f, mar = 12f;
        float mw, mh;
        if (_mmW >= _mmH) { mw = max; mh = max * _mmH / _mmW; } else { mh = max; mw = max * _mmW / _mmH; }
        float mx = w - mw - mar, my = h - mh - mar;
        _mmRect = (mx, my, mw, mh);
        _text.DrawRect(mx - 2, my - 2, mw + 4, mh + 4, 0f, 0f, 0f, 0.55f, w, h);
        _text.DrawImage(_minimapTex, mx, my, mw, mh, 0.72f, w, h);
        float ts = AdtFile.TileSize;
        float fx = (32 + _camera.Position.X / ts - _mmMinTx) * _mmTilePx / _mmW;
        float fy = (32 + _camera.Position.Z / ts - _mmMinTy) * _mmTilePx / _mmH;
        if (fx >= 0 && fx <= 1 && fy >= 0 && fy <= 1)
        {
            float cx = mx + fx * mw, cy = my + fy * mh;
            _text.DrawRect(cx - 3, cy - 3, 6, 6, 1f, 0.9f, 0.1f, 1f, w, h);
        }
    }

    private bool CursorOverMinimap() =>
        _minimapOn && _minimapTex != 0 && _mmRect.W > 0 &&
        _mouseX >= _mmRect.X && _mouseX <= _mmRect.X + _mmRect.W &&
        _mouseY >= _mmRect.Y && _mouseY <= _mmRect.Y + _mmRect.H;

    private bool MinimapClick(int mx, int my)
    {
        if (!_minimapOn || _minimapTex == 0 || _mmRect.W <= 0) return false;
        if (mx < _mmRect.X || mx > _mmRect.X + _mmRect.W || my < _mmRect.Y || my > _mmRect.Y + _mmRect.H) return false;
        float fx = (mx - _mmRect.X) / _mmRect.W, fy = (my - _mmRect.Y) / _mmRect.H;
        float ts = AdtFile.TileSize;
        float glx = (fx * _mmW / _mmTilePx + _mmMinTx - 32) * ts;
        float glz = (fy * _mmH / _mmTilePx + _mmMinTy - 32) * ts;
        TeleportCameraTo(glx, 250f, glz);
        return true;
    }

    private const int MinimapTilePx = 256;

    private int AddMinimapFiles(Dictionary<string, byte[]> files)
    {
        if (_mpq == null || _texCache == null || string.IsNullOrEmpty(_mapName)) return 0;
        if (!IsHandleCreated) return 0;
        MakeCurrent();

        int fbo = GL.GenFramebuffer();
        int color = GL.GenTexture();
        int depth = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.BindTexture(TextureTarget.Texture2D, color);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, MinimapTilePx, MinimapTilePx, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, MinimapTilePx, MinimapTilePx);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);

        var terr = new TerrainRenderer { FlatLight = true };
        var wmos = new WmoRenderer(_mpq) { RenderDistance = float.MaxValue };
        var editedAdts = _editedTiles.Values.ToList();
        terr.SetVisible(editedAdts, _texCache);
        wmos.Load(editedAdts, _texCache);

        float fogStart = SceneEnv.FogStart, fogEnd = SceneEnv.FogEnd;
        var lightTint = SceneEnv.LightTint;
        SceneEnv.FogStart = 1e9f; SceneEnv.FogEnd = 1e9f + 1f;
        SceneEnv.LightTint = Vector3.One;
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.Viewport(0, 0, MinimapTilePx, MinimapTilePx);

        const float ts = AdtFile.TileSize;
        var cam = new Camera();
        var rgba = new byte[MinimapTilePx * MinimapTilePx * 4];
        var flipped = new byte[rgba.Length];
        var trsEdits = new List<(int tx, int ty, string stored)>();

        foreach (var kv in _editedTiles)
        {
            var (tx, ty) = kv.Key;
            float cx = (tx - 31.5f) * ts, cz = (ty - 31.5f) * ts;
            var eye = new Vector3(cx, 12000f, cz);
            cam.Position = eye;
            cam.ViewOverride = Matrix4.LookAt(eye, new Vector3(cx, 0f, cz), new Vector3(0f, 0f, -1f));
            cam.ProjectionOverride = Matrix4.CreateOrthographic(ts, ts, 1f, 24000f);

            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            terr.Render(cam, 1f);
            wmos.Render(cam, 1f);
            GL.ReadPixels(0, 0, MinimapTilePx, MinimapTilePx, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);

            int rowBytes = MinimapTilePx * 4;
            for (int y = 0; y < MinimapTilePx; y++)
                Array.Copy(rgba, y * rowBytes, flipped, (MinimapTilePx - 1 - y) * rowBytes, rowBytes);

            byte[] blp = BlpWriter.EncodeDxt1(flipped, MinimapTilePx, MinimapTilePx);
            string stored = $"mm_{_mapName.ToLowerInvariant()}_{tx}_{ty}.blp";
            files[$@"textures\Minimap\{stored}"] = blp;
            trsEdits.Add((tx, ty, stored));
        }

        SceneEnv.FogStart = fogStart; SceneEnv.FogEnd = fogEnd;
        SceneEnv.LightTint = lightTint;
        if (cull) GL.Enable(EnableCap.CullFace);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RenderTarget.Screen);
        GL.Viewport(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        GL.DeleteFramebuffer(fbo);
        GL.DeleteTexture(color);
        GL.DeleteRenderbuffer(depth);
        terr.Dispose();
        wmos.Dispose();

        if (trsEdits.Count > 0)
            files[@"textures\Minimap\md5translate.trs"] = BuildPatchedTrs(trsEdits);
        return trsEdits.Count;
    }

    private byte[] BuildPatchedTrs(List<(int tx, int ty, string stored)> edits)
    {
        var lines = new List<string>();
        try
        {
            using var s = _mpq!.OpenFile(@"textures\Minimap\md5translate.trs");
            using var sr = new System.IO.StreamReader(s);
            string all = sr.ReadToEnd();
            lines.AddRange(all.Replace("\r\n", "\n").Split('\n'));
        }
        catch { }

        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tx, ty, stored) in edits)
            pending[$@"{_mapName}\map{tx}_{ty}.blp"] = stored;

        for (int i = 0; i < lines.Count; i++)
        {
            int tab = lines[i].IndexOf('\t');
            if (tab <= 0) continue;
            string logical = lines[i].Substring(0, tab);
            if (pending.TryGetValue(logical, out var stored))
            {
                lines[i] = logical + "\t" + stored;
                pending.Remove(logical);
            }
        }
        if (pending.Count > 0)
        {
            lines.Add($"dir: {_mapName}");
            foreach (var kv in pending) lines.Add(kv.Key + "\t" + kv.Value);
        }

        return System.Text.Encoding.ASCII.GetBytes(string.Join("\r\n", lines.Where(l => l.Length > 0)) + "\r\n");
    }
}
