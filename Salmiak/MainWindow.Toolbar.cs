using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;
using Salmiak.Rendering;

namespace Salmiak;

public partial class MainWindow
{

    private readonly Dictionary<GlViewport.Tool, System.Windows.Controls.Button> _toolButtons = new();
    private static readonly System.Windows.Media.Brush ToolIdle =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x3a));
    private static readonly System.Windows.Media.Brush ToolActive =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x6c, 0xc4));

    private void BuildToolbar()
    {
        var tools = new (GlViewport.Tool Tool, string Label, string Tip)[]
        {
            (GlViewport.Tool.Navigate, "Nav",     "Navigate - no editing"),
            (GlViewport.Tool.Sculpt,   "Raise",   "Sculpt raise/lower  (Ctrl+E)\nLMB raise · Ctrl+LMB lower"),
            (GlViewport.Tool.Flatten,  "Flatten", "Flatten to first-clicked height  (Ctrl+F)"),
            (GlViewport.Tool.Smooth,   "Smooth",  "Smooth / blur  (Ctrl+B)"),
            (GlViewport.Tool.Noise,    "Noise",   "Noise / roughen  (Ctrl+K)"),
            (GlViewport.Tool.Delete,   "Holes",   "Delete terrain / punch holes  (Ctrl+H)"),
            (GlViewport.Tool.Shadow,   "Shadow",  "Baked-shadow brush - paint, Ctrl+LMB erase  (Ctrl+M)"),
            (GlViewport.Tool.Liquid,   "Liquids", "Liquids - select / place / re-level  (Ctrl+U)"),
            (GlViewport.Tool.Texture,  "Texture", "Texture paint  (Ctrl+T)"),
            (GlViewport.Tool.Doodad,   "Doodad",  "Place / edit doodads  (Ctrl+D)"),
            (GlViewport.Tool.Wmo,      "WMO",     "Place / edit WMOs  (Ctrl+W)"),
            (GlViewport.Tool.Flight,   "Taxi",    "Taxi mode - taxi paths, zeppelins, boats  (B)"),
            (GlViewport.Tool.Zone,     "Zones",   "Zone mode - inspect / paint chunk zones, create new zones  (Ctrl+A)"),
            (GlViewport.Tool.Npc,      "NPCs",    "Show creature spawns from the world database (view only)"),
            (GlViewport.Tool.Herb,     "Herbs",   "Show herb/mining-vein spawns from the world database (view only)"),
        };
        foreach (var (tool, label, tip) in tools)
        {
            if (tool == GlViewport.Tool.Texture)
                ToolbarPanel.Children.Add(new System.Windows.Controls.Border
                { Width = 1, Background = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(5, 2, 5, 2) });
            var b = new System.Windows.Controls.Button
            {
                Content = label, Tag = tool, ToolTip = tip,
                Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(9, 3, 9, 3),
                Background = ToolIdle, Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
            };
            b.Click += (_, _) => { _viewport!.ActivateTool(tool); _viewport.Focus(); RefreshToolbar(); };
            _toolButtons[tool] = b;
            ToolbarPanel.Children.Add(b);
        }
        HelpText.Text = HelpSheet;
        RefreshToolbar();
    }

    private void RefreshToolbar()
    {
        if (_viewport == null || _toolButtons.Count == 0) return;
        UpdateDoodadDrawer();
        var cur = _viewport.CurrentTool;
        foreach (var (tool, btn) in _toolButtons)
            btn.Background = tool == cur ? ToolActive : ToolIdle;

        string name = cur switch
        {
            GlViewport.Tool.Navigate => "Navigate",     GlViewport.Tool.Sculpt => "Sculpt (raise/lower)",
            GlViewport.Tool.Flatten => "Flatten",       GlViewport.Tool.Smooth => "Smooth",
            GlViewport.Tool.Noise => "Noise",           GlViewport.Tool.Delete => "Delete / holes",
            GlViewport.Tool.Shadow => "Shadow brush",   GlViewport.Tool.Liquid => "Liquids",
            GlViewport.Tool.Texture => "Texture paint",
            GlViewport.Tool.Doodad => "Doodads",        GlViewport.Tool.Wmo => "WMOs",
            GlViewport.Tool.Flight => "Taxi mode",      GlViewport.Tool.Npc => "NPC view",
            GlViewport.Tool.Zone => "Zone mode",   GlViewport.Tool.Herb => "Herb/Vein view",
            _ => cur.ToString(),
        };
        bool brushTool = cur is GlViewport.Tool.Sculpt or GlViewport.Tool.Flatten or GlViewport.Tool.Smooth
            or GlViewport.Tool.Noise or GlViewport.Tool.Delete or GlViewport.Tool.Shadow
            or GlViewport.Tool.Liquid or GlViewport.Tool.Texture;
        ToolHud.Text = brushTool
            ? $"{name}   ·   brush {_viewport.BrushRadius:F0}   str {_viewport.BrushStrength:F0}   {_viewport.BrushFalloff}"
            : name;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time;
        public int X, Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMsg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    private static void ArmHotkeySwallow(System.Windows.Controls.TextBox box, char key)
    {
        while (PeekMessage(out _, IntPtr.Zero, 0x0100, 0x0103, 1)) { }

        char upper = char.ToUpperInvariant(key);
        System.Windows.Input.TextCompositionEventHandler? h = null;
        h = (_, e) =>
        {
            bool held = (GetAsyncKeyState(upper) & 0x8000) != 0;
            if (held && e.Text.Length == 1 && char.ToUpperInvariant(e.Text[0]) == upper)
            {
                e.Handled = true;
                return;
            }
            box.PreviewTextInput -= h;
        };
        box.PreviewTextInput += h;
    }

    private void ToggleHelp() => HelpPopup.IsOpen = !HelpPopup.IsOpen;

    private void HelpOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        HelpPopup.IsOpen = false;

    private const string HelpSheet =
@"MODES
  Ctrl+E  Sculpt terrain         Ctrl+T  Texture paint
  Ctrl+D  Doodads                Ctrl+W  WMOs
  B       Taxi mode              Ctrl+A  Area-id paint
  Esc     Deselect / exit the current mode

SCULPT  (Ctrl+E)
  LMB raise   ·   Ctrl+LMB lower   ·   Ctrl+scroll  brush size
  middle-click  brush strength + falloff
  Ctrl+F Flatten   Ctrl+B Smooth   Ctrl+K Noise
  Ctrl+H Delete/holes (Ctrl+LMB fills back)
  Ctrl+M Shadow brush (paint baked shadow, Ctrl+LMB erase)
  Ctrl+J River/valley carve (click points, Enter carves)
  Ctrl+Q  doodads & WMOs follow the terrain

LIQUIDS  (Ctrl+U)
  click a liquid = select its whole body   ·   scroll = surface height (Shift coarse)
  Ctrl+N pick a liquid to place (preview follows cursor, click fills)   Del remove   Esc deselect

TEXTURE PAINT  (Ctrl+T)
  LMB paint (the texture's layer is found per chunk)   ·   Ctrl+LMB erase that texture
  X strip the texture's layer from the hovered chunk
  Shift+scroll base/blend   N pick texture   P pipette   I isolate layer   J dither
  Ctrl+G generate   Ctrl+R road   Ctrl+P slope auto-paint   Ctrl+L blend seam   Ctrl+C / Ctrl+V copy / paste

DOODADS (Ctrl+D)  /  WMOs (Ctrl+W)
  click select   ·   drag centre = move, rings = rotate, (doodad) scale
  N palette   ·   Del delete   ·   RMB menu/copy   ·   armed: LMB place, PgUp/PgDn lift, O collide
  R random yaw   L align to ground

FLIGHT PATHS  (B)
  click a path   ·   drag waypoints   ·   N new route (name/cost/mount)   ·   middle-click edit a node
  drawing: MMB = waypoint at camera   ·   Enter = finish

CAMERA   WASD move · E/Space up · Q down · RMB look · scroll = speed
VIEW     F wireframe · G debug view · T doodads on/off · Y WMOs on/off · M minimap · Ctrl+Shift+P screenshot to clipboard
EDIT     Ctrl+Z undo · Ctrl+Y (or Ctrl+Shift+Z) redo · Ctrl+S save · Ctrl+O load
HELP     F1  toggle this sheet";
}
