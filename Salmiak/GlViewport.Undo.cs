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
    private sealed class Stroke
    {
        public readonly Dictionary<MapChunk, float[]> Heights = new();
        public readonly Dictionary<MapChunk, ushort> Holes = new();
        public readonly Dictionary<MapChunk, TexState> Textures = new();
        public readonly Dictionary<MapChunk, byte[]?> Shadows = new();
        public readonly List<(AdtFile Adt, DoodadDef Def)> Placed = new();
        public readonly List<(AdtFile Adt, DoodadDef Def)> Removed = new();
        public readonly List<(AdtFile Adt, WmoDef Def)> PlacedWmo = new();
        public readonly List<(AdtFile Adt, WmoDef Def)> RemovedWmo = new();
        public readonly List<(AdtFile Adt, int Index, DoodadDef Before)> DoodadXform = new();
        public readonly List<(AdtFile Adt, int Index, WmoDef Before)> WmoXform = new();
        public readonly Dictionary<(AdtFile Adt, int Index), System.Numerics.Vector3> DoodadPos = new();
        public readonly Dictionary<(AdtFile Adt, int Index), System.Numerics.Vector3> WmoPos = new();
        public readonly Dictionary<MapChunk, LiquidLayer?> Liquid = new();
        public readonly Dictionary<MapChunk, int> Area = new();
        public readonly HashSet<AdtFile> Tiles = new();
    }
    private readonly List<Stroke> _undo = new();
    private readonly List<Stroke> _redo = new();
    private Stroke? _stroke;
    private bool _prevZ;
    private bool _prevRedo;
    private const int MaxUndo = 64;

    private void PushUndo(Stroke rec)
    {
        _undo.Add(rec);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void CommitStroke()
    {
        if (_stroke == null) return;
        if (_stroke.Heights.Count > 0 || _stroke.Holes.Count > 0 || _stroke.Textures.Count > 0 || _stroke.Placed.Count > 0 || _stroke.Liquid.Count > 0 || _stroke.Shadows.Count > 0)
        {
            PushUndo(_stroke);
        }
        _stroke = null;
        _flattenTarget = float.NaN;

        if (_dirtyAlpha.Count > 0)
        {
            MakeCurrent();
            foreach (int id in _dirtyAlpha) TextureCache.RegenMips(id);
            _dirtyAlpha.Clear();
        }
    }
}
