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

    private (AdtFile Adt, int Index, bool IsWmo)? _sel;
    private readonly List<(AdtFile Adt, int Index)> _selSet = new();
    private readonly Dictionary<(AdtFile Adt, int Index), DoodadDef> _gizGroupBefore = new();
    private int _gizmoAxis = -1;
    private int _gizLastX, _gizLastY;
    private DoodadDef _gizBeforeD;
    private float _gizScaleStart = 1f;
    private float _gizDist0 = 1f;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DoodadMode { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? PlaceDoodad
    {
        get => _placeDoodad;
        set { _placeDoodad = value; _placeYaw = 0f; _placePitch = 0f; _placeRoll = 0f; _placeScale = 1f; _placeLift = 0f; }
    }
    private string? _placeDoodad;

    private float _placeYaw, _placePitch, _placeRoll;
    private float _placeScale = 1f;
    private float _placeLift;

    public event Action? OpenDoodadPalette;

    public event Action<string>? DoodadArmed;

    private DoodadDef? _doodadClip;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DoodadCollision { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool RandomRotation { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float RandomRotationRange { get; set; } = 360f;
    public event Action<bool>? RandomRotationChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AlignToNormal { get; set; }
    public event Action<bool>? AlignToNormalChanged;

    private void RollRandomYaw() => _placeYaw = (float)(_rng.NextDouble() * RandomRotationRange);

    public List<string> AvailableDoodads()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_wmoHolder != null)
            foreach (var m in _wmoHolder.DoodadModels)
                if (!string.IsNullOrEmpty(m)) set.Add(m);
        foreach (var kv in _tileCache)
            if (kv.Value != null)
                foreach (var m in kv.Value.DoodadModels)
                    if (!string.IsNullOrEmpty(m)) set.Add(m);
        return set.ToList();
    }

    private void RefreshPlacements()
    {
        if (WmoOnly) ReloadWmoOnly();
        else RefreshStreaming();
    }

    public void ResetPlacementRotation() { _placeYaw = 0f; _placePitch = 0f; _placeRoll = 0f; _placeScale = 1f; _placeLift = 0f; }

    public byte[]? RenderDoodadThumbnail(string path, int size, float yawDeg = 0f)
    {
        if (_doodads == null || !IsHandleCreated) return null;
        MakeCurrent();
        var px = _doodads.RenderThumbnail(path, size, yawDeg);
        GL.Viewport(0, 0, Width, Height);
        return px;
    }

    private DoodadRenderer.DoodadHit? DoodadPickAt(int mx, int my)
    {
        if (_doodads == null) return null;
        MakeCurrent();
        float aspect = Width > 0 && Height > 0 ? (float)Width / Height : 1f;
        return _doodads.Pick(_camera, aspect, mx, my, Width, Height);
    }

    private void CopyExternalDoodad(string path, float scale)
    {
        _placeDoodad = path;
        _placeYaw = 0f; _placePitch = 0f; _placeRoll = 0f; _placeLift = 0f;
        _placeScale = scale > 0f ? scale : 1f;
        DoodadArmed?.Invoke(path);
    }

    private void DeleteDoodad(AdtFile adt, int index)
    {
        if (index < 0 || index >= adt.Doodads.Count) return;
        var def = adt.Doodads[index];
        adt.Doodads.RemoveAt(index);
        MarkEdited(adt);
        var rec = new Stroke();
        rec.Removed.Add((adt, def));
        PushUndo(rec);
        ClearSelection();
        RefreshPlacements();
    }

    private void DeleteSelection()
    {
        if (_selSet.Count == 0) return;
        var rec = new Stroke();
        foreach (var g in _selSet.GroupBy(t => t.Adt))
        {
            var adt = g.Key;
            foreach (int idx in g.Select(t => t.Index).Distinct().OrderByDescending(i => i))
            {
                if (idx < 0 || idx >= adt.Doodads.Count) continue;
                rec.Removed.Add((adt, adt.Doodads[idx]));
                adt.Doodads.RemoveAt(idx);
            }
            MarkEdited(adt);
        }
        PushUndo(rec);
        Notify?.Invoke($"Deleted {rec.Removed.Count} doodad(s).");
        ClearSelection();
        RefreshPlacements();
    }

    private void CopyDoodad(AdtFile adt, int index)
    {
        if (index < 0 || index >= adt.Doodads.Count) return;
        var def = adt.Doodads[index];
        _placeDoodad = def.ModelPath;
        _placePitch = def.Rotation.X;
        _placeYaw   = def.Rotation.Y;
        _placeRoll  = def.Rotation.Z;
        _placeScale = def.Scale;
        DoodadArmed?.Invoke(def.ModelPath);
    }

    private DoodadDef BuildPlacementDef(Vector3 hit)
    {
        const float mapHalf = 32f * AdtFile.TileSize;
        var (pitch, roll) = AlignToNormal ? TiltToNormal(hit.X, hit.Z, _placeYaw) : (_placePitch, _placeRoll);
        return new DoodadDef
        {
            ModelPath = _placeDoodad!,
            Position  = new System.Numerics.Vector3(hit.X + mapHalf, hit.Y + _placeLift, hit.Z + mapHalf),
            Rotation  = new System.Numerics.Vector3(pitch, _placeYaw, roll),
            Scale     = _placeScale,
            UniqueId  = unchecked((uint)Environment.TickCount + (uint)_placeCounter++),
            Flags     = 0,
        };
    }
    private int _placeCounter;

    private (float pitchDeg, float rollDeg) TiltToNormal(float glx, float glz, float yawDeg)
    {
        const float e = 1.0f;
        float hxp = HeightAtGL(glx + e, glz), hxm = HeightAtGL(glx - e, glz);
        float hzp = HeightAtGL(glx, glz + e), hzm = HeightAtGL(glx, glz - e);
        if (float.IsNaN(hxp) || float.IsNaN(hxm) || float.IsNaN(hzp) || float.IsNaN(hzm))
            return (_placePitch, _placeRoll);
        float dhdx = (hxp - hxm) / (2f * e);
        float dhdz = (hzp - hzm) / (2f * e);
        var n = Vector3.Normalize(new Vector3(-dhdx, 1f, -dhdz));
        float a = -MathHelper.DegreesToRadians(yawDeg - 90f);
        float ca = MathF.Cos(a), sa = MathF.Sin(a);
        var nl = new Vector3(n.X * ca + n.Z * sa, n.Y, -n.X * sa + n.Z * ca);
        float pitch = MathF.Atan2(nl.Z, nl.Y);
        float roll  = MathF.Atan2(-nl.X, MathF.Sqrt(nl.Y * nl.Y + nl.Z * nl.Z));
        return (MathHelper.RadiansToDegrees(pitch), MathHelper.RadiansToDegrees(roll));
    }

    private void PlaceDoodadAt(int mx, int my)
    {
        if (WmoOnly) { PlaceDoodadInHolder(mx, my); return; }
        if (_wdt == null || !PickPlacement(mx, my, out var hit)) return;
        const float ts = AdtFile.TileSize;
        int tileX = (int)MathF.Floor(32f + hit.X / ts);
        int tileY = (int)MathF.Floor(32f + hit.Z / ts);
        if (!_tileCache.TryGetValue((tileX, tileY), out var adt) || adt == null) return;

        if (RandomRotation) RollRandomYaw();
        var def = BuildPlacementDef(hit);
        adt.Doodads.Add(def);
        MarkEdited(adt);
        Select(adt, adt.Doodads.Count - 1, false);

        var rec = new Stroke();
        rec.Placed.Add((adt, def));
        PushUndo(rec);

        RefreshStreaming();
    }

    private void PlaceDoodadInHolder(int mx, int my)
    {
        if (_wmoHolder == null || string.IsNullOrEmpty(_placeDoodad)) return;
        if (!PickSurface(mx, my, out var hit)) return;
        if (RandomRotation) RollRandomYaw();
        var def = BuildPlacementDef(hit);
        _wmoHolder.Doodads.Add(def);
        Select(_wmoHolder, _wmoHolder.Doodads.Count - 1, false);
        var rec = new Stroke();
        rec.Placed.Add((_wmoHolder, def));
        PushUndo(rec);
        ReloadWmoOnly();
    }

    private bool TryGetSelTransform(out Vector3 center, out float radius, out bool isWmo)
    {
        center = default; radius = 0f; isWmo = false;
        if (_sel == null) return false;
        var s = _sel.Value; isWmo = s.IsWmo;
        const float mapHalf = 32f * AdtFile.TileSize;
        if (s.IsWmo)
        {
            if (s.Index < 0 || s.Index >= s.Adt.Wmos.Count) return false;
            var d = s.Adt.Wmos[s.Index];
            center = new Vector3(d.Position.X - mapHalf, d.Position.Y, d.Position.Z - mapHalf);
            float rad = 30f;
            if (_wmos != null && _wmos.TryGetRadius(d.ModelPath, out var rr)) rad = rr;
            radius = MathF.Max(rad * 0.6f, 5f);
        }
        else
        {
            if (s.Index < 0 || s.Index >= s.Adt.Doodads.Count) return false;
            var d = s.Adt.Doodads[s.Index];
            center = new Vector3(d.Position.X - mapHalf, d.Position.Y, d.Position.Z - mapHalf);
            float rad = 4f;
            if (_doodads != null && _doodads.TryGetModelBounds(d.ModelPath, out _, out var mr)) rad = mr * d.Scale;
            radius = MathF.Max(rad, 2f);
        }
        return true;
    }

    private bool GizmoHitTest(int mx, int my, out int axis)
    {
        axis = -1;
        if (!TryGetSelTransform(out var center, out float radius, out bool isWmo)) return false;
        var (origin, dir) = RayFromMouse(mx, my);

        if (!isWmo)
        {
            var hp = center + new Vector3(1f, 1f, 1f) * (radius * GizmoRenderer.ScalePos);
            if (WorldToScreen(hp, out var hsp) && (hsp - new Vector2(mx, my)).Length < 14f) { axis = 3; return true; }
        }

        for (int a = 0; a < 3; a++)
        {
            Vector3 dirA = a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;
            var tip = center + dirA * (radius * GizmoRenderer.TranslateLen);
            if (WorldToScreen(tip, out var tsp) && (tsp - new Vector2(mx, my)).Length < 14f) { axis = 4 + a; return true; }
        }

        if (WorldToScreen(center, out var csp) && (csp - new Vector2(mx, my)).Length < 12f) { axis = 7; return true; }

        float best = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            Vector3 n = a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;
            float denom = Vector3.Dot(dir, n);
            if (MathF.Abs(denom) < 1e-4f) continue;
            float t = Vector3.Dot(center - origin, n) / denom;
            if (t <= 0f) continue;
            var p = origin + dir * t;
            float err = MathF.Abs((p - center).Length - radius);
            if (err < radius * 0.18f && err < best) { best = err; axis = a; }
        }
        return axis >= 0;
    }

    private void Select(AdtFile adt, int index, bool isWmo)
    {
        _sel = (adt, index, isWmo);
        _selSet.Clear();
        if (!isWmo) _selSet.Add((adt, index));
        SyncSelectionHighlight();
    }

    private void ClearSelection() { _sel = null; _selSet.Clear(); SyncSelectionHighlight(); }

    private void SyncSelectionHighlight() => _doodads?.SetSelection(_selSet);

    private void SelectAt(int mx, int my, bool additive)
    {
        if (WmoMode)
        {
            var hit = WmoPickAt(mx, my);
            if (hit != null) Select(hit.Value.Adt, hit.Value.Index, true);
            else if (!additive) ClearSelection();
            return;
        }
        if (!DoodadMode) return;

        var dhit = DoodadPickAt(mx, my);
        if (dhit is { Adt: not null } h)
        {
            var key = (h.Adt!, h.Index);
            if (additive)
            {
                int i = _selSet.IndexOf(key);
                if (i >= 0)
                {
                    _selSet.RemoveAt(i);
                    _sel = _selSet.Count > 0 ? (_selSet[^1].Adt, _selSet[^1].Index, false) : null;
                }
                else { _selSet.Add(key); _sel = (h.Adt!, h.Index, false); }
                SyncSelectionHighlight();
            }
            else Select(h.Adt!, h.Index, false);
        }
        else if (dhit is { ExternalPath: not null } e)
        {
            ClearSelection();
            CopyExternalDoodad(e.ExternalPath!, e.ExternalScale);
            Notify?.Invoke($"Interior doodad copied - click to place a movable copy: {System.IO.Path.GetFileNameWithoutExtension(e.ExternalPath)}");
        }
        else if (!additive) ClearSelection();
    }

    private void CaptureGizmoBefore()
    {
        if (_sel == null) return;
        var s = _sel.Value;
        if (s.IsWmo) { if (s.Index >= 0 && s.Index < s.Adt.Wmos.Count) _gizBeforeW = s.Adt.Wmos[s.Index]; }
        else { if (s.Index >= 0 && s.Index < s.Adt.Doodads.Count) _gizBeforeD = s.Adt.Doodads[s.Index]; }

        _gizGroupBefore.Clear();
        if (!s.IsWmo)
            foreach (var (a, i) in _selSet)
                if (i >= 0 && i < a.Doodads.Count) _gizGroupBefore[(a, i)] = a.Doodads[i];

        if (_gizmoAxis == 3 && !s.IsWmo)
        {
            _gizScaleStart = _gizBeforeD.Scale > 0f ? _gizBeforeD.Scale : 1f;
            _gizDist0 = 1f;
            if (TryGetSelTransform(out var c, out _, out _) && WorldToScreen(c, out var cs))
                _gizDist0 = MathF.Max((new Vector2(_gizLastX, _gizLastY) - cs).Length, 4f);
        }
    }

    private void ApplyGizmoDrag(int mx, int my)
    {
        if (_sel == null || _gizmoAxis < 0) return;
        float dxp = mx - _gizLastX, dyp = my - _gizLastY;
        var s = _sel.Value;

        float tmove = 0f; int taxis = _gizmoAxis - 4;
        if (_gizmoAxis >= 4 && TryGetSelTransform(out var center, out _, out _))
        {
            Vector3 axisDir = taxis == 0 ? Vector3.UnitX : taxis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            if (WorldToScreen(center, out var s0) && WorldToScreen(center + axisDir, out var s1))
            {
                var sd = s1 - s0; float len = sd.Length;
                if (len > 1e-3f) tmove = Vector2.Dot(new Vector2(dxp, dyp), sd / len) / len;
            }
        }

        _gizLastX = mx; _gizLastY = my;

        const float mapHalf = 32f * AdtFile.TileSize;
        bool freeMove = false;
        System.Numerics.Vector3 moveTo = default;
        if (_gizmoAxis == 7)
        {
            Vector3 moveHit = default;
            bool ok = WmoOnly ? PickSurface(mx, my, out moveHit) : PickTerrain(mx, my, out moveHit);
            if (ok)
            {
                freeMove = true;
                moveTo = new System.Numerics.Vector3(moveHit.X + mapHalf, moveHit.Y + _placeLift, moveHit.Z + mapHalf);
            }
        }

        if (s.IsWmo)
        {
            if (s.Index < 0 || s.Index >= s.Adt.Wmos.Count) return;
            var d = s.Adt.Wmos[s.Index];
            if (_gizmoAxis == 0) d.Rotation.X += dxp * 0.5f;
            else if (_gizmoAxis == 1) d.Rotation.Y += dxp * 0.5f;
            else if (_gizmoAxis == 2) d.Rotation.Z += dxp * 0.5f;
            else if (_gizmoAxis == 4) d.Position.X += tmove;
            else if (_gizmoAxis == 5) d.Position.Y += tmove;
            else if (_gizmoAxis == 6) d.Position.Z += tmove;
            else if (freeMove) d.Position = moveTo;
            s.Adt.Wmos[s.Index] = d;
            MarkEdited(s.Adt);
            if (_wmos == null || !_wmos.UpdateInstance(s.Adt, s.Index, WmoRenderer.BuildTransform(d))) RefreshStreaming();
        }
        else
        {
            if (s.Index < 0 || s.Index >= s.Adt.Doodads.Count) return;
            var d = s.Adt.Doodads[s.Index];
            if (_gizmoAxis == 0) d.Rotation.X += dxp * 0.5f;
            else if (_gizmoAxis == 1) d.Rotation.Y += dxp * 0.5f;
            else if (_gizmoAxis == 2) d.Rotation.Z += dxp * 0.5f;
            else if (_gizmoAxis == 7 && freeMove) d.Position = moveTo;
            else if (_gizmoAxis == 3)
            {
                if (TryGetSelTransform(out var c, out _, out _) && WorldToScreen(c, out var cs))
                {
                    float cur = MathF.Max((new Vector2(mx, my) - cs).Length, 1f);
                    d.Scale = Math.Clamp(_gizScaleStart * (cur / _gizDist0), 0.05f, 20f);
                }
            }
            else if (_gizmoAxis == 4) d.Position.X += tmove;
            else if (_gizmoAxis == 5) d.Position.Y += tmove;
            else if (_gizmoAxis == 6) d.Position.Z += tmove;
            s.Adt.Doodads[s.Index] = d;
            MarkEdited(s.Adt);
            if (_doodads == null || !_doodads.UpdateInstance(s.Adt, s.Index, DoodadRenderer.BuildTransform(d))) RefreshStreaming();

            if (_selSet.Count > 1)
            {
                var posD = d.Position - _gizBeforeD.Position;
                var rotD = d.Rotation - _gizBeforeD.Rotation;
                float scaleF = _gizBeforeD.Scale != 0f ? d.Scale / _gizBeforeD.Scale : 1f;
                foreach (var (a, i) in _selSet)
                {
                    if (a == s.Adt && i == s.Index) continue;
                    if (i < 0 || i >= a.Doodads.Count || !_gizGroupBefore.TryGetValue((a, i), out var b)) continue;
                    var o = a.Doodads[i];
                    o.Position = b.Position + posD;
                    o.Rotation = b.Rotation + rotD;
                    o.Scale = b.Scale * scaleF;
                    a.Doodads[i] = o;
                    MarkEdited(a);
                    if (_doodads == null || !_doodads.UpdateInstance(a, i, DoodadRenderer.BuildTransform(o))) RefreshStreaming();
                }
            }
        }
    }

    private void CommitGizmo()
    {
        if (_sel == null) return;
        var s = _sel.Value;
        var rec = new Stroke();
        if (s.IsWmo) rec.WmoXform.Add((s.Adt, s.Index, _gizBeforeW));
        else if (_gizGroupBefore.Count > 0)
            foreach (var (key, before) in _gizGroupBefore) rec.DoodadXform.Add((key.Adt, key.Index, before));
        else rec.DoodadXform.Add((s.Adt, s.Index, _gizBeforeD));
        PushUndo(rec);
        if (s.IsWmo) RefreshStreaming();
    }

    private void ShowDoodadContextMenu(int mx, int my)
    {
        var hit = DoodadPickAt(mx, my);
        if (hit == null) return;
        var h = hit.Value;

        if (h.ExternalPath != null)
        {
            string en = System.IO.Path.GetFileNameWithoutExtension(h.ExternalPath);
            var emenu = new ContextMenuStrip();
            emenu.Items.Add(new ToolStripLabel(en + "  (interior)")
            { Font = new System.Drawing.Font(emenu.Font, System.Drawing.FontStyle.Bold) });
            emenu.Items.Add(new ToolStripSeparator());
            emenu.Items.Add("Copy to world", null, (_, _) => CopyExternalDoodad(h.ExternalPath, h.ExternalScale));
            emenu.Show(ScreenPoint(mx, my));
            return;
        }
        if (h.Adt == null) return;
        var (adt, index) = (h.Adt, h.Index);
        string name = System.IO.Path.GetFileNameWithoutExtension(adt.Doodads[index].ModelPath);

        var menu = new ContextMenuStrip();
        var header = new ToolStripLabel(name) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy", null, (_, _) => CopyDoodad(adt, index));
        bool inGroup = _selSet.Count > 1 && _selSet.Contains((adt, index));
        menu.Items.Add(inGroup ? $"Delete {_selSet.Count} selected" : "Delete", null,
            (_, _) => { if (inGroup) DeleteSelection(); else DeleteDoodad(adt, index); });
        menu.Show(ScreenPoint(mx, my));
    }

    private void ClipboardCopy()
    {
        if (DoodadMode) CopySelectedDoodad();
        else if ((EditMode || TextureMode) && !WmoMode) CopyChunkTextures();
    }

    private void ClipboardPaste()
    {
        if (DoodadMode) PasteDoodad();
        else if ((EditMode || TextureMode) && !WmoMode) PasteChunkTextures();
    }

    private void CopySelectedDoodad()
    {
        if (_sel is { IsWmo: false } s && s.Index >= 0 && s.Index < s.Adt.Doodads.Count)
        {
            _doodadClip = s.Adt.Doodads[s.Index];
            DoodadArmed?.Invoke(s.Adt.Doodads[s.Index].ModelPath);
        }
    }

    private void PasteDoodad()
    {
        if (_doodadClip is not { } clip) return;
        if (!PickPlacement(_mouseX, _mouseY, out var hit)) return;
        const float mapHalf = 32f * AdtFile.TileSize;

        AdtFile? adt;
        if (WmoOnly) adt = _wmoHolder;
        else
        {
            if (_wdt == null) return;
            const float ts = AdtFile.TileSize;
            int tileX = (int)MathF.Floor(32f + hit.X / ts), tileY = (int)MathF.Floor(32f + hit.Z / ts);
            _tileCache.TryGetValue((tileX, tileY), out adt);
        }
        if (adt == null) return;

        var def = new DoodadDef
        {
            ModelPath = clip.ModelPath,
            Position = new System.Numerics.Vector3(hit.X + mapHalf, hit.Y + _placeLift, hit.Z + mapHalf),
            Rotation = clip.Rotation,
            Scale = clip.Scale,
            UniqueId = unchecked((uint)Environment.TickCount + (uint)_placeCounter++),
            Flags = clip.Flags,
        };
        adt.Doodads.Add(def);
        MarkEdited(adt);
        Select(adt, adt.Doodads.Count - 1, false);

        var rec = new Stroke();
        rec.Placed.Add((adt, def));
        PushUndo(rec);
        RefreshPlacements();
    }
}
