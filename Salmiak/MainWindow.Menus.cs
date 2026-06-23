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
    private void Minimap_Click(object sender, RoutedEventArgs e) => _viewport?.ToggleMinimap();

    private void BakeShadows_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null || _viewport.MapName == null) { StatusText.Text = "Bake shadows: open a terrain map first."; return; }

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        System.Windows.Controls.Slider MakeSlider(string label, double min, double max, double val, string unit)
        {
            var header = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2), Text = $"{label}: {val:F0}{unit}" };
            var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = val };
            sl.ValueChanged += (_, ev) => header.Text = $"{label}: {ev.NewValue:F0}{unit}";
            panel.Children.Add(header); panel.Children.Add(sl);
            return sl;
        }
        var azimuth = MakeSlider("Sun azimuth", 0, 360, 0, "°");
        var elevation = MakeSlider("Sun elevation", 10, 80, 30, "°");
        var maxDist = MakeSlider("Max shadow length", 20, 200, 130, " yd");

        var doodads = new System.Windows.Controls.CheckBox
        { Content = "Doodad shadows (trees cast crown blobs)", Foreground = System.Windows.Media.Brushes.White, IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(doodads);
        var wmos = new System.Windows.Controls.CheckBox
        { Content = "WMO shadows (buildings cast box shadows)", Foreground = System.Windows.Media.Brushes.White, IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(wmos);

        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Scope", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 10, 0, 2) });
        var scope = new System.Windows.Controls.ComboBox();
        scope.Items.Add("Edited chunks only (recommended)");
        scope.Items.Add("All loaded tiles (overwrites Blizzard shadows!)");
        scope.SelectedIndex = 0;
        panel.Children.Add(scope);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Replaces the baked shadow of every chunk in scope. One undo step - bake, judge, Ctrl+Z and retune freely. Touch up with the shadow brush (Ctrl+M) after.",
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });

        var ok = new System.Windows.Controls.Button { Content = "Bake", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Bake shadows", Width = 380, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.DimGray,
            Content = panel,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        StatusText.Text = "Baking shadows...";
        int n = _viewport.BakeShadows((float)azimuth.Value, (float)elevation.Value, (float)maxDist.Value,
                                      doodads.IsChecked == true, wmos.IsChecked == true, scope.SelectedIndex == 0);
        StatusText.Text = n > 0 ? $"Baked shadows for {n} chunk(s) - Ctrl+Z to undo and retune." : "Bake shadows: nothing in scope.";
    }

    private void SetWoWDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select WoW 1.12 Data folder",
            InitialDirectory = _wowDataPath
        };
        if (dlg.ShowDialog() == true)
            TryOpenDirectory(dlg.FolderName);
    }

    private void TryOpenDirectory(string path)
    {
        try
        {
            string? cur = _viewport?.MapName;
            var pose = _viewport?.CameraPose;

            _mpq?.Dispose();
            _mpq = MpqManager.OpenWoWDirectory(path);
            _wowDataPath = path;
            Properties.Settings.Default.WoWDataPath = path;
            Properties.Settings.Default.Save();

            _viewport?.SetMpq(_mpq);
            StatusText.Text = $"Opened {_mpq.DiagnosticLog.Split('\n').Count(l => l.StartsWith("OK"))} archives";
            PopulateMapList();

            if (cur != null)
            {
                var match = MapList.Items.OfType<MapEntry>()
                    .FirstOrDefault(me => string.Equals(me.Name, cur, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    _suppressMapSelect = true;
                    MapList.SelectedItem = match;
                    _suppressMapSelect = false;
                    LoadMap(match.Name);
                    if (pose != null && _viewport != null) _viewport.CameraPose = pose.Value;
                }
                else
                    StatusText.Text += $" - map '{cur}' not found in the new archive set.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void MpqArchives_Click(object sender, RoutedEventArgs e)
    {
        if (_mpq == null) { StatusText.Text = "Set your WoW directory first (File → Set WoW Directory)."; return; }

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Untick an archive to unload it (its files vanish from the editor; the current map reloads). " +
                   "Archives are listed in load order - later ones override earlier ones on conflict.",
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var listPanel = new System.Windows.Controls.StackPanel();
        var checks = new List<System.Windows.Controls.CheckBox>();
        foreach (var (name, enabled) in _mpq.ListArchives())
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = name, Tag = name, IsChecked = enabled,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1),
            };
            checks.Add(cb);
            listPanel.Children.Add(cb);
        }
        panel.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            Content = listPanel, MaxHeight = 320,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        });

        var apply = new System.Windows.Controls.Button { Content = "Apply", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
        panel.Children.Add(apply);

        var win = new Window
        {
            Title = "MPQ archives", Width = 360, Height = 460, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        apply.Click += (_, _) => win.DialogResult = true;
        if (_viewport != null) _viewport.ModalOpen = true;
        bool go;
        try { go = win.ShowDialog() == true; } finally { if (_viewport != null) _viewport.ModalOpen = false; }
        if (!go) return;

        bool changed = false;
        foreach (var cb in checks)
            if (cb.Tag is string name)
                changed |= _mpq.SetArchiveEnabled(name, cb.IsChecked == true);
        if (!changed) return;

        _allDoodadsCache = null; _allWmosCache = null;
        _doodadThumbs.Clear(); _wmoThumbs.Clear();
        _viewport?.SetMpq(_mpq);
        if (DoodadDrawer.Visibility == Visibility.Visible) _drawerRefresh?.Invoke();

        string? cur = (MapList.SelectedItem as MapEntry)?.Name;
        List<string>? maps;
        try
        {
            maps = _mpq.ListMaps();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}"; return;
        }

        MapList.Items.Clear();
        _mapIds = null;
        foreach (var m in maps) MapList.Items.Add(MakeMapEntry(m));
        var match = MapList.Items.OfType<MapEntry>()
            .FirstOrDefault(me => string.Equals(me.Name, cur, StringComparison.OrdinalIgnoreCase));
        int active = _mpq.ListArchives().Count(a => a.Enabled);
        if (match != null)
        {
            _suppressMapSelect = true;
            MapList.SelectedItem = match;
            _suppressMapSelect = false;
            LoadMap(match.Name);
        }
        else
            StatusText.Text = $"{active} archive(s) active" + (cur != null ? $" - map '{cur}' is no longer available." : ".");
    }

    private void OpenMap_Click(object sender, RoutedEventArgs e)
    {
        if (MapList.Items.Count > 0)
            MapList.SelectedIndex = 0;
        else
            SetWoWDirectory_Click(sender, e);
    }

    private void NewMap_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        if (_mpq == null) { StatusText.Text = "New map: set your WoW directory first (File → Set WoW Directory)."; return; }
        if (_viewport.EditedTileCount > 0 &&
            System.Windows.MessageBox.Show(this, "Creating a new map discards the current unsaved edits. Continue?",
                "New map", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        const string defaultTex = @"Tileset\Barrens\BarrensBaseGrass.blp";
        var textures = _viewport.AvailableGroundTextures();

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Create a new blank flat-terrain map you can sculpt, paint, and populate.",
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        System.Windows.Controls.TextBox Row(string label, string val, double width = 90)
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Width = 150, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            var tb = new System.Windows.Controls.TextBox { Text = val, Width = width, Padding = new Thickness(3) };
            row.Children.Add(tb); panel.Children.Add(row); return tb;
        }
        var nameBox = Row("Map name:", "CustomMap", 180);
        var wBox = Row("Width (tiles, 1–64):", "4");
        var hBox = Row("Height (tiles, 1–64):", "4");
        var zBox = Row("Base height (yd):", "0");

        panel.Children.Add(new System.Windows.Controls.TextBlock
        { Text = "Base texture:", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 6, 0, 2) });
        var texCombo = new System.Windows.Controls.ComboBox { MaxDropDownHeight = 240 };
        if (textures.Count > 0)
        {
            foreach (var t in textures) texCombo.Items.Add(t);
            int di = textures.FindIndex(t => string.Equals(t, defaultTex, StringComparison.OrdinalIgnoreCase));
            texCombo.SelectedIndex = di >= 0 ? di : 0;
        }
        else { texCombo.IsEditable = true; texCombo.Text = defaultTex; }
        panel.Children.Add(texCombo);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Tiles are 533×533 yd, centred on the world grid. Persist later via Project → Save Edits.",
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        var ok = new System.Windows.Controls.Button { Content = "Create map", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "New map", Width = 400, Height = 380, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        string name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusText.Text = "New map: enter a name."; return; }
        if (!int.TryParse(wBox.Text.Trim(), out int w) || !int.TryParse(hBox.Text.Trim(), out int h) || w < 1 || h < 1)
        { StatusText.Text = "New map: width/height must be positive integers."; return; }
        if (w > 64 || h > 64) { StatusText.Text = "New map: max 64 tiles per side."; return; }
        if (w * h > 1024) { StatusText.Text = $"New map: {w}×{h} = {w * h} tiles is too large (max 1024)."; return; }
        if (!float.TryParse(zBox.Text.Trim(), out float z)) z = 0f;
        string tex = (texCombo.IsEditable ? texCombo.Text : texCombo.SelectedItem as string) ?? defaultTex;
        if (string.IsNullOrWhiteSpace(tex)) tex = defaultTex;

        if (_viewport.CreateBlankMap(name, w, h, z, tex))
        {
            var entry = MapList.Items.OfType<MapEntry>()
                            .FirstOrDefault(me => string.Equals(me.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? new MapEntry(name, null);
            if (!MapList.Items.Contains(entry)) MapList.Items.Add(entry);
            _suppressMapSelect = true;
            MapList.SelectedItem = entry;
            _suppressMapSelect = false;
            StatusText.Text = $"Created '{name}' ({w}×{h} tiles). Sculpt/paint/place, then Project → Save Edits.";
        }
    }

    private static int ContinentId(string? map) => map?.ToLowerInvariant() switch
    {
        "azeroth" => 0,
        "kalimdor" => 1,
        _ => -1,
    };

    private void AddTile_Click(object sender, RoutedEventArgs e) => _viewport?.AddTileAtCamera();
    private void FlightPaths_Click(object sender, RoutedEventArgs e)
    {
        bool on = _viewport?.ToggleFlightPaths() ?? false;
        FlightPathsMenu.IsChecked = on;
    }

    private void ZoneLighting_Click(object sender, RoutedEventArgs e)
    {
        if (_mpq == null) { StatusText.Text = "Set your WoW directory first."; return; }
        int continent = ContinentId(_viewport?.MapName);
        if (continent < 0) { StatusText.Text = "Zone lighting: load Azeroth or Kalimdor first."; return; }

        Salmiak.Core.Formats.ZoneLighting zl;
        try { zl = Salmiak.Core.Formats.ZoneLighting.Load(p => _mpq.OpenFile(p)); }
        catch (Exception ex) { StatusText.Text = $"Zone lighting: {ex.Message}"; return; }

        var zones = zl.ListZones(continent);
        zones.Sort((a, b) => a.IsGlobal != b.IsGlobal ? (a.IsGlobal ? -1 : 1)
            : a.TileX0 != b.TileX0 ? a.TileX0.CompareTo(b.TileX0) : a.TileY0.CompareTo(b.TileY0));

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"{(continent == 0 ? "Eastern Kingdoms" : "Kalimdor")} light zones - tick zones to force always-night " +
                   "(repoints them to Duskwood's lighting). Each light is a sphere; the tile range is " +
                   "everything its outer falloff reaches. Tiles in no listed range use the 'whole map' default light. " +
                   "Duskwood ≈ tiles (36–39, 50–51).",
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var listPanel = new System.Windows.Controls.StackPanel();
        var checks = new List<System.Windows.Controls.CheckBox>();
        foreach (var z in zones)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = $"id {z.Id,-4} {z.TileSpan,-22}   ambient {z.AmbientLum,3:F0}" +
                          (z.IsNight ? "   (already night)" : ""),
                Foreground = z.IsNight ? System.Windows.Media.Brushes.Gold : System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1), Tag = z.RecIndex, IsChecked = z.IsNight,
            };
            checks.Add(cb);
            listPanel.Children.Add(cb);
        }
        panel.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            Content = listPanel, Height = 360,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        });

        var darkHeader = new System.Windows.Controls.TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 10, 0, 2),
            Text = "Darkness: 1.00  (1 = Duskwood, lower = darker)",
        };
        var darkSlider = new System.Windows.Controls.Slider { Minimum = 0.1, Maximum = 1.0, Value = 1.0 };
        darkSlider.ValueChanged += (_, ev) =>
            darkHeader.Text = $"Darkness: {ev.NewValue:F2}  ({(ev.NewValue >= 0.999 ? "Duskwood" : "darker than Duskwood")})";
        panel.Children.Add(darkHeader);
        panel.Children.Add(darkSlider);

        var export = new System.Windows.Controls.Button { Content = "Export night patch MPQ...", Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(export);

        var win = new Window
        {
            Title = "Zone lighting", Width = 520, Height = 560, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        export.Click += (_, _) => win.DialogResult = true;
        if (_viewport != null) _viewport.ModalOpen = true;
        bool go;
        try { go = win.ShowDialog() == true; } finally { if (_viewport != null) _viewport.ModalOpen = false; }
        if (!go) return;

        float darkness = (float)darkSlider.Value;
        int n = 0;
        foreach (var cb in checks)
            if (cb.IsChecked == true && cb.Tag is int idx) { zl.MakeNight(idx, darkness); n++; }
        if (n == 0) { StatusText.Text = "Zone lighting: no zones selected."; return; }

        var patch = zl.BuildPatchFiles();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "MPQ patch (*.mpq)|*.mpq|All files (*.*)|*.*",
            DefaultExt = ".mpq", FileName = "patch-night.mpq",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Salmiak.Core.IO.MpqArchiveWriter.Write(dlg.FileName, patch);
            StatusText.Text = $"Night patch written ({n} zone(s)): {dlg.FileName} - drop into your WoW Data folder.";
        }
        catch (Exception ex) { StatusText.Text = $"Export failed: {ex.Message}"; }
    }

    private const string ProjectFilter = "WoW Map Editor project (*.wmproj)|*.wmproj|All files (*.*)|*.*";

    private void SaveEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        if (_viewport.EditedTileCount == 0 && !_viewport.HasExportableEdits &&
            _viewport.FlightEditCount == 0 && _viewport.NewZoneCount == 0)
        {
            StatusText.Text = "No edits to save.";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = ProjectFilter,
            DefaultExt = ".wmproj",
            FileName = $"{_viewport.MapName ?? "edits"}.wmproj",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _viewport.SaveEdits(dlg.FileName);
            StatusText.Text = $"Saved {_viewport.EditedTileCount} tile(s), {_viewport.FlightEditCount} route(s), " +
                              $"{_viewport.NewZoneCount} zone(s) -> {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = ProjectFilter, DefaultExt = ".wmproj" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            string savedMap = _viewport.LoadEdits(dlg.FileName);
            _allWmosCache = null;
            if (!string.IsNullOrEmpty(savedMap) && _viewport.MapName != null &&
                !string.Equals(savedMap, _viewport.MapName, StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show(this,
                    $"These edits were saved for map '{savedMap}', but '{_viewport.MapName}' is loaded.\n" +
                    "Open that map to see them in place.",
                    "Map mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            StatusText.Text = $"Loaded {_viewport.EditedTileCount} edited tile(s) from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportServerMaps_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        var dlg = new OpenFolderDialog { Title = "Pick the server's maps folder (e.g. mangos\\build\\bin\\x64_Debug\\maps)" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = _viewport.ExportServerMaps(dlg.FolderName);
            StatusText.Text = $"Wrote {n} server .map file(s) → {dlg.FolderName} - restart mangosd to pick them up.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Server map export failed: {ex.Message}";
        }
    }

    private void ExportMpq_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        if (!_viewport.HasExportableEdits)
        {
            StatusText.Text = "No edits to export.";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export edits to MPQ patch",
            Filter = "MPQ archive (*.mpq)|*.mpq|All files (*.*)|*.*",
            DefaultExt = ".mpq",
            FileName = $"patch-{_viewport.MapName ?? "edits"}.mpq",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            int n = _viewport.ExportMpq(dlg.FileName);
            StatusText.Text = $"Exported {n} file(s) -> {System.IO.Path.GetFileName(dlg.FileName)}  (drop into WoW\\Data)";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportFlightPaths_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        int edits = _viewport.FlightEditCount;
        if (edits == 0)
        {
            StatusText.Text = "Flight paths: no edits to export (enter flight mode, edit a path, then export).";
            return;
        }
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose an output folder for the flight/transport export",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = _viewport.ExportFlightPaths(dlg.FolderName);
            StatusText.Text = $"Exported {n} edited/new path(s) to {dlg.FolderName} - see README.txt for where each file goes.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Flight-path export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MergeMpq_Click(object sender, RoutedEventArgs e)
    {
        var list = new System.Windows.Controls.ListBox { Margin = new Thickness(0, 0, 0, 6), MinHeight = 160 };

        void AddFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add MPQ files to merge",
                Filter = "MPQ archives (*.mpq)|*.mpq|All files (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames)
                if (!list.Items.Contains(f)) list.Items.Add(f);
        }

        var addBtn = new System.Windows.Controls.Button { Content = "Add MPQ files...", Margin = new Thickness(0, 0, 0, 4) };
        addBtn.Click += (_, _) => AddFiles();
        var removeBtn = new System.Windows.Controls.Button { Content = "Remove selected", Margin = new Thickness(0, 0, 0, 4) };
        removeBtn.Click += (_, _) => { if (list.SelectedItem != null) list.Items.Remove(list.SelectedItem); };
        var mergeBtn = new System.Windows.Controls.Button { Content = "Merge...", Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.Bold };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Pick two or more MPQ patches to combine into one (e.g. teammates' exports).\nLater files override earlier ones on conflict.",
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(addBtn);
        panel.Children.Add(list);
        panel.Children.Add(removeBtn);
        panel.Children.Add(mergeBtn);

        var win = new Window
        {
            Title = "Merge MPQs", Width = 520, Height = 420, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };

        mergeBtn.Click += (_, _) =>
        {
            if (list.Items.Count < 2) { System.Windows.MessageBox.Show(win, "Pick at least two MPQ files.", "Merge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save merged MPQ",
                Filter = "MPQ archive (*.mpq)|*.mpq|All files (*.*)|*.*",
                DefaultExt = ".mpq",
                FileName = "patch-merged.mpq",
            };
            if (save.ShowDialog(win) != true) return;
            var sources = list.Items.Cast<string>().ToList();
            try
            {
                var (count, conflicts) = MpqArchiveWriter.Merge(sources, save.FileName);
                string msg = $"Merged {sources.Count} archives → {count} file(s) into {System.IO.Path.GetFileName(save.FileName)}.";
                if (conflicts.Count > 0) msg += $"\n\n{conflicts.Count} conflicting file(s) (later source kept):\n" + string.Join("\n", conflicts.Take(15)) + (conflicts.Count > 15 ? "\n..." : "");
                System.Windows.MessageBox.Show(win, msg, "Merge complete", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = $"Merged {count} file(s) -> {System.IO.Path.GetFileName(save.FileName)}";
                win.Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(win, ex.Message, "Merge failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        win.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
