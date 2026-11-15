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
    //Liquid sidebar: takes the map panel's slot while the Liquids tool is active, exactly as the
    //Shadows sidebar does. Its whole reason for existing is that the surface height stops being an
    //accident of where the cursor happened to land: the number in the box is BOTH the height the
    //next click writes and the height a selection is re-levelled to, so a long river can be painted
    //in several clicks and then welded to one level in one go.
    private System.Windows.Controls.Button? _liqCreateButton, _liqApplyButton, _liqCursorButton,
                                                 _liqExtendButton, _liqRemoveButton;
    private System.Windows.Controls.TextBox? _liqLevelBox;
    private System.Windows.Controls.TextBlock? _liqState, _liqLevelHead, _liqFlowHead, _liqLiftHead, _liqHint;
    private readonly System.Windows.Controls.CheckBox[] _liqTypeRows = new System.Windows.Controls.CheckBox[5];
    private System.Windows.Controls.CheckBox? _liqKeepRow, _liqJoinRow, _liqAutoRow;
    private System.Windows.Controls.Slider? _liqFlowSlider, _liqLiftSlider;
    private bool _liqBusy;
    //The liquid Place liquid arms; the four rows above it are how you change it.
    private int _liqPickedType = 1;

    private static readonly System.Windows.Media.Brush LiquidAccent =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0x5f, 0xa8));

    private static string LiquidTypeName(int t) => t switch { 2 => "ocean", 3 => "magma", 4 => "slime", _ => "water" };

    //The level as one number, or NaN while placement is still reading the terrain. This is the value
    //both the readout and the box show, so what you see is what a click or a re-level will write.
    private double LiquidLevelNumber()
    {
        if (_viewport == null) return double.NaN;
        if (!float.IsNaN(_viewport.LiquidLevel)) return _viewport.LiquidLevel;
        if (_viewport.LiquidSelectedChunks > 0) return _viewport.LiquidSelectedLevel;
        return double.NaN;
    }

    private string LevelHeadText()
    {
        if (_viewport == null) return "auto";
        if (!float.IsNaN(_viewport.LiquidLevel)) return $"{_viewport.LiquidLevel:F2} yd - fixed";
        if (_viewport.LiquidSelectedChunks > 0) return $"{_viewport.LiquidSelectedLevel:F2} yd - from the selection";
        return $"terrain + {_viewport.LiquidPlaceLift:F2} yd - auto";
    }

    private string LiquidStateText()
    {
        if (_viewport == null) return "open a terrain map first";
        if (_viewport.LiquidPlacing)
            return $"Placing {LiquidTypeName(_viewport.LiquidArmedType)} - click terrain   [surface: {LevelHeadText()}]";
        if (_viewport.LiquidSelectedChunks > 0)
            return $"{_viewport.LiquidSelectedChunks} chunk(s) selected   [surface: {LevelHeadText()}]";
        return "No liquid selected - click one to select its body, or place a new one";
    }

    private static string LiquidFlowText(int chunks) =>
        chunks <= 0 ? "off - brush only" : $"{chunks} chunks";

    //The box keeps whatever the user typed until it is used: it is only rewritten while it still holds
    //the level this panel put there itself (or nothing at all), so scrolling the surface or re-levelling
    //echoes the new height, and a number you meant for Set level is never quietly eaten by an
    //unrelated refresh.
    private double _liqBoxShown = double.NaN;

    private void SyncLevelBox()
    {
        if (_liqLevelBox == null) return;
        var cur = LiquidLevelNumber();
        var text = _liqLevelBox.Text?.Trim() ?? "";
        var ours = text.Length == 0 ||
                   (double.TryParse(text.Replace(',', '.'), out var typed) && !double.IsNaN(_liqBoxShown) &&
                       Math.Abs(typed - _liqBoxShown) < 0.005);
        if (double.IsNaN(cur))
        {
            //Nothing to mirror: drop only what this panel echoed, keep what you typed.
            if (ours) _liqLevelBox.Text = "";
            _liqBoxShown = double.NaN;
            return;
        }
        if (ours || (double.TryParse(text.Replace(',', '.'), out var kept) && Math.Abs(kept - cur) < 0.005))
        {
            _liqLevelBox.Text = cur.ToString("F2");
            _liqBoxShown = cur;
        }
    }

    private bool LiquidFlattenOn() => _viewport == null || _viewport.LiquidFlattenSelected;

    //Placement reading the ground under the cursor instead of a height you set: NaN is how the
    //viewport spells that, and it is what makes the next pond take its own height.
    private bool LiquidAutoLevel() => _viewport == null || float.IsNaN(_viewport.LiquidLevel);

    private void RefreshLiquidOptions()
    {
        if (_viewport == null) return;
        //Armed placement is the truth of what is being created, however it was armed: the Ctrl+N
        //picker window moves that state without touching the radio rows here.
        if (_viewport.LiquidPlacing) _liqPickedType = _viewport.LiquidArmedType;
        if (_liqState != null) _liqState.Text = LiquidStateText();
        if (_liqLevelHead != null) _liqLevelHead.Text = $"Level: {LevelHeadText()}";
        SyncLevelBox();
        for (int t = 1; t <= 4; t++)
            if (_liqTypeRows[t] != null) _liqTypeRows[t].IsChecked = _liqPickedType == t;
        if (_liqKeepRow != null) _liqKeepRow.IsChecked = !LiquidFlattenOn();
        if (_liqAutoRow != null) _liqAutoRow.IsChecked = LiquidAutoLevel();
        if (_liqJoinRow != null) _liqJoinRow.IsChecked = _viewport.LiquidJoinLevels;
        if (_liqFlowHead != null) _liqFlowHead.Text = $"Flow past the brush: {LiquidFlowText(_viewport.LiquidFlowReach)}";
        if (_liqFlowSlider != null) _liqFlowSlider.Value = _viewport.LiquidFlowReach;
        if (_liqLiftHead != null) _liqLiftHead.Text = $"Lift above terrain (auto level): {_viewport.LiquidPlaceLift:F2} yd";
        if (_liqLiftSlider != null) _liqLiftSlider.Value = _viewport.LiquidPlaceLift;
    }

    //Swap the sidebar in/out; the map panel, the asset drawer and the Shadows sidebar share the slot,
    //so it is only handed back while none of the other sidebars is showing.
    private void UpdateLiquidPanel()
    {
        if (_viewport == null || _liqBusy) return;
        _liqBusy = true;
        try
        {
            if (_viewport.CurrentTool == GlViewport.Tool.Liquid)
            {
                SidePanel.Visibility = Visibility.Collapsed;
                LiquidSide.Visibility = Visibility.Visible;
                RefreshLiquidOptions();
            }
            else if (LiquidSide.Visibility == Visibility.Visible)
            {
                LiquidSide.Visibility = Visibility.Collapsed;
                if (ShadowSide.Visibility != Visibility.Visible && DoodadDrawer.Visibility != Visibility.Visible)
                    SidePanel.Visibility = Visibility.Visible;
            }
        }
        finally { _liqBusy = false; }
    }

    private void BuildLiquidPanel()
    {
        var col = new System.Windows.Controls.StackPanel();

        System.Windows.Controls.TextBlock Section(string text)
        {
            var t = new System.Windows.Controls.TextBlock
            {
                Text = text, Foreground = LiquidAccent, FontSize = 12,
                Margin = new Thickness(0, 8, 0, 2),
            };
            col.Children.Add(t);
            return t;
        }

        //Progress comes first here too: this is the line the user watches while a placement run is
        //walking down a river, and it doubles as the readout of the level every click will write.
        _liqState = new System.Windows.Controls.TextBlock
        { Text = LiquidStateText(), Foreground = LiquidAccent, FontSize = 11, Margin = new Thickness(0, 2, 0, 6) };
        col.Children.Add(_liqState);

        Section("Create liquid");
        foreach (var t in new[] { 1, 2, 3, 4 })
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = $"{LiquidTypeName(t)} {(t == 1 ? "(rivers, lakes)" : t == 2 ? "(fixed sea level)" : "")}",
                Foreground = System.Windows.Media.Brushes.White,
                IsChecked = _liqPickedType == t, Margin = new Thickness(0, 3, 0, 1),
            };
            int type = t;
            //Re-arming is what the Ctrl+N picker does too, so ticking another row in the middle of a
            //run really does switch the liquid the next click writes - the level you already fixed
            //stays fixed.
            cb.Click += (_, _) =>
            {
                _liqPickedType = type;
                if (_viewport != null && _viewport.LiquidPlacing) _viewport.ArmLiquid(type);
                RefreshLiquidOptions();
            };
            _liqTypeRows[t] = cb;
            col.Children.Add(cb);
        }
        _liqCreateButton = new System.Windows.Controls.Button
        {
            Content = "Place liquid", Foreground = System.Windows.Media.Brushes.White,
            Background = LiquidAccent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 8, 0, 2),
            ToolTip = "Start placing the liquid you ticked above: the brush fills the chunks under every click, and the surface keeps one height across all of them",
        };
        _liqCreateButton.Click += (_, _) =>
        {
            if (_viewport == null) return;
            _viewport.ArmLiquid(_liqPickedType);
            _viewport.Focus();
        };
        col.Children.Add(_liqCreateButton);

        Section("Surface level");
        _liqLevelHead = new System.Windows.Controls.TextBlock
        { Text = $"Level: {LevelHeadText()}", Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) };
        _liqLevelBox = new System.Windows.Controls.TextBox { Width = 92, Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 2) };
        col.Children.Add(_liqLevelHead);
        col.Children.Add(_liqLevelBox);
        SyncLevelBox();

        _liqApplyButton = new System.Windows.Controls.Button
        {
            Content = "Set level", Foreground = System.Windows.Media.Brushes.White,
            Background = LiquidAccent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 6, 0, 2),
            ToolTip = "Type a height into the box, then press this: the chunks you selected are set to exactly that height, and every later click writes it too",
        };
        _liqApplyButton.Click += (_, _) =>
        {
            if (_viewport == null) return;
            var typed = (_liqLevelBox?.Text ?? "").Trim().Replace(',', '.');
            if (double.TryParse(typed, out double v))
            {
                _viewport.SetLiquidLevel(v);
                if (_viewport.LiquidSelectedChunks > 0) _viewport.ApplyLiquidLevel(v);
                _viewport.Focus();
            }
            else Say("Set level: type the surface height in yards into the box first, for example 12.5");
            RefreshLiquidOptions();
        };
        col.Children.Add(_liqApplyButton);

        _liqCursorButton = new System.Windows.Controls.Button
        {
            Content = "Level to cursor", Foreground = System.Windows.Media.Brushes.White,
            Background = LiquidAccent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 4, 0, 2),
            ToolTip = "Set the selection's height to the terrain under the cursor - click the shoreline you want the water to reach",
        };
        _liqCursorButton.Click += (_, _) =>
        {
            if (_viewport == null) return;
            _viewport.LevelLiquidToCursor();
            _viewport.Focus();
            RefreshLiquidOptions();
        };
        col.Children.Add(_liqCursorButton);

        _liqKeepRow = new System.Windows.Controls.CheckBox
        {
            Content = "Keep each chunk's own height", Foreground = System.Windows.Media.Brushes.White,
            IsChecked = !LiquidFlattenOn(), Margin = new Thickness(0, 6, 0, 1),
            ToolTip = "Off (the default): one flat plane, which is what turns several groups placed apart into one level. On: every chunk only moves by its own amount, so the height differences stay.",
        };
        _liqKeepRow.Click += (_, _) =>
        {
            if (_viewport != null) _viewport.SetLiquidFlatten(!LiquidFlattenOn());
            RefreshLiquidOptions();
        };
        col.Children.Add(_liqKeepRow);

        _liqAutoRow = new System.Windows.Controls.CheckBox
        {
            Content = "Auto level: read the terrain each click", Foreground = System.Windows.Media.Brushes.White,
            IsChecked = LiquidAutoLevel(), Margin = new Thickness(0, 6, 0, 1),
            ToolTip = "On: the first click takes its height from the ground under the cursor and the later clicks reuse it. Off: the level in the box is the height every click writes, whatever the ground there does.",
        };
        _liqAutoRow.Click += (_, _) =>
        {
            if (_viewport != null) _viewport.SetLiquidLevelFixed(LiquidAutoLevel());
            RefreshLiquidOptions();
        };
        col.Children.Add(_liqAutoRow);

        Section("Grow it");
        _liqExtendButton = new System.Windows.Controls.Button
        {
            Content = "Extend along bed", Foreground = System.Windows.Media.Brushes.White,
            Background = LiquidAccent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 6, 0, 2),
            ToolTip = "Walk the selected surface along the terrain: every neighbour chunk whose boundary with the water sits below the level gets liquid at that same level, and stays selected so pressing this again keeps going",
        };
        _liqExtendButton.Click += (_, _) =>
        {
            if (_viewport == null) return;
            _viewport.ExtendLiquidBed();
            _viewport.Focus();
            RefreshLiquidOptions();
        };
        col.Children.Add(_liqExtendButton);

        _liqFlowHead = new System.Windows.Controls.TextBlock
        { Text = $"Flow past the brush: {LiquidFlowText(_viewport?.LiquidFlowReach ?? 0)}", Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) };
        _liqFlowSlider = new System.Windows.Controls.Slider
        { Minimum = 0, Maximum = 128, Value = _viewport?.LiquidFlowReach ?? 0, Foreground = LiquidAccent, Margin = new Thickness(0, 0, 0, 2) };
        _liqFlowSlider.ValueChanged += (_, e) =>
        {
            if (_viewport != null) _viewport.SetLiquidFlowReach((int)e.NewValue);
            if (_liqFlowHead != null) _liqFlowHead.Text = $"Flow past the brush: {LiquidFlowText((int)e.NewValue)}";
        };
        col.Children.Add(_liqFlowHead);
        col.Children.Add(_liqFlowSlider);

        Section("Options");
        _liqJoinRow = new System.Windows.Controls.CheckBox
        {
            Content = "Join bodies across heights", Foreground = System.Windows.Media.Brushes.White,
            IsChecked = _viewport?.LiquidJoinLevels ?? false, Margin = new Thickness(0, 4, 0, 1),
            ToolTip = "On: one click selects the whole liquid of that type, whatever heights its parts were placed at - and a re-level then reaches all of them. Off: the bed decides, so a chunk the ground keeps above the water stays a separate body, which is how a waterfall or a dammed pool stays put.",
        };
        _liqJoinRow.Click += (_, _) =>
        {
            if (_viewport != null) _viewport.SetLiquidJoinLevels(!_viewport.LiquidJoinLevels);
            RefreshLiquidOptions();
        };
        col.Children.Add(_liqJoinRow);

        _liqLiftHead = new System.Windows.Controls.TextBlock
        { Text = $"Lift above terrain (auto level): {_viewport?.LiquidPlaceLift ?? 1.5:F2} yd", Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) };
        _liqLiftSlider = new System.Windows.Controls.Slider
        { Minimum = -10, Maximum = 60, Value = _viewport?.LiquidPlaceLift ?? 1.5, Foreground = LiquidAccent, Margin = new Thickness(0, 0, 0, 2) };
        _liqLiftSlider.ValueChanged += (_, e) =>
        {
            if (_viewport != null) _viewport.SetLiquidPlaceLift(e.NewValue);
            if (_liqLiftHead != null) _liqLiftHead.Text = $"Lift above terrain (auto level): {e.NewValue:F2} yd";
        };
        col.Children.Add(_liqLiftHead);
        col.Children.Add(_liqLiftSlider);

        _liqRemoveButton = new System.Windows.Controls.Button
        {
            Content = "Remove liquid", Foreground = System.Windows.Media.Brushes.White,
            Background = LiquidAccent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 8, 0, 2),
            ToolTip = "Take the liquid off every selected chunk (Ctrl+Z gets it back)",
        };
        _liqRemoveButton.Click += (_, _) =>
        {
            if (_viewport == null) return;
            _viewport.RemoveSelectedLiquid();
            _viewport.Focus();
            RefreshLiquidOptions();
        };
        col.Children.Add(_liqRemoveButton);

        _liqHint = new System.Windows.Controls.TextBlock
        {
            Text = "One height, two uses: the number in the box is what the next click writes AND what Set level puts over the chunks you selected.\n" +
                   "Place liquid arms the ticked type, click terrain: the brush fills the chunks it touches at that height, and every later click reuses it instead of reading the ground again.\n" +
                   "Extend along bed walks that height down the terrain (the Flow past the brush slider does the same walk right after each click, 0 = brush only).\n" +
                   "Keep each chunk's own height off = the selection becomes one flat plane, which is what welds several separately placed groups onto one level. Ctrl+Z undoes.",
            Foreground = System.Windows.Media.Brushes.Gray, FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        col.Children.Add(_liqHint);

        LiquidSide.Child = col;
    }
}
