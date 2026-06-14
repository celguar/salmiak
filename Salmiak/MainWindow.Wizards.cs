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

    private void EditWmo_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null || _mpq == null) { StatusText.Text = "Open a WoW directory first."; return; }
        if (_viewport.WmoEditMode) { StatusText.Text = "Already editing a WMO - Save & Close or Cancel first."; return; }

        var pick = ShowWmoEditPicker();
        if (pick.Path == null) return;

        string target = pick.Path;
        if (pick.Copy)
        {
            string? name = PromptText("Copy WMO", "New WMO name (no extension):",
                System.IO.Path.GetFileNameWithoutExtension(pick.Path) + "_copy");
            if (string.IsNullOrWhiteSpace(name)) return;
            var np = _viewport.CopyWmo(pick.Path, name);
            if (np == null) { StatusText.Text = "Copy WMO failed."; return; }
            target = np;
            _allWmosCache = null;
        }

        _viewport.EnterWmoEdit(target);
        _viewport.SetPrimaryMode(GlViewport.EditorMode.Doodad);
        WmoEditBar.Visibility = Visibility.Visible;
        WmoEditLabel.Text = $"WMO edit: {System.IO.Path.GetFileName(target)} - place furniture (N), then Save & Close";
        StatusText.Text = "WMO edit mode - press N to pick furniture, click to place on the WMO surface.";

        PopulateWmoSetBox();
    }

    private bool _populatingWmoSet;

    private void PopulateWmoSetBox(int? selectIndex = null)
    {
        if (_viewport == null) return;
        _populatingWmoSet = true;
        WmoSetBox.Items.Clear();
        var sets = _viewport.WmoEditDoodadSets();
        foreach (var (name, count) in sets) WmoSetBox.Items.Add($"{name} ({count})");
        int sel = selectIndex ?? _viewport.WmoEditDoodadSet;
        WmoSetBox.SelectedIndex = sets.Count > 0 ? Math.Min(Math.Max(sel, 0), sets.Count - 1) : -1;
        WmoSetBox.IsEnabled = sets.Count > 1;
        _populatingWmoSet = false;
    }

    private void WmoSet_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_populatingWmoSet || _viewport == null || !_viewport.WmoEditMode) return;
        if (WmoSetBox.SelectedIndex >= 0) _viewport.SetWmoEditDoodadSet(WmoSetBox.SelectedIndex);
    }

    private void WmoAddSet_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null || !_viewport.WmoEditMode) return;
        string? name = PromptText("New furniture set", "Set name:", $"Set {WmoSetBox.Items.Count}");
        if (name == null) return;
        int idx = _viewport.AddWmoEditDoodadSet(name);
        if (idx < 0) { StatusText.Text = "Couldn't add furniture set."; return; }
        PopulateWmoSetBox(idx);
        StatusText.Text = "New furniture set added & selected - place furniture, then Save & Close.";
    }

    private void WmoEditSave_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null || !_viewport.WmoEditMode) { WmoEditBar.Visibility = Visibility.Collapsed; return; }
        string name = System.IO.Path.GetFileName(_viewport.WmoEditPath ?? "");
        int n = _viewport.SaveWmoEdit();
        _viewport.ExitWmoEdit(save: false);
        WmoEditBar.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Saved {n} doodad(s) into {name}. Export to MPQ to use it in-game.";
    }

    private void WmoEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) { WmoEditBar.Visibility = Visibility.Collapsed; return; }
        _viewport.ExitWmoEdit(save: false);
        WmoEditBar.Visibility = Visibility.Collapsed;
        StatusText.Text = "WMO edit cancelled.";
    }

    private (string? Path, bool Copy) ShowWmoEditPicker()
    {
        if (_viewport == null || _mpq == null) return (null, false);
        var all = _mpq.ListModels(true);
        const int MaxRows = 500;
        string? chosen = null; bool copy = false;

        var search = new System.Windows.Controls.TextBox { Margin = new Thickness(4) };
        var info = new System.Windows.Controls.TextBlock
        { Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 10, Margin = new Thickness(6, 0, 0, 4) };
        var list = new System.Windows.Controls.ListBox
        { Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.White };
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(list, false);
        var wrapFactory = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.WrapPanel));
        wrapFactory.SetValue(System.Windows.Controls.WrapPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        list.ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(wrapFactory);
        System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(list, System.Windows.Controls.ScrollBarVisibility.Disabled);

        var rows = new List<(System.Windows.Controls.ListBoxItem Item, System.Windows.Controls.Image Img, string Path)>();

        void Populate()
        {
            string filter = search.Text.Trim();
            list.Items.Clear(); rows.Clear();
            int shown = 0, matched = 0;
            foreach (var p in all)
            {
                if (filter.Length > 0 && p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched++;
                if (shown >= MaxRows) continue;
                shown++;
                var img = new System.Windows.Controls.Image
                { Width = 64, Height = 64, Stretch = System.Windows.Media.Stretch.Uniform };
                var tile = new System.Windows.Controls.Border { Child = img, Padding = new Thickness(2), Margin = new Thickness(2) };
                var item = new System.Windows.Controls.ListBoxItem
                { Content = tile, Tag = p, Padding = new Thickness(0), ToolTip = System.IO.Path.GetFileNameWithoutExtension(p) };
                list.Items.Add(item);
                rows.Add((item, img, p));
            }
            info.Text = matched > shown ? $"Showing {shown} of {matched} matches - type to narrow." : $"{matched} WMO(s)";
        }

        void GenerateVisible()
        {
            foreach (var (item, img, path) in rows)
            {
                if (img.Source != null || !item.IsVisible) continue;
                double top;
                try { top = item.TransformToAncestor(list).Transform(new System.Windows.Point(0, 0)).Y; }
                catch { continue; }
                if (top + item.ActualHeight < 0 || top > list.ActualHeight) continue;
                var src = GetModelThumb(path, true);
                if (src != null) img.Source = src;
            }
        }
        void ScheduleGenerate() => Dispatcher.BeginInvoke(GenerateVisible, System.Windows.Threading.DispatcherPriority.Background);

        search.TextChanged += (_, _) => { Populate(); ScheduleGenerate(); };
        list.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler((_, _) => ScheduleGenerate()));
        Populate();

        string? Selected() => list.SelectedItem is System.Windows.Controls.ListBoxItem it && it.Tag is string s ? s : null;

        var topPanel = new System.Windows.Controls.StackPanel();
        topPanel.Children.Add(search);
        topPanel.Children.Add(info);

        var editBtn = new System.Windows.Controls.Button { Content = "Edit", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(4) };
        var copyBtn = new System.Windows.Controls.Button { Content = "Copy & edit...", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(4) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(4), IsCancel = true };
        var btnPanel = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        btnPanel.Children.Add(editBtn); btnPanel.Children.Add(copyBtn); btnPanel.Children.Add(cancelBtn);

        var dock = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(topPanel, System.Windows.Controls.Dock.Top);
        System.Windows.Controls.DockPanel.SetDock(btnPanel, System.Windows.Controls.Dock.Bottom);
        dock.Children.Add(topPanel);
        dock.Children.Add(btnPanel);
        dock.Children.Add(list);

        var win = new Window
        {
            Title = "WMO edit - pick a building to edit", Width = 500, Height = 620, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = dock,
        };
        editBtn.Click += (_, _) => { chosen = Selected(); if (chosen != null) win.Close(); };
        copyBtn.Click += (_, _) => { chosen = Selected(); if (chosen != null) { copy = true; win.Close(); } };
        list.MouseDoubleClick += (_, _) => { chosen = Selected(); if (chosen != null) win.Close(); };
        win.Loaded += (_, _) => { search.Focus(); ScheduleGenerate(); };
        _viewport.ModalOpen = true;
        try { win.ShowDialog(); } finally { _viewport.ModalOpen = false; }
        return (chosen, copy);
    }

    private string? PromptText(string title, string label, string initial)
    {
        var tb = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(8), MinWidth = 300 };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 72, Margin = new Thickness(4) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Width = 72, Margin = new Thickness(4) };
        var buttons = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(4) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        { Text = label, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(8, 8, 8, 0) });
        panel.Children.Add(tb);
        panel.Children.Add(buttons);
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        bool okd = false;
        ok.Click += (_, _) => { okd = true; win.Close(); };
        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        win.ShowDialog();
        return okd ? tb.Text?.Trim() : null;
    }

    private void ShowAreaIdPicker()
    {
        if (_viewport == null) return;
        var areas = _viewport.AreaList();
        var nameById = new Dictionary<int, string>();
        foreach (var (id, name) in areas) nameById[id] = name;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = areas.Count > 0 ? "Search a zone by name or id (or type an id below):"
                                   : "AreaTable.dbc unavailable - enter an area id below:",
            Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 6),
        });

        var search = new System.Windows.Controls.TextBox { Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 6) };
        if (areas.Count > 0) panel.Children.Add(search);

        var list = new System.Windows.Controls.ListBox
        {
            Height = 300, Margin = new Thickness(0, 0, 0, 8),
            Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black,
        };
        if (areas.Count > 0) panel.Children.Add(list);

        var idRow = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal };
        idRow.Children.Add(new System.Windows.Controls.TextBlock
        { Text = "Area id:", Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var idBox = new System.Windows.Controls.TextBox
        { Text = _viewport.TargetAreaId.ToString(), Width = 90, Padding = new Thickness(3) };
        idRow.Children.Add(idBox);
        var nameLabel = new System.Windows.Controls.TextBlock
        { Foreground = System.Windows.Media.Brushes.LightGreen, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        idRow.Children.Add(nameLabel);
        panel.Children.Add(idRow);

        var ok = new System.Windows.Controls.Button { Content = "Set id", Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Area-id paint", Width = 360, Height = areas.Count > 0 ? 470 : 170, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };

        void Rebuild(string q)
        {
            list.Items.Clear();
            q = q.Trim();
            int shown = 0;
            foreach (var (id, name) in areas)
            {
                if (q.Length > 0 &&
                    name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !id.ToString().Contains(q)) continue;
                list.Items.Add(new System.Windows.Controls.ListBoxItem { Content = $"{name}   (id {id})", Tag = id });
                if (++shown >= 600) break;
            }
        }
        void SyncName()
        {
            nameLabel.Text = int.TryParse(idBox.Text.Trim(), out int tid) && nameById.TryGetValue(tid, out var nm)
                ? nm : "";
        }

        search.TextChanged += (_, _) => Rebuild(search.Text);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is System.Windows.Controls.ListBoxItem it && it.Tag is int sid)
            { idBox.Text = sid.ToString(); SyncName(); }
        };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) win.DialogResult = true; };
        idBox.TextChanged += (_, _) => SyncName();
        idBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) win.DialogResult = true; };
        ok.Click += (_, _) => win.DialogResult = true;

        win.Loaded += (_, _) =>
        {
            Rebuild("");
            SyncName();
            foreach (System.Windows.Controls.ListBoxItem it in list.Items)
                if (it.Tag is int sid && sid == _viewport.TargetAreaId) { it.IsSelected = true; list.ScrollIntoView(it); break; }
            (areas.Count > 0 ? (System.Windows.Controls.Control)search : idBox).Focus();
        };

        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        if (int.TryParse(idBox.Text.Trim(), out int chosenId) && chosenId >= 0) _viewport.SetAreaId(chosenId);
        else StatusText.Text = "Area-id paint: enter a non-negative integer.";
    }

    private void ShowGeneratePicker()
    {
        if (_viewport == null) return;
        var textures = _viewport.LoadedTextures();
        if (textures.Count == 0) { StatusText.Text = "Ground generator: no textures loaded yet."; return; }

        var thumbs = new Dictionary<string, System.Windows.Media.ImageSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in textures) thumbs[t] = DecodeThumbnail(t);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
        System.Windows.Controls.ComboBox MakeRow(string label, int defaultIdx)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            { Text = label, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 6, 0, 2) });
            var cb = new System.Windows.Controls.ComboBox();
            foreach (var t in textures)
            {
                var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                row.Children.Add(new System.Windows.Controls.Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0), Source = thumbs[t] });
                row.Children.Add(new System.Windows.Controls.TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(t), VerticalAlignment = VerticalAlignment.Center });
                cb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = row, Tag = t, ToolTip = t });
            }
            cb.SelectedIndex = Math.Min(defaultIdx, textures.Count - 1);
            panel.Children.Add(cb);
            return cb;
        }
        int IndexOf(string? path, int fallback)
        {
            if (path != null) { int i = textures.FindIndex(t => string.Equals(t, path, StringComparison.OrdinalIgnoreCase)); if (i >= 0) return i; }
            return Math.Min(fallback, textures.Count - 1);
        }
        var baseCb = MakeRow("Base texture (layer 0)", IndexOf(_viewport.GenTex(0), 0));
        var l1 = MakeRow("Layer 1", IndexOf(_viewport.GenTex(1), 1));
        var l2 = MakeRow("Layer 2", IndexOf(_viewport.GenTex(2), 2));
        var l3 = MakeRow("Layer 3", IndexOf(_viewport.GenTex(3), 3));

        System.Windows.Controls.Slider MakeSlider(string label, double min, double max, double val, string fmt)
        {
            var header = new System.Windows.Controls.TextBlock
            { Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2), Text = $"{label}: {val.ToString(fmt)}" };
            var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = val };
            sl.ValueChanged += (_, e) => header.Text = $"{label}: {e.NewValue.ToString(fmt)}";
            panel.Children.Add(header);
            panel.Children.Add(sl);
            return sl;
        }
        var cov1 = MakeSlider("Layer 1 coverage", 0, 1, _viewport.GenCoverage(1), "F2");
        var cov2 = MakeSlider("Layer 2 coverage", 0, 1, _viewport.GenCoverage(2), "F2");
        var cov3 = MakeSlider("Layer 3 coverage", 0, 1, _viewport.GenCoverage(3), "F2");
        double patchVal = Math.Clamp((0.05 - _viewport.GenScale) / 0.044, 0, 1);
        var patch = MakeSlider("Patch size", 0, 1, patchVal, "F2");

        var ok = new System.Windows.Controls.Button { Content = "Generate with these", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Ground generator - pick 4 textures", Width = 400, Height = 620, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray,
            Content = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        string PathOf(System.Windows.Controls.ComboBox cb) =>
            (cb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";
        string b = PathOf(baseCb), p1 = PathOf(l1), p2 = PathOf(l2), p3 = PathOf(l3);
        float scale = (float)(0.05 - patch.Value * 0.044);
        if (b.Length > 0 && p1.Length > 0 && p2.Length > 0 && p3.Length > 0)
            _viewport.SetGenerateConfig(b, p1, p2, p3, (float)cov1.Value, (float)cov2.Value, (float)cov3.Value, scale);
    }

    private void ShowBlendSettings()
    {
        if (_viewport == null) return;
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };

        var feather = new System.Windows.Controls.RadioButton { Content = "Feather (soft, linear fade)", Foreground = System.Windows.Media.Brushes.White, IsChecked = !_viewport.BlendAggressive, Margin = new Thickness(0, 4, 0, 2) };
        var aggressive = new System.Windows.Controls.RadioButton { Content = "Aggressive (full equalise, stronger)", Foreground = System.Windows.Media.Brushes.White, IsChecked = _viewport.BlendAggressive, Margin = new Thickness(0, 2, 0, 6) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Blend method", Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold });
        panel.Children.Add(feather);
        panel.Children.Add(aggressive);

        var bandHeader = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2), Text = $"Band width (texels): {_viewport.BlendBandWidth}" };
        var band = new System.Windows.Controls.Slider { Minimum = 2, Maximum = 64, Value = _viewport.BlendBandWidth, IsSnapToTickEnabled = true, TickFrequency = 1 };
        band.ValueChanged += (_, e) => bandHeader.Text = $"Band width (texels): {(int)e.NewValue}";
        panel.Children.Add(bandHeader);
        panel.Children.Add(band);

        var ok = new System.Windows.Controls.Button { Content = "Apply", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Blend settings", Width = 340, Height = 240, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }
        _viewport.SetBlendConfig(aggressive.IsChecked == true, (int)band.Value);
    }

    private void ShowRoadPicker()
    {
        if (_viewport == null) return;
        var textures = _viewport.LoadedTextures();
        if (textures.Count == 0) { StatusText.Text = "Road: no textures loaded yet."; return; }

        var thumbs = new Dictionary<string, System.Windows.Media.ImageSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in textures) thumbs[t] = DecodeThumbnail(t);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
        System.Windows.Controls.ComboBox MakeRow(string label, int defaultIdx)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 6, 0, 2) });
            var cb = new System.Windows.Controls.ComboBox();
            foreach (var t in textures)
            {
                var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                row.Children.Add(new System.Windows.Controls.Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0), Source = thumbs[t] });
                row.Children.Add(new System.Windows.Controls.TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(t), VerticalAlignment = VerticalAlignment.Center });
                cb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = row, Tag = t, ToolTip = t });
            }
            cb.SelectedIndex = Math.Min(defaultIdx, textures.Count - 1);
            panel.Children.Add(cb);
            return cb;
        }
        int IndexOf(string? path, int fb)
        { if (path != null) { int i = textures.FindIndex(t => string.Equals(t, path, StringComparison.OrdinalIgnoreCase)); if (i >= 0) return i; } return Math.Min(fb, textures.Count - 1); }

        var road = MakeRow("Road", IndexOf(_viewport.RoadTex(0), 0));
        var contour1 = MakeRow("Contour 1 (edges)", IndexOf(_viewport.RoadTex(1), 1));
        var contour2 = MakeRow("Contour 2 (blends with 1)", IndexOf(_viewport.RoadTex(2), 2));

        System.Windows.Controls.Slider MakeSlider(string label, double min, double max, double val, string fmt)
        {
            var header = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2), Text = $"{label}: {val.ToString(fmt)}" };
            var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = val };
            sl.ValueChanged += (_, e) => header.Text = $"{label}: {e.NewValue.ToString(fmt)}";
            panel.Children.Add(header); panel.Children.Add(sl);
            return sl;
        }
        var width = MakeSlider("Road width", 0.1, 0.8, _viewport.RoadWidth, "F2");
        var contourW = MakeSlider("Contour width", 0.05, 0.5, _viewport.RoadContour, "F2");
        var noise = MakeSlider("Edge noise", 0, 0.4, _viewport.RoadNoise, "F2");

        var ok = new System.Windows.Controls.Button { Content = "Paint roads with these", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Road generator", Width = 400, Height = 560, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray,
            Content = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        string PathOf(System.Windows.Controls.ComboBox cb) => (cb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";
        string rd = PathOf(road), c1 = PathOf(contour1), c2 = PathOf(contour2);
        if (rd.Length > 0 && c1.Length > 0 && c2.Length > 0)
            _viewport.SetRoadConfig(rd, c1, c2, (float)width.Value, (float)contourW.Value, (float)noise.Value);
    }

    private void ShowLiquidPicker()
    {
        if (_viewport == null) return;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Pick a liquid to place. A preview follows the cursor - scroll to fine-tune the surface height, click to fill.",
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        var win = new Window
        {
            Title = "Place liquid", Width = 300, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };

        var entries = new (int Type, string Name, System.Windows.Media.Color Swatch)[]
        {
            (1, "Water (river / lake)", System.Windows.Media.Color.FromRgb(0x22, 0x57, 0x8C)),
            (2, "Ocean", System.Windows.Media.Color.FromRgb(0x14, 0x3C, 0x66)),
            (3, "Magma", System.Windows.Media.Color.FromRgb(0xF2, 0x59, 0x0D)),
            (4, "Slime", System.Windows.Media.Color.FromRgb(0x4C, 0xB2, 0x26)),
        };
        foreach (var (type, name, swatch) in entries)
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(new System.Windows.Controls.Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(3),
                Background = new System.Windows.Media.SolidColorBrush(swatch),
            });
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var btn = new System.Windows.Controls.Button
            { Content = row, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left };
            int t = type;
            btn.Click += (_, _) => { _viewport.ArmLiquid(t); win.Close(); };
            panel.Children.Add(btn);
        }
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        panel.Children.Add(cancel);

        _viewport.ModalOpen = true;
        try { win.ShowDialog(); } finally { _viewport.ModalOpen = false; }
    }

    private void ShowRiverPicker()
    {
        if (_viewport == null) return;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
        System.Windows.Controls.Slider MakeSlider(string label, double min, double max, double val, string unit)
        {
            var header = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2), Text = $"{label}: {val:F0}{unit}" };
            var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = val };
            sl.ValueChanged += (_, e) => header.Text = $"{label}: {e.NewValue:F0}{unit}";
            panel.Children.Add(header); panel.Children.Add(sl);
            return sl;
        }
        var bedW = MakeSlider("Bed width", 4, 60, _viewport.RiverBedWidth, " yd");
        var bankW = MakeSlider("Bank width (rim blend)", 2, 40, _viewport.RiverBankWidth, " yd");
        var depth = MakeSlider("Depth", 1, 30, _viewport.RiverDepth, " yd");

        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Fill with", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2) });
        var fill = new System.Windows.Controls.ComboBox();
        foreach (var name in new[] { "Nothing (dry valley)", "Water", "Ocean", "Magma", "Slime" })
            fill.Items.Add(name);
        fill.SelectedIndex = Math.Clamp(_viewport.RiverWaterType, 0, 4);
        panel.Children.Add(fill);
        var waterD = MakeSlider("Water depth (above bed bottom)", 1, 20, _viewport.RiverWaterDepth, " yd");

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Click waypoints along the terrain to draw the course, then press Enter to carve. Backspace removes the last point; Esc exits.",
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });

        var ok = new System.Windows.Controls.Button { Content = "Start drawing the course", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "River / valley carver", Width = 380, Height = 430, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray,
            Content = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        _viewport.SetRiverConfig((float)bedW.Value, (float)bankW.Value, (float)depth.Value,
                                 fill.SelectedIndex, (float)waterD.Value);
    }

    private void ShowAutoPaintPicker()
    {
        if (_viewport == null) return;
        var textures = _viewport.LoadedTextures();
        if (textures.Count == 0) { StatusText.Text = "Auto-paint: no textures loaded yet."; return; }

        var thumbs = new Dictionary<string, System.Windows.Media.ImageSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in textures) thumbs[t] = DecodeThumbnail(t);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
        System.Windows.Controls.ComboBox MakeRow(string label, int defaultIdx, bool optional)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 6, 0, 2) });
            var cb = new System.Windows.Controls.ComboBox();
            if (optional)
                cb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "(none - two bands only)", Tag = null });
            foreach (var t in textures)
            {
                var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                row.Children.Add(new System.Windows.Controls.Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0), Source = thumbs[t] });
                row.Children.Add(new System.Windows.Controls.TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(t), VerticalAlignment = VerticalAlignment.Center });
                cb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = row, Tag = t, ToolTip = t });
            }
            cb.SelectedIndex = Math.Min(defaultIdx, cb.Items.Count - 1);
            panel.Children.Add(cb);
            return cb;
        }
        int IndexOf(string? path, int fb)
        { if (path != null) { int i = textures.FindIndex(t => string.Equals(t, path, StringComparison.OrdinalIgnoreCase)); if (i >= 0) return i; } return Math.Min(fb, textures.Count - 1); }

        var flat = MakeRow("Flat ground (gentle slopes)", IndexOf(_viewport.AutoPaintTex(0), 0), optional: false);
        var mid = MakeRow("Mid band (optional - dirt/scree)", _viewport.AutoPaintTex(1) is { } m ? IndexOf(m, 0) + 1 : 0, optional: true);
        var cliff = MakeRow("Cliff (steep faces)", IndexOf(_viewport.AutoPaintTex(2), Math.Min(1, textures.Count - 1)), optional: false);

        System.Windows.Controls.Slider MakeSlider(string label, double min, double max, double val)
        {
            var header = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2), Text = $"{label}: {val:F0}°" };
            var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = val };
            sl.ValueChanged += (_, e) => header.Text = $"{label}: {e.NewValue:F0}°";
            panel.Children.Add(header); panel.Children.Add(sl);
            return sl;
        }
        var lowDeg = MakeSlider("Flat up to", 5, 75, _viewport.AutoPaintLowDeg);
        var highDeg = MakeSlider("Cliff from", 10, 85, _viewport.AutoPaintHighDeg);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Characters can walk slopes up to ~50°. Transitions are dithered around the thresholds.",
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        var ok = new System.Windows.Controls.Button { Content = "Paint by slope with these", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Slope auto-paint", Width = 400, Height = 560, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray,
            Content = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        string? PathOf(System.Windows.Controls.ComboBox cb) => (cb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        string? fl = PathOf(flat); string? md = PathOf(mid); string? cl = PathOf(cliff);
        if (fl != null && cl != null)
            _viewport.SetAutoPaintConfig(fl, md, cl, (float)lowDeg.Value, (float)highDeg.Value);
    }

    private List<(uint Id, string Label)> DbcOptions(string dbcName, int nameField)
    {
        var list = new List<(uint, string)>();
        if (_mpq == null) return list;
        try
        {
            DbcFile d;
            using (var s = _mpq.OpenFile($@"DBFilesClient\{dbcName}.dbc")) d = DbcFile.Read(s);
            for (int i = 0; i < d.RecordCount; i++)
            {
                uint id = d.GetU(i, 0);
                string label = $"{id}";
                if (nameField >= 0 && nameField < d.FieldCount)
                {
                    uint ofs = d.GetU(i, nameField);
                    if (ofs > 0 && ofs < d.StringBlock.Length)
                    {
                        int end = (int)ofs;
                        while (end < d.StringBlock.Length && d.StringBlock[end] != 0) end++;
                        string nm = System.Text.Encoding.UTF8.GetString(d.StringBlock, (int)ofs, end - (int)ofs);
                        if (nm.Length > 0) label = $"{nm}  ({id})";
                    }
                }
                list.Add((id, label));
            }
        }
        catch { }
        return list;
    }

    private void ShowNewZoneWizard()
    {
        if (_viewport == null) return;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        void Label(string t) => panel.Children.Add(new System.Windows.Controls.TextBlock
        { Text = t, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2) });

        Label("Zone name");
        var nameBox = new System.Windows.Controls.TextBox();
        panel.Children.Add(nameBox);

        uint Picked(System.Windows.Controls.ComboBox box, List<(uint Id, string Label)> options) =>
            box.SelectedIndex > 0 && box.SelectedIndex - 1 < options.Count ? options[box.SelectedIndex - 1].Id : 0;

        System.Windows.Media.MediaPlayer? player = null;
        System.Windows.Controls.Button? playingBtn = null;
        void StopPreview()
        {
            player?.Stop(); player?.Close(); player = null;
            if (playingBtn != null) playingBtn.Content = "▶";
            playingBtn = null;
        }
        void TogglePreview(System.Windows.Controls.Button btn, Func<string?> mpqFile)
        {
            if (playingBtn == btn) { StopPreview(); return; }
            StopPreview();
            string? path = mpqFile();
            if (path == null || _mpq == null) { StatusText.Text = "Preview: no sound file for that entry."; return; }
            try
            {
                byte[] bytes;
                using (var s = _mpq.OpenFile(path)) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "wme-music-preview" + System.IO.Path.GetExtension(path));
                File.WriteAllBytes(tmp, bytes);
                player = new System.Windows.Media.MediaPlayer();
                player.MediaEnded += (_, _) => StopPreview();
                player.Open(new Uri(tmp));
                player.Play();
                playingBtn = btn; btn.Content = "■";
                StatusText.Text = $"Playing {System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex) { StatusText.Text = $"Preview failed: {ex.Message}"; }
        }

        System.Windows.Controls.ComboBox Combo(List<(uint Id, string Label)> options, Func<uint, string?>? preview = null)
        {
            var box = new System.Windows.Controls.ComboBox { Foreground = System.Windows.Media.Brushes.Black };
            box.Items.Add("(none)");
            foreach (var (_, label) in options) box.Items.Add(label);
            box.SelectedIndex = 0;
            if (preview == null) { panel.Children.Add(box); return box; }

            var row = new System.Windows.Controls.DockPanel();
            var btn = new System.Windows.Controls.Button { Content = "▶", Width = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Preview" };
            System.Windows.Controls.DockPanel.SetDock(btn, System.Windows.Controls.Dock.Right);
            row.Children.Add(btn);
            row.Children.Add(box);
            panel.Children.Add(row);
            btn.Click += (_, _) => TogglePreview(btn, () =>
            { uint id = Picked(box, options); return id == 0 ? null : preview(id); });
            box.SelectionChanged += (_, _) => { if (playingBtn == btn) StopPreview(); };
            return box;
        }

        Label("Zone music (ZoneMusic.dbc)");
        var musicOpts = DbcOptions("ZoneMusic", 1);
        var musicBox = Combo(musicOpts, ZoneMusicPreviewFile);
        Label("Ambience (SoundAmbience.dbc)");
        var ambOpts = DbcOptions("SoundAmbience", -1);
        var ambBox = Combo(ambOpts);
        Label("Intro music (ZoneIntroMusicTable.dbc)");
        var introOpts = DbcOptions("ZoneIntroMusicTable", 1);
        var introBox = Combo(introOpts, IntroMusicPreviewFile);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After OK the zone is armed: click chunks (or Shift+drag a rectangle) to paint it. " +
                   "Export bundles the patched AreaTable.dbc for client and server.",
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });

        var ok = new System.Windows.Controls.Button
        { Content = "OK - create zone", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "New zone", Width = 380, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        ArmHotkeySwallow(nameBox, 'N');
        win.Loaded += (_, _) => nameBox.Focus();
        win.Closed += (_, _) => StopPreview();
        _viewport.ModalOpen = true;
        bool go;
        try { go = win.ShowDialog() == true; } finally { _viewport.ModalOpen = false; }
        if (!go || string.IsNullOrWhiteSpace(nameBox.Text)) return;

        _viewport.CreateZone(nameBox.Text,
            Picked(musicBox, musicOpts), Picked(ambBox, ambOpts), Picked(introBox, introOpts));
        _viewport.Focus();
    }

    private string? ZoneMusicPreviewFile(uint zoneMusicId) =>
        SoundEntriesFile(DbcField("ZoneMusic", zoneMusicId, 6));

    private string? IntroMusicPreviewFile(uint introId) =>
        SoundEntriesFile(DbcField("ZoneIntroMusicTable", introId, 2));

    private uint DbcField(string dbcName, uint id, int field)
    {
        if (_mpq == null) return 0;
        try
        {
            DbcFile d;
            using (var s = _mpq.OpenFile($@"DBFilesClient\{dbcName}.dbc")) d = DbcFile.Read(s);
            for (int i = 0; i < d.RecordCount; i++)
                if (d.GetU(i, 0) == id) return d.GetU(i, field);
        }
        catch { }
        return 0;
    }

    private string? SoundEntriesFile(uint soundId)
    {
        if (soundId == 0 || _mpq == null) return null;
        try
        {
            DbcFile d;
            using (var s = _mpq.OpenFile(@"DBFilesClient\SoundEntries.dbc")) d = DbcFile.Read(s);
            string Str(uint ofs)
            {
                if (ofs == 0 || ofs >= d.StringBlock.Length) return "";
                int e = (int)ofs; while (e < d.StringBlock.Length && d.StringBlock[e] != 0) e++;
                return System.Text.Encoding.UTF8.GetString(d.StringBlock, (int)ofs, e - (int)ofs);
            }
            for (int i = 0; i < d.RecordCount; i++)
            {
                if (d.GetU(i, 0) != soundId) continue;
                string dir = Str(d.GetU(i, 23)).TrimEnd('\\');
                for (int f = 3; f <= 12 && f < d.FieldCount; f++)
                {
                    string file = Str(d.GetU(i, f));
                    if (file.Length == 0) continue;
                    return dir.Length > 0 ? $@"{dir}\{file}" : file;
                }
                return null;
            }
        }
        catch { }
        return null;
    }

    private void ShowNewFlightPathWizard()
    {
        if (_viewport == null) return;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        void Label(string t) => panel.Children.Add(new System.Windows.Controls.TextBlock
        { Text = t, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2) });

        Label("Route name");
        var nameBox = new System.Windows.Controls.TextBox { Text = "" };
        panel.Children.Add(nameBox);

        Label("Route type");
        var typeBox = new System.Windows.Controls.ComboBox { Foreground = System.Windows.Media.Brushes.Black };
        typeBox.Items.Add("Taxi path (gryphon / wind rider)");
        typeBox.Items.Add("Zeppelin");
        typeBox.Items.Add("Boat");
        typeBox.SelectedIndex = 0;
        panel.Children.Add(typeBox);

        var flightPanel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(flightPanel);
        void FLabel(string t) => flightPanel.Children.Add(new System.Windows.Controls.TextBlock
        { Text = t, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2) });

        FLabel("Faction");
        var facBoth = new System.Windows.Controls.RadioButton
        { Content = "Both", IsChecked = true, Foreground = System.Windows.Media.Brushes.White, GroupName = "fpFac" };
        var facAlliance = new System.Windows.Controls.RadioButton
        { Content = "Alliance only", Foreground = System.Windows.Media.Brushes.White, GroupName = "fpFac", Margin = new Thickness(0, 2, 0, 0) };
        var facHorde = new System.Windows.Controls.RadioButton
        { Content = "Horde only", Foreground = System.Windows.Media.Brushes.White, GroupName = "fpFac", Margin = new Thickness(0, 2, 0, 0) };
        flightPanel.Children.Add(facBoth);
        flightPanel.Children.Add(facAlliance);
        flightPanel.Children.Add(facHorde);

        var bidi = new System.Windows.Controls.CheckBox
        {
            Content = "Bidirectional - also create the return route", IsChecked = true,
            Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 10, 0, 0),
        };
        flightPanel.Children.Add(bidi);

        FLabel("Return route name");
        var backBox = new System.Windows.Controls.TextBox { Text = "" };
        flightPanel.Children.Add(backBox);

        bool backEdited = false;
        backBox.TextChanged += (_, _) => { if (backBox.IsFocused) backEdited = true; };
        nameBox.TextChanged += (_, _) =>
        {
            if (backEdited) return;
            string n = nameBox.Text.Trim();
            var parts = n.Split(" - ");
            backBox.Text = n.Length == 0 ? "" : parts.Length == 2 ? $"{parts[1]} - {parts[0]}" : $"{n} Return";
        };
        bidi.Checked += (_, _) => backBox.IsEnabled = true;
        bidi.Unchecked += (_, _) => backBox.IsEnabled = false;

        FLabel("Cost (copper - 10000 = 1 gold)");
        var costBox = new System.Windows.Controls.TextBox { Text = "0" };
        flightPanel.Children.Add(costBox);

        var hint = new System.Windows.Controls.TextBlock
        {
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        panel.Children.Add(hint);

        const string FlightHint = "After OK: fly along the route and middle-click to drop a waypoint at the " +
                                  "camera position. Drag waypoints to adjust, PgUp/PgDn for altitude, Enter to finish. " +
                                  "Start or end near an existing flight point - island routes crash the client.";
        const string TransportHint = "After OK: middle-click to drop waypoints at the camera. Draw a CLOSED LOOP " +
                                     "(end near the start, at least 3 waypoints) - the vehicle circles it forever. " +
                                     "Middle-click a waypoint afterwards to set its dock pause. Deploy, then apply " +
                                     "deploy-sql\\transports.sql and restart mangosd.";
        hint.Text = FlightHint;
        typeBox.SelectionChanged += (_, _) =>
        {
            bool taxi = typeBox.SelectedIndex == 0;
            flightPanel.Visibility = taxi ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = taxi ? FlightHint : TransportHint;
        };

        var ok = new System.Windows.Controls.Button
        { Content = "OK - start drawing", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "New taxi route / transport", Width = 400, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        ArmHotkeySwallow(nameBox, 'N');
        win.Loaded += (_, _) => nameBox.Focus();
        _viewport.ModalOpen = true;
        bool go;
        try { go = win.ShowDialog() == true; } finally { _viewport.ModalOpen = false; }
        if (!go) return;

        if (typeBox.SelectedIndex != 0)
        {
            _viewport.StartFlightPathDraw(nameBox.Text, 0, 0, 0, twoWay: false,
                transportKind: typeBox.SelectedIndex == 1 ? "zeppelin" : "boat");
            _viewport.Focus();
            return;
        }

        uint mountHorde = facAlliance.IsChecked == true ? 0u : 3574u;
        uint mountAlliance = facHorde.IsChecked == true ? 0u : 541u;
        if (!uint.TryParse(costBox.Text.Trim(), out uint cost)) cost = 0;
        _viewport.StartFlightPathDraw(nameBox.Text, cost, mountHorde, mountAlliance,
            twoWay: bidi.IsChecked == true, returnName: backBox.Text);
        _viewport.Focus();
    }
}
