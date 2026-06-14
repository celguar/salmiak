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
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool NpcMode { get; set; }

    public event Action? NpcModeEntered;

    private Salmiak.Core.Formats.CreatureModels? _creatureModels;
    private int _npcCount;
    private readonly List<NpcSpawn> _npcShown = new();
    private int _npcSelected = -1;

    public readonly record struct PatrolPoint(int Point, float X, float Y, float Z, float O,
                                              uint WaitMs, uint ScriptId, string? Comment);
    private readonly List<PatrolPoint> _patrol = new();
    private int _patrolSel = -1;
    private float[] _patrolVerts = Array.Empty<float>();
    private int _patrolVertCount;
    private float[] _patrolSelVerts = Array.Empty<float>();
    private int _patrolSelVertCount;

    public event Action<NpcSpawn>? NpcSelected;

    public readonly record struct NpcSpawn(uint Guid, uint Entry, uint DisplayId, string Name,
                                           int MovementType, float X, float Y, float Z, float O, float Scale);

    public void ShowNpcs(List<NpcSpawn> spawns)
    {
        if (_mpq == null || _doodads == null) return;
        try { _creatureModels ??= Salmiak.Core.Formats.CreatureModels.Load(p => _mpq.OpenFile(p)); }
        catch (Exception ex) { Notify?.Invoke($"NPC view: {ex.Message}"); return; }
        _npcShown.Clear();
        _npcSelected = -1;
        ClearPatrol();

        var axisFix = new Matrix4(
            1, 0,  0, 0,
            0, 0, -1, 0,
            0, 1,  0, 0,
            0, 0,  0, 1);

        var defs = new List<(string, DoodadRenderer.NpcLook?, Matrix4)>(spawns.Count);
        int unresolved = 0;
        foreach (var s in spawns)
        {
            var d = _creatureModels.Resolve(s.DisplayId);
            if (d == null) { unresolved++; continue; }
            var t = Matrix4.CreateScale(d.Scale * (s.Scale > 0f ? s.Scale : 1f))
                  * axisFix
                  * Matrix4.CreateRotationY(s.O + MathF.PI / 2f)
                  * Matrix4.CreateTranslation(-s.Y, s.Z, -s.X);
            defs.Add((d.ModelPath, new DoodadRenderer.NpcLook
            { Skin = d.Skin, Hair = d.Hair, HairGeoset = d.HairGeoset, Character = d.Character }, t));
            _npcShown.Add(s);
        }
        _doodads.SetNpcs(defs);
        _npcCount = defs.Count;
        Notify?.Invoke($"NPC view - {defs.Count} spawn(s) shown" +
                       (unresolved > 0 ? $", {unresolved} with unknown display ids skipped." : "."));
        ModeChanged?.Invoke(ModeLabel());
    }

    public void HideNpcs()
    {
        _doodads?.ClearNpcs();
        _npcCount = 0;
        _npcShown.Clear();
        _npcSelected = -1;
        ClearPatrol();
    }

    public void ShowPatrol(List<PatrolPoint> points)
    {
        _patrol.Clear();
        _patrol.AddRange(points);
        _patrolSel = -1;
        RebuildPatrolOverlay();
        if (_npcSelected >= 0 && _npcSelected < _npcShown.Count)
        {
            var s = _npcShown[_npcSelected];
            Notify?.Invoke(points.Count > 0
                ? $"{s.Name} (guid {s.Guid}) - {points.Count} patrol waypoint(s); click one for details."
                : $"{s.Name} (guid {s.Guid}) - no patrol waypoints ({MovementTypeName(s.MovementType)}).");
        }
    }

    private void ClearPatrol()
    {
        _patrol.Clear();
        _patrolSel = -1;
        _patrolVertCount = 0;
        _patrolSelVertCount = 0;
    }

    private static string MovementTypeName(int t) => t switch
    {
        0 => "idle", 1 => "random movement", 2 => "waypoint movement", _ => $"movement type {t}",
    };

    private void RebuildPatrolOverlay()
    {
        _patrolVertCount = 0;
        _patrolSelVertCount = 0;
        if (_patrol.Count == 0) return;
        var verts = new List<float>();
        for (int i = 1; i < _patrol.Count; i++)
        {
            var a = WorldToGl((_patrol[i - 1].X, _patrol[i - 1].Y, _patrol[i - 1].Z));
            var b = WorldToGl((_patrol[i].X, _patrol[i].Y, _patrol[i].Z));
            verts.Add(a.X); verts.Add(a.Y); verts.Add(a.Z);
            verts.Add(b.X); verts.Add(b.Y); verts.Add(b.Z);
            AppendArrow(verts, a, b, 6f);
        }
        foreach (var p in _patrol)
            AppendCross(verts, WorldToGl((p.X, p.Y, p.Z)), 1.5f);
        _patrolVerts = verts.ToArray();
        _patrolVertCount = _patrolVerts.Length / 3;

        if (_patrolSel >= 0 && _patrolSel < _patrol.Count)
        {
            var sel = new List<float>();
            var p = _patrol[_patrolSel];
            AppendCross(sel, WorldToGl((p.X, p.Y, p.Z)), 4f);
            _patrolSelVerts = sel.ToArray();
            _patrolSelVertCount = _patrolSelVerts.Length / 3;
        }
    }

    private bool TryPickPatrolPoint(int mx, int my, out int index)
    {
        index = -1;
        var click = new Vector2(mx, my);
        float best = 14f;
        for (int i = 0; i < _patrol.Count; i++)
        {
            if (!WorldToScreen(WorldToGl((_patrol[i].X, _patrol[i].Y, _patrol[i].Z)), out var sp)) continue;
            float d = (sp - click).Length;
            if (d < best) { best = d; index = i; }
        }
        return index >= 0;
    }

    private void NpcModeClick(int mx, int my)
    {
        if (_patrol.Count > 0 && TryPickPatrolPoint(mx, my, out int wp))
        {
            _patrolSel = wp;
            RebuildPatrolOverlay();
            return;
        }
        if (_doodads == null) return;
        MakeCurrent();
        float aspect = Width > 0 && Height > 0 ? (float)Width / Height : 1f;
        int idx = _doodads.PickNpc(_camera, aspect, mx, my, Width, Height);
        if (idx >= 0 && idx < _npcShown.Count)
        {
            _npcSelected = idx;
            _doodads.SelectedNpc = idx;
            ClearPatrol();
            NpcSelected?.Invoke(_npcShown[idx]);
        }
        else
        {
            _npcSelected = -1;
            _doodads.SelectedNpc = -1;
            ClearPatrol();
        }
    }
}
