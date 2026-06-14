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
    public bool HerbMode { get; set; }

    public event Action? HerbModeEntered;

    private Salmiak.Core.Formats.GameObjectModels? _goModels;
    private readonly List<HerbSpawn> _herbShown = new();
    private int _herbSelected = -1;

    public readonly record struct HerbSpawn(uint Guid, uint Entry, uint DisplayId, string Name,
                                            uint LockId, float X, float Y, float Z, float O, float Scale);

    public void ShowHerbs(List<HerbSpawn> spawns)
    {
        if (_mpq == null || _doodads == null) return;
        try { _goModels ??= Salmiak.Core.Formats.GameObjectModels.Load(p => _mpq.OpenFile(p)); }
        catch (Exception ex) { Notify?.Invoke($"Herb view: {ex.Message}"); return; }
        _herbShown.Clear();
        _herbSelected = -1;

        var axisFix = new Matrix4(
            1, 0,  0, 0,
            0, 0, -1, 0,
            0, 1,  0, 0,
            0, 0,  0, 1);

        var defs = new List<(string, DoodadRenderer.NpcLook?, Matrix4)>(spawns.Count);
        int herbs = 0, veins = 0;
        foreach (var s in spawns)
        {
            if (!(_goModels.LockInfo(s.LockId, out int lockType, out _))) continue;
            if (lockType == Salmiak.Core.Formats.GameObjectModels.LockHerbalism) herbs++;
            else if (lockType == Salmiak.Core.Formats.GameObjectModels.LockMining) veins++;
            else continue;

            string? model = _goModels.DisplayModel(s.DisplayId);
            if (model == null || model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)) continue;
            var t = Matrix4.CreateScale(s.Scale > 0f ? s.Scale : 1f)
                  * axisFix
                  * Matrix4.CreateRotationY(s.O + MathF.PI / 2f)
                  * Matrix4.CreateTranslation(-s.Y, s.Z, -s.X);
            defs.Add((model, null, t));
            _herbShown.Add(s);
        }
        _doodads.SetNpcs(defs);
        Notify?.Invoke($"Herb/Vein view - {herbs} herb(s), {veins} vein(s) shown (models stream in).");
        ModeChanged?.Invoke(ModeLabel());
    }

    public void HideHerbs()
    {
        _doodads?.ClearNpcs();
        _herbShown.Clear();
        _herbSelected = -1;
    }

    private void HerbModeClick(int mx, int my)
    {
        if (_doodads == null) return;
        MakeCurrent();
        float aspect = Width > 0 && Height > 0 ? (float)Width / Height : 1f;
        int idx = _doodads.PickNpc(_camera, aspect, mx, my, Width, Height);
        _herbSelected = idx >= 0 && idx < _herbShown.Count ? idx : -1;
        _doodads.SelectedNpc = _herbSelected;
    }

    private string HerbInfo()
    {
        if (_herbSelected < 0 || _herbSelected >= _herbShown.Count || _goModels == null) return "";
        var s = _herbShown[_herbSelected];
        string cat = "node";
        int req = 0;
        if (_goModels.LockInfo(s.LockId, out int lockType, out req))
            cat = lockType == Salmiak.Core.Formats.GameObjectModels.LockHerbalism ? "Herbalism" : "Mining";
        return $"{s.Name}   entry {s.Entry}   guid {s.Guid}   {cat}" + (req > 0 ? $" (requires {req})" : "");
    }
}
