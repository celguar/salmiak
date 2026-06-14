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
    private bool _wireframe;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastMs;

    public event Action<Vector3>? CameraUpdated;
    public event Action<string>? DebugModeChanged;

    private static readonly string[] DebugModeNames =
    {
        "Normal", "Alpha layer 1", "Diffuse only (no blend)", "UV coords", "Lighting only",
    };

    private int _fpsCount;
    private long _fpsWindowStart;
    private float _fps, _frameMsAvg;
    private double _frameMsAccum;

    private int _tickNo;

    private void OnRenderTick(TimeSpan _)
    {
        RenderTarget.Screen = Framebuffer;
        _glDc = wglGetCurrentDC();
        _glRc = wglGetCurrentContext();
        if (!_glReady)
        {
            _glReady = true;
            var hz = SceneEnv.HorizonColor;
            GL.ClearColor(hz.X, hz.Y, hz.Z, 1f);
            GL.Enable(EnableCap.DepthTest);
            _sky = new SkyRenderer();
            _brush = new BrushOverlay();
            _text = new TextOverlay();
            _gizmo = new GizmoRenderer();
        }
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX; _dpiY = dpi.DpiScaleY;
        if (_pendingStream is { } ps) { _pendingStream = null; StreamMap(ps.Wdt, ps.Name); }
        PollMouse();

        _tickNo++;
        if (ModalOpen)
        {
            if ((_tickNo & 7) != 0) return;
        }
        else
        {
            bool interactive = _cursorInside || _lmb || _rmb || _flightDrawing;
            if (!interactive && (_tickNo & 3) != 0) return;
        }

        long t0 = _sw.ElapsedTicks;
        RenderFrame();
        _frameMsAccum += (_sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
        _fpsCount++;
        long nowMs = _sw.ElapsedMilliseconds;
        if (nowMs - _fpsWindowStart >= 1000)
        {
            _fps = _fpsCount * 1000f / (nowMs - _fpsWindowStart);
            _frameMsAvg = (float)(_frameMsAccum / Math.Max(1, _fpsCount));
            _fpsCount = 0; _frameMsAccum = 0;
            _fpsWindowStart = nowMs;
        }
    }

    private void RenderFrame()
    {
        if (!IsHandleCreated) return;
        GL.Viewport(0, 0, Width, Height);

        long now = _sw.ElapsedMilliseconds;
        float dt = (now - _lastMs) / 1000f;
        _lastMs = now;

        bool active = IsAppActive();
        if (active) ProcessKeyboardInput(dt);
        if (active && (EditMode || TextureMode) && !DoodadMode && !WmoMode && !_blendMode && !GenerateMode && !_areaMode && !_hmPlacing && _lmb) EditStep(dt);
        UpdateStreaming();

        if (_doodads != null && _doodads.DrainReady() > 0) RefreshPlacements();
        DrainMinimap();

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        float aspect = Width > 0 && Height > 0 ? (float)Width / Height : 1f;
        if (_doodads != null) _doodads.Highlight = DoodadMode;
        if (_wmos != null) _wmos.Highlight = WmoMode;
        if (_terrain != null) _terrain.HighlightLiquids = LiquidMode;
        _sky?.Render(_camera, aspect);
        if (_terrain != null) _terrain.IsolateLayerIndex = (TextureMode && IsolateLayer) ? PaintLayer : -1;
        _terrain?.Render(_camera, aspect);
        _doodads?.Render(_camera, aspect);
        _wmos?.Render(_camera, aspect);

        if (FlightMode && _brush != null)
        {
            float netAlpha = _flightDrawing ? 0.1f : _selectedPath != null ? 0.25f : 1f;
            if (_flightVertCount >= 2)
                _brush.RenderColored(_camera, aspect, _flightVerts, _flightVertCount, PrimitiveType.Lines, 0.2f, 0.9f, 1f, netAlpha);
            if (_selPathVertCount >= 2)
                _brush.RenderColored(_camera, aspect, _selPathVerts, _selPathVertCount, PrimitiveType.Lines, 1f, 0.85f, 0.1f);
            if (_wpVertCount >= 2)
                _brush.RenderColored(_camera, aspect, _wpVerts, _wpVertCount, PrimitiveType.Lines, 0.5f, 1f, 0.5f);
            if (_stopVertCount >= 2)
                _brush.RenderColored(_camera, aspect, _stopVerts, _stopVertCount, PrimitiveType.Lines, 1f, 0.55f, 0.1f);
            if (_selNodeVertCount >= 2)
                _brush.RenderColored(_camera, aspect, _selNodeVerts, _selNodeVertCount, PrimitiveType.Lines, 1f, 0.2f, 0.9f);
        }

        if (RiverMode && _brush != null && _riverVertCount >= 2)
            _brush.RenderColored(_camera, aspect, _riverVerts, _riverVertCount, PrimitiveType.Lines, 0.25f, 0.85f, 1f);

        if (NpcMode && _brush != null)
        {
            if (_patrolVertCount >= 2)
                _brush.RenderColored(_camera, aspect, _patrolVerts, _patrolVertCount, PrimitiveType.Lines, 1f, 0.6f, 0.15f);
            if (_patrolSelVertCount >= 2)
                _brush.RenderColored(_camera, aspect, _patrolSelVerts, _patrolSelVertCount, PrimitiveType.Lines, 1f, 0.2f, 0.9f);
        }

        if (LiquidMode && _brush != null) DrawLiquidOverlay(aspect);

        if (WmoMode && !string.IsNullOrEmpty(_placeWmo) && _wmos != null &&
            PickPlacement(_mouseX, _mouseY, out var wmoHit))
        {
            _wmos.RenderPreview(_camera, aspect, BuildWmoDef(wmoHit));
        }
        else if (DoodadMode && !string.IsNullOrEmpty(_placeDoodad) && _doodads != null &&
            PickPlacement(_mouseX, _mouseY, out var ghostHit))
        {
            _doodads.RenderPreview(_camera, aspect, BuildPlacementDef(ghostHit));
        }
        else if ((EditMode || TextureMode) && !DoodadMode && !WmoMode && !_blendMode && !GenerateMode && !_areaMode && !_hmPlacing && !RiverMode && _brush != null && !CursorOverMinimap() && PickTerrain(_mouseX, _mouseY, out var brushHit))
        {
            int ringN = BuildBrushRing(brushHit);
            if (TextureMode)
                _brush.RenderColored(_camera, aspect, _ring, ringN, PrimitiveType.LineLoop, 0.85f, 0.45f, 1f);
            else
                _brush.Render(_camera, aspect, _ring, ringN);
        }

        if (GenerateMode && _brush != null && PickTerrain(_mouseX, _mouseY, out var genHit) &&
            TryGetChunkAt(genHit, out _, out var hoverChunk, out _, out _, out _, out _))
        {
            _brush.Render(_camera, aspect, _outline, BuildChunkOutline(hoverChunk));
        }

        if (_areaMode && _brush != null)
        {
            if (_zoneOverlayStamp != _tileCache.Count) RebuildZoneOverlay();
            foreach (var (v, n, r, g, b) in _zoneBatches)
                if (n >= 2) _brush.RenderColored(_camera, aspect, v, n, PrimitiveType.Lines, r, g, b, 0.8f);

            if (_zoneRectAnchor is { } za)
            {
                const float cs2 = AdtFile.ChunkSize, half = 32f * AdtFile.TileSize;
                int c0 = Math.Min(za.Col, _zoneRectCur.Col), c1 = Math.Max(za.Col, _zoneRectCur.Col) + 1;
                int r0 = Math.Min(za.Row, _zoneRectCur.Row), r1 = Math.Max(za.Row, _zoneRectCur.Row) + 1;
                var rect = new float[]
                {
                    c0 * cs2 - half, 0, r0 * cs2 - half,  c1 * cs2 - half, 0, r0 * cs2 - half,
                    c1 * cs2 - half, 0, r1 * cs2 - half,  c0 * cs2 - half, 0, r1 * cs2 - half,
                };
                for (int i = 0; i < 4; i++)
                {
                    float hY = HeightAtGL(rect[i * 3], rect[i * 3 + 2]);
                    rect[i * 3 + 1] = (float.IsNaN(hY) ? _camera.Position.Y - 50f : hY) + 1f;
                }
                _brush.RenderColored(_camera, aspect, rect, 4, PrimitiveType.LineLoop, 1f, 1f, 1f);
            }
            else if (PickTerrain(_mouseX, _mouseY, out var areaHit) &&
                     TryGetChunkAt(areaHit, out _, out var areaHover, out _, out _, out _, out _))
            {
                _brush.Render(_camera, aspect, _outline, BuildChunkOutline(areaHover));
            }
        }

        if (_hmPlacing && _hmCornerA != null && _brush != null &&
            PickTerrain(_mouseX, _mouseY, out var hmCur))
        {
            int n = BuildRectOutline(_hmCornerA.Value, hmCur);
            if (n >= 2) _brush.RenderColored(_camera, aspect, _rectOutline, n, PrimitiveType.LineLoop, 0.25f, 1f, 0.5f);
        }

        if (_blendMode && _brush != null)
        {
            if (_compatVerts > 0)
                _brush.RenderColored(_camera, aspect, _compatBuf, _compatVerts,
                    OpenTK.Graphics.OpenGL4.PrimitiveType.Lines, 0.3f, 1f, 0.45f);
            foreach (var sel in _blendSel)
            {
                var ch = sel.Adt.Chunks[sel.CRow, sel.CCol];
                if (ch != null) _brush.Render(_camera, aspect, _outline, BuildChunkOutline(ch));
            }
        }

        bool armed = (DoodadMode && !string.IsNullOrEmpty(_placeDoodad)) || (WmoMode && !string.IsNullOrEmpty(_placeWmo));
        if (!armed && (DoodadMode || WmoMode) && _gizmo != null && _sel != null &&
            TryGetSelTransform(out var gc, out var gr, out var gIsWmo))
        {
            _gizmo.Render(_camera, aspect, gc, gr, _gizmoAxis, !gIsWmo);
        }

        if (_screenshotPending) { _screenshotPending = false; CaptureScreenshot(); }

        DrawOverlay();

        CameraUpdated?.Invoke(_camera.Position);
    }
}
