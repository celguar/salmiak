using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;
using Salmiak.Rendering;

namespace Salmiak;

public partial class MainWindow
{
    //Shadow sidebar: replaces the map panel while the Shadows tool is active, exactly like the
    //asset drawer does for Doodads / WMOs / Textures. The mode radio (brush vs raycast bake)
    //sits on top, then the bake options, then the Bake button with its progress bar below it.
    private System.Windows.Controls.CheckBox? _shadowBrushRow, _shadowRayRow;
    private System.Windows.Controls.Button? _shadowBakeButton;
    private System.Windows.Controls.TextBlock? _shadowProgressText, _shadowHint;
    private System.Windows.Controls.Border? _shadowFill;
    private const double ShadowBarWidth = 168;
    private bool _shadowBusy;

    //Options are mirrored one way only: dragging a slider or ticking a box writes into the
    //viewport, and RefreshShadowOptions() paints the current viewport values back onto the
    //controls, so the sidebar can never disagree with what the next bake will use.
    private readonly List<(System.Windows.Controls.Slider Sl, System.Windows.Controls.TextBlock Head, string Name, int K)> _shSliders = new();
    private readonly List<(System.Windows.Controls.CheckBox Cb, int K)> _shChecks = new();
    private static readonly System.Windows.Media.Brush ShadowAccent =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x6c, 0xc4));
    private static readonly System.Windows.Media.Brush ShadowTrack =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x36, 0x36, 0x3c));
    //A bake that landed is shown in green, but kept away from the pure (0,255,0) the theme uses
    //everywhere else: a full bar of acid green would read as a highlight rather than as a done
    //state, and it fights the blue of every other control on this sidebar.
    private static readonly System.Windows.Media.Brush ShadowDone =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4e, 0x8b, 0x57));

    private void BuildShadowPanel()
    {
        var col = new System.Windows.Controls.StackPanel();

        System.Windows.Controls.TextBlock Section(string text)
        {
            var t = new System.Windows.Controls.TextBlock
            {
                Text = text, Foreground = ShadowAccent, FontSize = 12,
                Margin = new Thickness(0, 8, 0, 2),
            };
            col.Children.Add(t);
            return t;
        }

        System.Windows.Controls.Slider Make(string label, int k, double min, double max, string unit)
        {
            double v = ShGet(k);
            var head = new System.Windows.Controls.TextBlock
            {
                Text = $"{label}: {ShText(k)}", Foreground = System.Windows.Media.Brushes.LightGray,
                FontSize = 11, Margin = new Thickness(0, 6, 0, 2),
            };
            var sl = new System.Windows.Controls.Slider
            {
                Minimum = min, Maximum = max, Value = v,
                Foreground = ShadowAccent, Margin = new Thickness(0, 0, 0, 2),
            };
            sl.ValueChanged += (_, ev) =>
            {
                head.Text = $"{label}: {ev.NewValue:F0}{unit}";
                ShSet(k, ev.NewValue);
            };
            col.Children.Add(head);
            col.Children.Add(sl);
            _shSliders.Add((sl, head, label, k));
            return sl;
        }

        System.Windows.Controls.CheckBox MakeCheck(string label, int k)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = label, Foreground = System.Windows.Media.Brushes.White,
                IsChecked = ShcGet(k), Margin = new Thickness(0, 4, 0, 1),
            };
            cb.Click += (_, _) =>
            {
                ShcSet(k, !ShcGet(k));
                RefreshShadowOptions();
            };
            col.Children.Add(cb);
            _shChecks.Add((cb, k));
            return cb;
        }

        System.Windows.Controls.CheckBox MakeRadio(string label, bool brush)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = label, Foreground = System.Windows.Media.Brushes.White,
                IsChecked = brush == (_viewport?.ShadowMode ?? false), Margin = new Thickness(0, 4, 0, 1),
            };
            cb.Click += (_, _) =>
            {
                if (_viewport == null) return;
                _viewport.SelectShadowMode(brush);
                _viewport.Focus();
            };
            col.Children.Add(cb);
            return cb;
        }

        Section("Mode");
        _shadowBrushRow = MakeRadio("Brush - paint shadows", true);
        _shadowRayRow = MakeRadio("Raycast - bake chunks", false);

        Section("Shadow view");
        //Not a bake option: this is a display helper, so it sits with the mode row rather than with
        //the options that decide what the rays do. The plain Blizzard dimming is often too subtle to
        //tell where a bake starts and stops, and the violet wash makes it obvious without touching
        //the shadow data.
        MakeCheck("Highlight shadows (display only)", 7);

        Section("Bake options");
        MakeCheck("Doodad shadows (M2)", 0);
        MakeCheck("Alpha-test foliage", 1);
        MakeCheck("Skip transparent", 5);
        MakeCheck("Slopes block sun", 6);
        MakeCheck("Soft shadows (slow)", 2);
        MakeCheck("Jitter rays", 3);
        MakeCheck("Keep existing shadows", 4);

        Section("Sun & rays");
        Make("Sun height", 0, -80, 80, "°");
        Make("Sun rotation", 1, 0, 360, "°");
        Make("Max length", 2, 20, 600, " yd");
        Make("Alpha cutoff", 3, 0, 255, "");
        Make("Softness", 4, 1, 8, "x");
        Make("Hit limit", 5, 1, 48, "");
        Make("Jitter", 6, 0, 40, " ‰");

        _shadowBakeButton = new System.Windows.Controls.Button
        {
            Content = "Bake Shadows", Foreground = System.Windows.Media.Brushes.White,
            Background = ShadowAccent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 8, 0, 2),
            ToolTip = "Ray-bake the chunks you selected, or every edited tile when none are selected",
        };
        _shadowBakeButton.Click += (_, _) => BakeShadowNow();
        col.Children.Add(_shadowBakeButton);

        //Progress goes directly under the Bake button, so the thing that fills up sits at the place
        //that started it. The counter line is the one that stays readable while the bake walks the
        //chunks a few at a time between frames.
        _shadowProgressText = new System.Windows.Controls.TextBlock
        { Text = "", Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 10 };
        _shadowFill = new System.Windows.Controls.Border
        {
            Width = 0, Height = 8, Background = ShadowAccent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        var bar = new System.Windows.Controls.Border
        {
            Width = ShadowBarWidth, Height = 8, Background = ShadowTrack,
            CornerRadius = new CornerRadius(3), Child = _shadowFill,
            Margin = new Thickness(0, 4, 0, 6),
        };
        col.Children.Add(_shadowProgressText);
        col.Children.Add(bar);

        _shadowHint = new System.Windows.Controls.TextBlock
        {
            Text = "Chunks: click / Shift+drag, C = reset, Shift+C = whole tile. No chunks picked = every edited tile.",
            Foreground = System.Windows.Media.Brushes.Gray, FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        col.Children.Add(_shadowHint);

        ShadowSide.Child = col;
    }

    //Reads one bake option out of the viewport (index matches the order built above).
    private double ShGet(int k)
    {
        if (_viewport == null) return 0d;
        return (k switch
        {
            0 => _viewport.ShadowSunHeight,
            1 => _viewport.ShadowSunRotation,
            2 => _viewport.ShadowMaxDistance,
            3 => _viewport.ShadowAlphaThreshold,
            4 => _viewport.ShadowHighResSamples,
            5 => _viewport.ShadowHighResThreshold,
            _ => _viewport.ShadowJitterValue * 100d,
        });
    }

    private string ShText(int k)
    {
        if (_viewport == null) return "";
        int v = k switch
        {
            0 => (int)_viewport.ShadowSunHeight,
            1 => (int)_viewport.ShadowSunRotation,
            2 => (int)_viewport.ShadowMaxDistance,
            3 => _viewport.ShadowAlphaThreshold,
            4 => _viewport.ShadowHighResSamples,
            5 => _viewport.ShadowHighResThreshold,
            _ => (int)Math.Round(_viewport.ShadowJitterValue * 100f),
        };
        return k switch
        {
            0 => $"{v}°",  1 => $"{v}°",  2 => $"{v} yd",  3 => $"{v}",
            4 => $"{v}x",  5 => $"{v}",  _ => $"{v} ‰",
        };
    }

    private void ShSet(int k, double v)
    {
        if (_viewport == null) return;
        switch (k)
        {
            case 0: _viewport.ShadowSunHeight = (float)v; break;
            case 1: _viewport.ShadowSunRotation = (float)v; break;
            case 2: _viewport.ShadowMaxDistance = (float)v; break;
            case 3: _viewport.ShadowAlphaThreshold = (int)v; break;
            case 4: _viewport.ShadowHighResSamples = (int)v; break;
            case 5: _viewport.ShadowHighResThreshold = (int)v; break;
            case 6: _viewport.ShadowJitterValue = (float)(v / 100d); break;
        }
    }

    private bool ShcGet(int k)
    {
        if (_viewport == null) return false;
        return (k switch
        {
            0 => _viewport.ShadowDoodads,
            1 => _viewport.ShadowAlphaTest,
            2 => _viewport.ShadowHighRes,
            3 => _viewport.ShadowJitter,
            4 => _viewport.ShadowKeepOld,
            5 => _viewport.ShadowSkipTransparent,
            6 => _viewport.ShadowTerrainBlocks,
            _ => _viewport.ShadowHighlight,
        });
    }

    private void ShcSet(int k, bool v)
    {
        if (_viewport == null) return;
        switch (k)
        {
            case 0: _viewport.ShadowDoodads = v; break;
            case 1: _viewport.ShadowAlphaTest = v; break;
            case 2: _viewport.ShadowHighRes = v; break;
            case 3: _viewport.ShadowJitter = v; break;
            case 4: _viewport.ShadowKeepOld = v; break;
            case 5: _viewport.ShadowSkipTransparent = v; break;
            case 6: _viewport.ShadowTerrainBlocks = v; break;
            case 7: _viewport.ShadowHighlight = v; break;
        }
    }

    //Paints the current viewport settings onto the sidebar controls.
    private void RefreshShadowOptions()
    {
        if (_viewport == null) return;
        for (int i = 0; i < _shSliders.Count; i++)
        {
            var (sl, head, name, k) = _shSliders[i];
            double v = ShGet(k);
            sl.Value = v;
            head.Text = $"{name}: {ShText(k)}";
        }
        for (int i = 0; i < _shChecks.Count; i++)
        {
            var (cb, k) = _shChecks[i];
            cb.IsChecked = ShcGet(k);
        }
        if (_shadowBrushRow != null) _shadowBrushRow.IsChecked = _viewport.ShadowMode && !_viewport.BakeChunksMode;
        if (_shadowRayRow != null) _shadowRayRow.IsChecked = _viewport.BakeChunksMode;
    }

    //Empties the bar and puts it back in the sidebar's blue. Called straight from the click, before
    //the bake is even prepared: PrepareShadowBake only fires the viewport's own reset once the CPU
    //geometry copies are gathered, so without this a short bake would leave the previous bake's
    //green fill on screen for the whole preparation and the user could not tell a new one started.
    private void ResetShadowBar()
    {
        if (_shadowFill != null)
        {
            _shadowFill.Width = 0;
            _shadowFill.Background = ShadowAccent;
        }
        if (_shadowProgressText != null) _shadowProgressText.Text = "Baking shadows...";
    }

    //Swap the sidebar in/out; the asset drawer and the map panel own the same slot.
    private void UpdateShadowPanel()
    {
        if (_viewport == null || _shadowBusy) return;
        _shadowBusy = true;
        try
        {
            if (_viewport.CurrentTool == GlViewport.Tool.Shadow)
            {
                SidePanel.Visibility = Visibility.Collapsed;
                ShadowSide.Visibility = Visibility.Visible;
                RefreshShadowOptions();
            }
            else if (ShadowSide.Visibility == Visibility.Visible)
            {
                ShadowSide.Visibility = Visibility.Collapsed;
                SidePanel.Visibility = Visibility.Visible;
            }
        }
        finally { _shadowBusy = false; }
    }

    //Bakes straight from the sidebar: the picked chunks when any are selected, otherwise the tiles
    //touched since the last export, which keeps the brush submode useful without a scope picker.
    private void BakeShadowNow()
    {
        if (_viewport == null) return;
        if (_viewport.MapName == null) { Say("Bake shadows: open a terrain map first."); return; }
        int chunks = _viewport.BakedChunkCount;
        bool selected = chunks > 0;
        var scope = selected ? GlViewport.BakeScope.SelectedChunks : GlViewport.BakeScope.EditedTiles;
        //The bar goes empty and blue before anything else happens, so the click reads as a new bake
        //even when the rays finish too fast to watch: the green that is still on screen from the
        //previous bake would otherwise sit there through the whole preparation step.
        ResetShadowBar();
        //No blocking call here: the viewport render loop walks the bake a few chunks per frame, so
        //the window stays alive and the bar under the button fills up while the rays run.
        if (!_viewport.StartShadowBake(scope)) return;
        Say(selected ? $"Ray-baking {chunks} chunk(s) - watch the bar under the button."
            : "Ray-baking the edited tiles - watch the bar under the button.");
        if (_shadowHint != null) _shadowHint.Text = "Baking, one chunk per slice - Ctrl+Z undoes when the bar is full.";
        //The chunk list is deliberately kept so the sun can be retuned and the same chunks rebaked.
    }
}
