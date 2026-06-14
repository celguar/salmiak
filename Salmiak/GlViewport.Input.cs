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

    private bool _prevDel;

    private bool _prevP;
    private bool _prevT;
    private bool _prevY;
    private bool _prevB;
    private bool _prevDelete;

    private bool _rmb;
    private bool _rmbMoved;
    private bool _lmb;
    private int _lastMx, _lastMy;
    private int _mouseX, _mouseY;
    private bool _prevF;
    private bool _prevG;

    private bool _prevLiqDel;

    private bool _prevCtrlL;

    private bool _prevCtrlA;

    private bool _prevCtrlG;

    private bool _prevCtrlR, _prevCtrlN;

    private bool _prevCtrlT;
    private bool _prevI;
    private bool _prevX;
    private bool _prevShot;

    private bool _prevCtrlP;

    private bool _prevN;
    private bool _prevEsc;

    private bool _prevF1;

    private bool _prevM;

    private bool _prevCtrlS, _prevCtrlO, _prevCtrlD, _prevCtrlE, _prevCtrlW, _prevCtrlC, _prevCtrlV, _prevCtrlQ;
    private bool _prevCtrlF, _prevCtrlH, _prevJ, _prevCtrlB, _prevCtrlK, _prevCtrlU, _prevCtrlM;

    private bool _prevO;

    private bool _prevR;

    private bool _prevL;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ModalOpen { get; set; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point p);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(System.Drawing.Point p);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private int _lastPollX = int.MinValue, _lastPollY;
    private bool _pollL, _pollR, _pollM;
    private bool _cursorInside;

    private void PollMouse()
    {
        var src = System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        if (src == null) return;
        if (!GetCursorPos(out var sp)) return;
        System.Windows.Point dip;
        try { dip = PointFromScreen(new System.Windows.Point(sp.X, sp.Y)); }
        catch { return; }
        int x = (int)Math.Round(dip.X * _dpiX), y = (int)Math.Round(dip.Y * _dpiY);
        bool inside = x >= 0 && y >= 0 && x < Width && y < Height &&
                      WindowFromPoint(sp) == src.Handle;
        _cursorInside = inside;

        if ((x != _lastPollX || y != _lastPollY) && (inside || _lmb || _rmb))
            HandleMouseMove(new MouseEventArgs(MouseButtons.None, 1, x, y, 0));
        _lastPollX = x; _lastPollY = y;

        bool swap = GetSystemMetrics(23) != 0;
        bool l = (GetAsyncKeyState(swap ? 0x02 : 0x01) & 0x8000) != 0;
        bool r = (GetAsyncKeyState(swap ? 0x01 : 0x02) & 0x8000) != 0;
        bool m = (GetAsyncKeyState(0x04) & 0x8000) != 0;
        bool canPress = inside && !ModalOpen && IsAppActive();

        if (l != _pollL)
        {
            if (l && canPress) HandleMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
            else if (!l && _lmb) HandleMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
            _pollL = l;
        }
        if (r != _pollR)
        {
            if (r && canPress) HandleMouseDown(new MouseEventArgs(MouseButtons.Right, 1, x, y, 0));
            else if (!r && _rmb) HandleMouseUp(new MouseEventArgs(MouseButtons.Right, 1, x, y, 0));
            _pollR = r;
        }
        if (m != _pollM)
        {
            if (m && canPress) HandleMouseDown(new MouseEventArgs(MouseButtons.Middle, 1, x, y, 0));
            else if (!m) HandleMouseUp(new MouseEventArgs(MouseButtons.Middle, 1, x, y, 0));
            _pollM = m;
        }
    }

    private void ProcessKeyboardInput(float dt)
    {
        if (ModalOpen) return;

        bool ctrl = (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;

        float dx = 0, dy = 0, dz = 0;
        if (!ctrl)
        {
            if (IsKeyDown(Keys.W) || IsKeyDown(Keys.Up))    dz += 1;
            if (IsKeyDown(Keys.S) || IsKeyDown(Keys.Down))  dz -= 1;
            if (IsKeyDown(Keys.A) || IsKeyDown(Keys.Left))  dx -= 1;
            if (IsKeyDown(Keys.D) || IsKeyDown(Keys.Right)) dx += 1;
            if (IsKeyDown(Keys.E) || IsKeyDown(Keys.Space)) dy += 1;
            if (IsKeyDown(Keys.Q))                          dy -= 1;
        }
        if (dx != 0 || dy != 0 || dz != 0)
            _camera.Move(dx, dy, dz, dt);

        bool f1 = IsKeyDown(Keys.F1);
        if (f1 && !_prevF1) HelpToggled?.Invoke();
        _prevF1 = f1;

        bool mNow = IsKeyDown(Keys.M) && !ctrl && !ModalOpen;
        if (mNow && !_prevM) ToggleMinimap();
        _prevM = mNow;

        bool delSelNow = ((DoodadMode && _selSet.Count > 0) || (WmoMode && _sel is { IsWmo: true }))
            && (IsKeyDown(Keys.Delete) || IsKeyDown(Keys.Back)) && !ModalOpen;
        if (delSelNow && !_prevDel)
        {
            if (DoodadMode && _selSet.Count > 0) DeleteSelection();
            else if (_sel is { IsWmo: true } sw)
            {
                ClearSelection();
                DeleteWmo(sw.Adt, sw.Index);
            }
        }
        _prevDel = delSelNow;

        bool ctrlS = ctrl && IsKeyDown(Keys.S);
        if (ctrlS && !_prevCtrlS) { _prevCtrlS = true; SaveRequested?.Invoke(); } else _prevCtrlS = ctrlS;
        bool ctrlO = ctrl && IsKeyDown(Keys.O);
        if (ctrlO && !_prevCtrlO) { _prevCtrlO = true; LoadRequested?.Invoke(); } else _prevCtrlO = ctrlO;
        bool ctrlQ = ctrl && IsKeyDown(Keys.Q);
        if (ctrlQ && !_prevCtrlQ)
        {
            _prevCtrlQ = true;
            DoodadFollow = !DoodadFollow;
            Notify?.Invoke($"Doodads & WMOs follow terrain: {(DoodadFollow ? "ON" : "off")}");
        }
        else _prevCtrlQ = ctrlQ;
        bool ctrlD = ctrl && IsKeyDown(Keys.D);
        if (ctrlD && !_prevCtrlD) { _prevCtrlD = true; TogglePrimaryMode(EditorMode.Doodad); } else _prevCtrlD = ctrlD;
        bool ctrlE = ctrl && IsKeyDown(Keys.E);
        if (ctrlE && !_prevCtrlE) { _prevCtrlE = true; TogglePrimaryMode(EditorMode.Edit); } else _prevCtrlE = ctrlE;
        bool ctrlW = ctrl && IsKeyDown(Keys.W);
        if (ctrlW && !_prevCtrlW) { _prevCtrlW = true; TogglePrimaryMode(EditorMode.Wmo); } else _prevCtrlW = ctrlW;
        bool ctrlF = ctrl && IsKeyDown(Keys.F);
        if (ctrlF && !_prevCtrlF) { _prevCtrlF = true; ToggleSculptTool(SculptTool.Flatten); } else _prevCtrlF = ctrlF;
        bool ctrlH = ctrl && IsKeyDown(Keys.H);
        if (ctrlH && !_prevCtrlH) { _prevCtrlH = true; ToggleSculptTool(SculptTool.Delete); } else _prevCtrlH = ctrlH;
        bool ctrlB = ctrl && IsKeyDown(Keys.B);
        if (ctrlB && !_prevCtrlB) { _prevCtrlB = true; ToggleSculptTool(SculptTool.Smooth); } else _prevCtrlB = ctrlB;
        bool ctrlK = ctrl && IsKeyDown(Keys.K);
        if (ctrlK && !_prevCtrlK) { _prevCtrlK = true; ToggleSculptTool(SculptTool.Noise); } else _prevCtrlK = ctrlK;
        bool ctrlJ = ctrl && IsKeyDown(Keys.J);
        if (ctrlJ && !_prevCtrlJ) { _prevCtrlJ = true; ToggleRiverMode(); } else _prevCtrlJ = ctrlJ;
        bool ctrlM = ctrl && IsKeyDown(Keys.M);
        if (ctrlM && !_prevCtrlM) { _prevCtrlM = true; ToggleSculptTool(SculptTool.Shadow); } else _prevCtrlM = ctrlM;
        bool ctrlU = ctrl && IsKeyDown(Keys.U);
        if (ctrlU && !_prevCtrlU) { _prevCtrlU = true; TogglePrimaryMode(EditorMode.Liquid); } else _prevCtrlU = ctrlU;
        bool ctrlC = ctrl && IsKeyDown(Keys.C);
        if (ctrlC && !_prevCtrlC) { _prevCtrlC = true; ClipboardCopy(); } else _prevCtrlC = ctrlC;
        bool ctrlV = ctrl && IsKeyDown(Keys.V);
        if (ctrlV && !_prevCtrlV) { _prevCtrlV = true; ClipboardPaste(); } else _prevCtrlV = ctrlV;
        bool ctrlL = ctrl && IsKeyDown(Keys.L);
        if (ctrlL && !_prevCtrlL) { _prevCtrlL = true; ToggleBlendMode(); } else _prevCtrlL = ctrlL;
        bool ctrlG = ctrl && IsKeyDown(Keys.G);
        if (ctrlG && !_prevCtrlG) { _prevCtrlG = true; ToggleGenerateMode(); } else _prevCtrlG = ctrlG;
        bool ctrlA = ctrl && IsKeyDown(Keys.A);
        if (ctrlA && !_prevCtrlA) { _prevCtrlA = true; ToggleAreaMode(); } else _prevCtrlA = ctrlA;
        bool ctrlR = ctrl && IsKeyDown(Keys.R);
        if (ctrlR && !_prevCtrlR) { _prevCtrlR = true; ToggleRoadMode(); } else _prevCtrlR = ctrlR;
        bool ctrlP = ctrl && !shift && IsKeyDown(Keys.P);
        if (ctrlP && !_prevCtrlP) { _prevCtrlP = true; ToggleAutoPaintMode(); } else _prevCtrlP = ctrlP;
        bool ctrlN = ctrl && IsKeyDown(Keys.N);
        if (ctrlN && !_prevCtrlN)
        {
            _prevCtrlN = true;
            if (!ModalOpen)
            {
                if (RoadMode) OpenRoadPicker?.Invoke();
                else if (AutoPaintMode) OpenAutoPaintPicker?.Invoke();
                else if (LiquidMode) OpenLiquidPicker?.Invoke();
            }
        }
        else _prevCtrlN = ctrlN;
        bool ctrlT = ctrl && IsKeyDown(Keys.T);
        if (ctrlT && !_prevCtrlT) { _prevCtrlT = true; TogglePrimaryMode(EditorMode.Texture); } else _prevCtrlT = ctrlT;

        bool iNow = IsKeyDown(Keys.I) && !ctrl;
        if (iNow && !_prevI && TextureMode)
        {
            IsolateLayer = !IsolateLayer;
            Notify?.Invoke(IsolateLayer ? $"Isolating layer {PaintLayer}." : "Showing all layers.");
        }
        _prevI = iNow;

        bool fNow = IsKeyDown(Keys.F) && !ctrl;
        if (fNow && !_prevF)
        {
            _wireframe = !_wireframe;
            GL.PolygonMode(TriangleFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);
        }
        _prevF = fNow;

        bool shotNow = ctrl && shift && IsKeyDown(Keys.P) && !ModalOpen;
        if (shotNow && !_prevShot) _screenshotPending = true;
        _prevShot = shotNow;

        bool jNow = IsKeyDown(Keys.J) && !ctrl;
        if (jNow && !_prevJ && TextureMode) { Dither = !Dither; Notify?.Invoke($"Dither: {(Dither ? "ON" : "off")}"); }
        _prevJ = jNow;

        bool xNow = IsKeyDown(Keys.X) && !ctrl;
        if (xNow && !_prevX && TextureMode && !ModalOpen) RemoveLayerAtCursor();
        _prevX = xNow;

        bool gNow = IsKeyDown(Keys.G) && !ctrl;
        if (gNow && !_prevG && _terrain != null)
        {
            _terrain.DebugMode = (_terrain.DebugMode + 1) % DebugModeNames.Length;
            DebugModeChanged?.Invoke(DebugModeNames[_terrain.DebugMode]);
        }
        _prevG = gNow;

        bool tNow = IsKeyDown(Keys.T) && !ctrl;
        if (tNow && !_prevT && _doodads != null) _doodads.Enabled = !_doodads.Enabled;
        _prevT = tNow;

        bool yNow = IsKeyDown(Keys.Y) && !ctrl;
        if (yNow && !_prevY && _wmos != null) _wmos.Enabled = !_wmos.Enabled;
        _prevY = yNow;

        bool bNow = IsKeyDown(Keys.B) && !ctrl;
        if (bNow && !_prevB) ToggleFlightPaths();
        _prevB = bNow;

        bool shiftDown = (GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
        bool zNow = IsKeyDown(Keys.Z) && ctrl && !shiftDown;
        if (zNow && !_prevZ) { if (FlightMode) FlightUndo(); else Undo(); }
        _prevZ = zNow;

        bool redoNow = ctrl && ((IsKeyDown(Keys.Z) && shiftDown) || IsKeyDown(Keys.Y));
        if (redoNow && !_prevRedo) { if (FlightMode) FlightRedo(); else Redo(); }
        _prevRedo = redoNow;

        bool pNow = IsKeyDown(Keys.P);
        if (pNow && !_prevP && TextureMode) SampleTextureAtCursor();
        _prevP = pNow;

        bool oNow = IsKeyDown(Keys.O) && !ctrl;
        if (oNow && !_prevO && DoodadMode) { DoodadCollision = !DoodadCollision; DoodadArmed?.Invoke(_placeDoodad ?? ""); }
        _prevO = oNow;

        bool rNow = IsKeyDown(Keys.R) && !ctrl;
        if (rNow && !_prevR && DoodadMode)
        {
            RandomRotation = !RandomRotation;
            if (RandomRotation) RollRandomYaw();
            RandomRotationChanged?.Invoke(RandomRotation);
            Notify?.Invoke(RandomRotation ? "Random rotation ON" : "Random rotation off");
            DoodadArmed?.Invoke(_placeDoodad ?? "");
        }
        _prevR = rNow;

        bool lNow = IsKeyDown(Keys.L) && !ctrl;
        if (lNow && !_prevL && DoodadMode)
        {
            AlignToNormal = !AlignToNormal;
            AlignToNormalChanged?.Invoke(AlignToNormal);
            Notify?.Invoke(AlignToNormal ? "Align to ground normal: ON" : "Align to ground normal: off");
            DoodadArmed?.Invoke(_placeDoodad ?? "");
        }
        _prevL = lNow;

        bool nNow = IsKeyDown(Keys.N) && !ctrl;
        bool nEdge = nNow && !_prevN && !ModalOpen;
        bool openPalette = nEdge && (DoodadMode || WmoMode || TextureMode);
        _prevN = nNow;
        if (openPalette)
        {
            if (WmoMode) OpenWmoPalette?.Invoke();
            else if (DoodadMode) OpenDoodadPalette?.Invoke();
            else OpenTexturePicker?.Invoke();
        }
        else if (nEdge && FlightMode) OpenFlightPathWizard?.Invoke();
        else if (nEdge && _areaMode) OpenNewZoneWizard?.Invoke();

        bool escNow = IsKeyDown(Keys.Escape);
        if (escNow && !_prevEsc)
        {
            if (_hmPlacing) CancelHeightmapPlacement();
            else if (_blendMode) { _blendMode = false; _blendSel.Clear(); _compatVerts = 0; Notify?.Invoke("Blend cancelled."); }
            else if (_areaMode) { _areaMode = false; Notify?.Invoke("Area-id paint off."); ModeChanged?.Invoke(ModeLabel()); }
            else if (GenerateMode) { GenerateMode = false; Notify?.Invoke("Ground generator off."); }
            else if (RoadMode) { RoadMode = false; Notify?.Invoke("Road off."); }
            else if (AutoPaintMode) { AutoPaintMode = false; Notify?.Invoke("Slope auto-paint off."); }
            else if (RiverMode) { RiverMode = false; _riverNodes.Clear(); _riverVertCount = 0; Notify?.Invoke("River carve off."); }
            else if (LiquidMode && _liqArmed != 0) { _liqArmed = 0; Notify?.Invoke("Liquid placement disarmed."); ModeChanged?.Invoke(ModeLabel()); }
            else if (LiquidMode && _liqSel.Count > 0) { _liqSel.Clear(); _liqScrollStroke = null; Notify?.Invoke("Liquid deselected."); ModeChanged?.Invoke(ModeLabel()); }
            else if (FlightMode)
            {
                if (_flightDrawing) FinishFlightDraw();
                else if (_selectedNode >= 0) { _selectedNode = -1; RefreshFlightSelection(); }
                else if (_selectedPath != null) ClearFlightSelection();
                else SetPrimaryMode(EditorMode.None);
            }
            else if (NpcMode)
            {
                if (_patrolSel >= 0) { _patrolSel = -1; RebuildPatrolOverlay(); }
                else if (_npcSelected >= 0)
                {
                    _npcSelected = -1;
                    if (_doodads != null) _doodads.SelectedNpc = -1;
                    ClearPatrol();
                }
                else SetPrimaryMode(EditorMode.None);
            }
            else if (HerbMode)
            {
                if (_herbSelected >= 0)
                {
                    _herbSelected = -1;
                    if (_doodads != null) _doodads.SelectedNpc = -1;
                }
                else SetPrimaryMode(EditorMode.None);
            }
            else if (!string.IsNullOrEmpty(_placeDoodad) || !string.IsNullOrEmpty(_placeWmo))
            {
                _placeDoodad = null;
                _placeWmo = null;
                PaintTextureChanged?.Invoke(PaintTexture);
            }
        }
        _prevEsc = escNow;

        if (LiquidMode)
        {
            bool delNow = IsKeyDown(Keys.Delete);
            if (delNow && !_prevLiqDel && _liqSel.Count > 0) RemoveSelectedLiquid();
            _prevLiqDel = delNow;
        }
        else _prevLiqDel = false;

        if (RiverMode)
        {
            bool enterNow = IsKeyDown(Keys.Return);
            if (enterNow && !_prevRvEnter) CarveRiver();
            _prevRvEnter = enterNow;
            bool backNow = IsKeyDown(Keys.Back);
            if (backNow && !_prevRvBack && _riverNodes.Count > 0)
            { _riverNodes.RemoveAt(_riverNodes.Count - 1); RebuildRiverPreview(); }
            _prevRvBack = backNow;
        }
        else { _prevRvEnter = false; _prevRvBack = false; }

        if (FlightMode)
        {
            bool delNow = IsKeyDown(Keys.Delete) || IsKeyDown(Keys.Back);
            if (delNow && !_prevDelete) DeleteFlightSelection();
            _prevDelete = delNow;

            bool entNow = IsKeyDown(Keys.Return);
            if (_flightDrawing && entNow && !_prevFpEnter) FinishFlightDraw();
            _prevFpEnter = entNow;

            bool hkNow = IsKeyDown(Keys.PageUp) || IsKeyDown(Keys.PageDown);
            if (_selectedNode >= 0)
            {
                if (hkNow && !_prevWpHeight) PushFlightUndo();
                float wpLift = 80f * dt;
                if (IsKeyDown(Keys.PageUp))   AdjustNodeHeight(wpLift);
                if (IsKeyDown(Keys.PageDown)) AdjustNodeHeight(-wpLift);
            }
            _prevWpHeight = hkNow;
        }
        else { _prevDelete = false; _prevWpHeight = false; _prevFpEnter = false; }

        if (!string.IsNullOrEmpty(_placeDoodad) || !string.IsNullOrEmpty(_placeWmo))
        {
            float lift = 5f * dt;
            if (IsKeyDown(Keys.PageUp))   _placeLift += lift;
            if (IsKeyDown(Keys.PageDown)) _placeLift -= lift;
            if (IsKeyDown(Keys.D0)) { _placeLift = 0f; }
        }
    }

    private static bool IsKeyDown(Keys key) =>
        (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private bool IsAppActive()
    {
        if (!IsHandleCreated) return false;
        var hwnd = (System.Windows.PresentationSource.FromVisual(this)
            as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return false;
        var root = GetAncestor(hwnd, 2);
        return root != IntPtr.Zero && root == GetForegroundWindow();
    }

    private void HandleMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && MinimapClick(e.X, e.Y)) return;
        if (e.Button == MouseButtons.Right)
        {
            _rmb = true;
            _rmbMoved = false;
            _lastMx = e.X;
            _lastMy = e.Y;
            CaptureMouse();
        }
        else if (e.Button == MouseButtons.Left)
        {
            if (_hmPlacing) { HeightmapClick(e.X, e.Y); return; }

            _lmb = true;
            CaptureMouse();
            if (FlightMode) FlightModeClick(e.X, e.Y);
            else if (NpcMode) NpcModeClick(e.X, e.Y);
            else if (HerbMode) HerbModeClick(e.X, e.Y);
            else if (_blendMode) BlendSelectAt(e.X, e.Y);
            else if (_areaMode)
            {
                bool zShift = (GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
                if (zShift && TryGetChunkGridAt(e.X, e.Y, out var zc))
                { _zoneRectAnchor = zc; _zoneRectCur = zc; }
                else AreaPaintAt(e.X, e.Y);
            }
            else if (GenerateMode) GenerateAt(e.X, e.Y);
            else if (RiverMode) RiverClick(e.X, e.Y);
            else if (LiquidMode)
            {
                if (_liqArmed == 0 && _liqSel.Count > 0 && LiquidGizmoHit(e.X, e.Y))
                { _liqGizmoDrag = true; _liqGizmoLastY = e.Y; }
                else if (_liqArmed != 0) PlaceLiquidAt(e.X, e.Y);
                else SelectLiquidAt(e.X, e.Y);
            }
            else if (WmoMode && !string.IsNullOrEmpty(_placeWmo)) PlaceWmoAt(e.X, e.Y);
            else if (DoodadMode && !string.IsNullOrEmpty(_placeDoodad)) PlaceDoodadAt(e.X, e.Y);
            else if (DoodadMode || WmoMode)
            {
                bool shift = (GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
                if (!shift && _sel != null && GizmoHitTest(e.X, e.Y, out int axis))
                {
                    _gizmoAxis = axis; _gizLastX = e.X; _gizLastY = e.Y;
                    CaptureGizmoBefore();
                }
                else SelectAt(e.X, e.Y, shift);
            }
            else if (EditMode || TextureMode) { _stroke = new Stroke(); _flattenTarget = float.NaN; _followClearD.Clear(); _followClearW.Clear(); _noiseSeed = unchecked((uint)_rng.Next()); }
        }
        else if (e.Button == MouseButtons.Middle && _blendMode)
        {
            OpenBlendSettings?.Invoke();
        }
        else if (e.Button == MouseButtons.Middle && FlightMode && _flightDrawing)
        {
            AddWaypointAtCamera(stop: (Control.ModifierKeys & Keys.Shift) != 0);
        }
        else if (e.Button == MouseButtons.Middle && FlightMode && _selectedPath != null && TryPickWaypoint(e.X, e.Y, out int wpEdit))
        {
            _selectedNode = wpEdit; RefreshFlightSelection();
            ShowWaypointEditor(e.X, e.Y);
        }
        else if (e.Button == MouseButtons.Middle && _areaMode)
        {
            OpenAreaPicker?.Invoke();
        }
        else if (e.Button == MouseButtons.Middle && EditMode && NoiseMode)
        {
            ShowNoiseTuner(e.X, e.Y);
        }
        else if (e.Button == MouseButtons.Middle && EditMode)
        {
            ShowStrengthTuner(e.X, e.Y);
        }
        else if (e.Button == MouseButtons.Middle && LiquidMode)
        {
            OpenLiquidPicker?.Invoke();
        }
        else if (e.Button == MouseButtons.Middle && TextureMode && !string.IsNullOrEmpty(PaintTexture))
        {
            ShowPaintTuner(e.X, e.Y);
        }
    }

    private void ShowNoiseTuner(int mx, int my) =>
        ShowSliderPopup(mx, my, "Noise grain", " yd", 2, 60, (int)MathF.Round(NoiseGrain),
            v => NoiseGrain = v, "◄ fine   ·   rolling ►");

    private void ShowPaintTuner(int mx, int my) =>
        ShowSliderPopup(mx, my, "Paint strength", "", 1, 30, (int)MathF.Round(PaintStrength),
            v => PaintStrength = v, "◄ light   ·   heavy ►");

    private void ShowStrengthTuner(int mx, int my)
    {
        var menu = new ContextMenuStrip();
        var label = new ToolStripLabel($"Brush strength: {BrushStrength:F0}")
        { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(label);
        menu.Items.Add(new ToolStripSeparator());

        var track = new System.Windows.Forms.TrackBar
        {
            Minimum = 1, Maximum = 200, TickFrequency = 25,
            AutoSize = false, Width = 220, Height = 40,
            Value = Math.Clamp((int)MathF.Round(BrushStrength), 1, 200),
        };
        track.ValueChanged += (_, _) =>
        {
            BrushStrength = track.Value; label.Text = $"Brush strength: {track.Value}";
            BrushRadiusChanged?.Invoke(BrushRadius);
        };
        menu.Items.Add(new ToolStripControlHost(track) { AutoSize = false, Width = 230, Height = 44 });
        menu.Items.Add(new ToolStripLabel("◄ gentle   ·   strong ►") { ForeColor = System.Drawing.Color.Gray });

        menu.Items.Add(new ToolStripSeparator());
        foreach (var shape in Enum.GetValues<BrushShape>())
        {
            var item = new ToolStripMenuItem($"Falloff: {shape}") { Checked = BrushFalloff == shape };
            var sh = shape;
            item.Click += (_, _) => BrushFalloff = sh;
            menu.Items.Add(item);
        }

        menu.Show(ScreenPoint(mx, my));
    }

    private void ShowSliderPopup(int mx, int my, string title, string unit, int min, int max, int current,
                                 Action<int> apply, string hint)
    {
        var menu = new ContextMenuStrip();
        var label = new ToolStripLabel($"{title}: {current}{unit}")
        { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(label);
        menu.Items.Add(new ToolStripSeparator());

        var track = new System.Windows.Forms.TrackBar
        {
            Minimum = min, Maximum = max, TickFrequency = Math.Max(1, (max - min) / 8),
            AutoSize = false, Width = 220, Height = 40, Value = Math.Clamp(current, min, max),
        };
        track.ValueChanged += (_, _) => { apply(track.Value); label.Text = $"{title}: {track.Value}{unit}"; };
        menu.Items.Add(new ToolStripControlHost(track) { AutoSize = false, Width = 230, Height = 44 });
        menu.Items.Add(new ToolStripLabel(hint) { ForeColor = System.Drawing.Color.Gray });

        menu.Show(ScreenPoint(mx, my));
    }

    private void HandleMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _rmb = false;
            if (!_rmbMoved && WmoMode)
                ShowWmoContextMenu(e.X, e.Y);
            else if (!_rmbMoved && DoodadMode)
                ShowDoodadContextMenu(e.X, e.Y);
        }
        else if (e.Button == MouseButtons.Left)
        {
            _lmb = false;
            _draggingNode = false;
            _liqGizmoDrag = false;
            if (_zoneRectAnchor is { } za) { ZoneRectPaint(za, _zoneRectCur); _zoneRectAnchor = null; }
            if (_gizmoAxis >= 0) { CommitGizmo(); _gizmoAxis = -1; }
            else CommitStroke();
        }
        if (!_rmb && !_lmb) ReleaseMouseCapture();
    }

    private void HandleMouseMove(MouseEventArgs e)
    {
        _mouseX = e.X;
        _mouseY = e.Y;
        if (_lmb && _gizmoAxis >= 0) { ApplyGizmoDrag(e.X, e.Y); return; }
        if (_lmb && _liqGizmoDrag) { DragLiquidGizmo(e.Y); return; }
        if (_lmb && _draggingNode) { MoveSelectedNode(e.X, e.Y); return; }
        if (_lmb && _zoneRectAnchor != null)
        {
            if (TryGetChunkGridAt(e.X, e.Y, out var zc)) _zoneRectCur = zc;
            return;
        }
        if (!_rmb) return;
        if (Math.Abs(e.X - _lastMx) + Math.Abs(e.Y - _lastMy) > 2) _rmbMoved = true;
        _camera.Rotate(e.X - _lastMx, e.Y - _lastMy);
        _lastMx = e.X;
        _lastMy = e.Y;
    }

    private void HandleMouseWheel(MouseEventArgs e)
    {
        bool ctrl = (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
        bool brushMode = (EditMode || TextureMode || LiquidMode) && !DoodadMode && !WmoMode;

        if (TextureMode && shift)
        {
            PaintLayer = Math.Clamp(PaintLayer + (e.Delta > 0 ? 1 : -1), 0, MapChunkMaxLayers - 1);
        }
        else if (brushMode && ctrl)
        {
            BrushRadius = Math.Clamp(BrushRadius + e.Delta * 0.006f, 1f, 200f);
            BrushRadiusChanged?.Invoke(BrushRadius);
        }
        else if (LiquidMode && (_liqArmed != 0 || _liqSel.Count > 0))
        {
            float step = (shift ? 2f : 0.25f) * (e.Delta > 0 ? 1 : -1);
            if (_liqArmed != 0)
            {
                _liqPlaceLift += step;
                Notify?.Invoke($"Liquid surface: terrain + {_liqPlaceLift:F2} yd");
            }
            else AdjustSelectedLiquid(step);
        }
        else
        {
            _camera.MoveSpeed = Math.Clamp(_camera.MoveSpeed * MathF.Pow(1.05f, e.Delta / 120f), 1f, 5000f);
            CameraSpeedChanged?.Invoke(_camera.MoveSpeed);
        }
    }

    private MouseEventArgs Adapt(System.Windows.Input.MouseEventArgs e, MouseButtons button, int delta = 0)
    {
        var p = e.GetPosition(this);
        return new MouseEventArgs(button, 1,
            (int)Math.Round(p.X * _dpiX), (int)Math.Round(p.Y * _dpiY), delta);
    }

    protected override void OnMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnMouseUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        e.Handled = true;
    }

    protected override void OnMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleMouseWheel(Adapt(e, MouseButtons.None, e.Delta));
        e.Handled = true;
    }

    private System.Drawing.Point ScreenPoint(int px, int py)
    {
        var p = PointToScreen(new System.Windows.Point(px / _dpiX, py / _dpiY));
        return new System.Drawing.Point((int)p.X, (int)p.Y);
    }
}
