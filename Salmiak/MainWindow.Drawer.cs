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
    private readonly Dictionary<string, System.Windows.Media.ImageSource?> _doodadThumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Windows.Media.ImageSource?> _wmoThumbs = new(StringComparer.OrdinalIgnoreCase);

    private enum DrawerKind { None, Doodad, Wmo, Texture }

    private DrawerKind _drawerKind = DrawerKind.None;
    private System.Windows.Controls.TextBox? _drawerSearch;

    private void BuildDoodadDrawer() => BuildAssetDrawer(DrawerKind.Doodad);

    private void BuildAssetDrawer(DrawerKind kind)
    {
        const int MaxRows = 500;
        bool isTexture = kind == DrawerKind.Texture;
        bool isWmo = kind == DrawerKind.Wmo;
        var loaded = new List<string>();

        var header = new System.Windows.Controls.TextBlock
        {
            Text = kind switch
            {
                DrawerKind.Wmo => "WMOs - click to arm, Shift+click = 3D preview, right-click = favorite",
                DrawerKind.Texture => "Textures - click to arm the paint brush, right-click = favorite",
                _ => "Doodads - click to arm, Shift+click = 3D preview, right-click = favorite / export OBJ",
            },
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11,
            Margin = new Thickness(6, 6, 6, 2), TextWrapping = TextWrapping.Wrap,
        };
        var search = new System.Windows.Controls.TextBox { Margin = new Thickness(4) };
        var source = new System.Windows.Controls.ComboBox { Margin = new Thickness(4, 0, 4, 4) };
        source.Items.Add("Loaded tiles");
        source.Items.Add(isTexture ? "All tileset textures" : "All models");
        source.Items.Add("Favorites");
        source.SelectedIndex = 0;
        var info = new System.Windows.Controls.TextBlock
        {
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 10, Margin = new Thickness(6, 0, 0, 4),
        };

        var list = new System.Windows.Controls.ListBox
        {
            Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
        };
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(list, false);
        var wrapFactory = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.WrapPanel));
        wrapFactory.SetValue(System.Windows.Controls.WrapPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        list.ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(wrapFactory);
        System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(list, System.Windows.Controls.ScrollBarVisibility.Disabled);

        var rows = new List<(System.Windows.Controls.ListBoxItem Item, System.Windows.Controls.Image Img, string Path)>();

        List<string> SourceList() => source.SelectedIndex switch
        {
            1 => isTexture ? AllTextures() : AllModels(isWmo),
            2 => FavList(kind),
            _ => loaded,
        };

        void Arm(string path)
        {
            if (_viewport == null) return;
            switch (kind)
            {
                case DrawerKind.Wmo: _viewport.PlaceWmo = path; break;
                case DrawerKind.Texture: _viewport.PaintTexture = path; break;
                default: _viewport.PlaceDoodad = path; break;
            }
            _viewport.Focus();
        }

        System.Windows.Media.ImageSource? Thumb(string path) =>
            isTexture ? GetTextureThumb(path) : GetModelThumb(path, isWmo);

        void Populate()
        {
            string filter = search.Text.Trim();
            list.Items.Clear();
            rows.Clear();
            int shown = 0, matched = 0;
            foreach (var p in OrderedModelMatches(SourceList(), filter, wmo: isWmo || isTexture))
            {
                matched++;
                if (shown >= MaxRows) continue;
                shown++;

                string capture = p;
                var img = new System.Windows.Controls.Image
                { Width = 64, Height = 64, Stretch = System.Windows.Media.Stretch.Uniform };
                var tile = new System.Windows.Controls.Border
                {
                    Child = img, Padding = new Thickness(2), Margin = new Thickness(2),
                    BorderBrush = System.Windows.Media.Brushes.Gold,
                    BorderThickness = new Thickness(IsFav(kind, p) ? 2 : 0),
                };
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Content = tile, Tag = p, Padding = new Thickness(0),
                    ToolTip = System.IO.Path.GetFileNameWithoutExtension(p),
                };
                item.PreviewMouseLeftButtonUp += (_, _) =>
                {
                    if (!isTexture && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) ShowSpinPreview(capture, isWmo);
                    else Arm(capture);
                };
                void ToggleFavTile()
                {
                    ToggleFav(kind, capture);
                    bool fav = IsFav(kind, capture);
                    tile.BorderThickness = new Thickness(fav ? 2 : 0);
                    if (source.SelectedIndex == 2 && !fav) Populate();
                }
                if (kind == DrawerKind.Doodad)
                {
                    var menu = new System.Windows.Controls.ContextMenu();
                    var favItem = new System.Windows.Controls.MenuItem { Header = "Toggle favorite" };
                    favItem.Click += (_, _) => ToggleFavTile();
                    var expItem = new System.Windows.Controls.MenuItem { Header = "Export to OBJ (Blender)..." };
                    expItem.Click += (_, _) => ExportDoodadObj(capture);
                    menu.Items.Add(favItem);
                    menu.Items.Add(expItem);
                    item.ContextMenu = menu;
                }
                else
                    item.MouseRightButtonUp += (_, _) => ToggleFavTile();
                list.Items.Add(item);
                rows.Add((item, img, p));
            }
            info.Text = matched > shown
                ? $"Showing {shown} of {matched} matches - type to narrow."
                : $"{matched} {(isTexture ? "texture(s)" : "model(s)")}";
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
                var src = Thumb(path);
                if (src != null) img.Source = src;
            }
        }
        void ScheduleGenerate() => Dispatcher.BeginInvoke(GenerateVisible, System.Windows.Threading.DispatcherPriority.Background);

        search.TextChanged += (_, _) => { Populate(); ScheduleGenerate(); };
        source.SelectionChanged += (_, _) => { Populate(); ScheduleGenerate(); };
        list.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler((_, _) => ScheduleGenerate()));

        list.PreviewMouseWheel += (_, _) => CloseSpinPreview();
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var d = e.OriginalSource as System.Windows.DependencyObject;
            while (d != null && d is not System.Windows.Controls.ListBoxItem)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            if (d == null) CloseSpinPreview();
        };

        search.GotKeyboardFocus  += (_, _) => { if (_viewport != null) _viewport.ModalOpen = true; };
        search.LostKeyboardFocus += (_, _) => { if (_viewport != null) _viewport.ModalOpen = false; };
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) _viewport?.Focus(); };

        var dock = new System.Windows.Controls.DockPanel();
        foreach (var top in new System.Windows.UIElement[] { header, search, source, info })
        {
            System.Windows.Controls.DockPanel.SetDock(top, System.Windows.Controls.Dock.Top);
            dock.Children.Add(top);
        }
        if (isTexture)
        {
            var clear = new System.Windows.Controls.Button
            { Content = "Clear armed texture (sculpt-safe)", Margin = new Thickness(4, 0, 4, 4) };
            clear.Click += (_, _) => { if (_viewport != null) { _viewport.PaintTexture = null; _viewport.Focus(); } };
            System.Windows.Controls.DockPanel.SetDock(clear, System.Windows.Controls.Dock.Top);
            dock.Children.Add(clear);
        }
        dock.Children.Add(list);
        DoodadDrawer.Child = dock;

        _drawerKind = kind;
        _drawerSearch = search;
        _drawerRefresh = () =>
        {
            loaded.Clear();
            if (_viewport != null)
                loaded.AddRange(kind switch
                {
                    DrawerKind.Wmo => _viewport.AvailableWmos(),
                    DrawerKind.Texture => _viewport.LoadedTextures(),
                    _ => _viewport.AvailableDoodads(),
                });
            Populate();
            ScheduleGenerate();
        };
    }

    private void ExportDoodadObj(string modelPath)
    {
        if (_mpq == null) { StatusText.Text = "Export OBJ: open a WoW directory first."; return; }
        string m2Path = System.IO.Path.ChangeExtension(modelPath, ".m2");
        M2File? m2;
        try { using var s = _mpq.OpenFile(m2Path); m2 = M2File.Parse(s); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Export OBJ", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (m2 == null || m2.Indices.Length == 0)
        { System.Windows.MessageBox.Show(this, $"Couldn't parse model geometry:\n{m2Path}", "Export OBJ", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        string stem = System.IO.Path.GetFileNameWithoutExtension(m2Path);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export doodad to OBJ (Blender)",
            Filter = "Wavefront OBJ (*.obj)|*.obj",
            DefaultExt = ".obj",
            FileName = stem + ".obj",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var warnings = M2ObjExporter.Export(m2, dlg.FileName, tex =>
            { try { return _mpq.OpenFile(tex.Replace('/', '\\')); } catch { return null; } });
            string msg = $"Exported {stem}.obj (+ .mtl + textures) to {System.IO.Path.GetDirectoryName(dlg.FileName)}.\n" +
                         "In Blender: File → Import → Wavefront (.obj), default axis settings.";
            if (warnings.Count > 0)
                msg += $"\n\n{warnings.Count} texture warning(s):\n" + string.Join("\n", warnings.Take(8)) +
                       (warnings.Count > 8 ? "\n..." : "");
            System.Windows.MessageBox.Show(this, msg, "Export OBJ complete", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = $"Exported {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export OBJ failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateDoodadDrawer()
    {
        var kind = _viewport?.CurrentTool switch
        {
            GlViewport.Tool.Doodad => DrawerKind.Doodad,
            GlViewport.Tool.Wmo => DrawerKind.Wmo,
            GlViewport.Tool.Texture => DrawerKind.Texture,
            _ => DrawerKind.None,
        };
        if (kind != DrawerKind.None)
        {
            bool rebuilt = kind != _drawerKind;
            if (rebuilt) { CloseSpinPreview(); BuildAssetDrawer(kind); }
            if (DoodadDrawer.Visibility != Visibility.Visible)
            {
                SidePanel.Visibility = Visibility.Collapsed;
                DoodadDrawer.Visibility = Visibility.Visible;
                _drawerRefresh?.Invoke();
            }
            else if (rebuilt) _drawerRefresh?.Invoke();
        }
        else if (DoodadDrawer.Visibility == Visibility.Visible)
        {
            CloseSpinPreview();
            DoodadDrawer.Visibility = Visibility.Collapsed;
            SidePanel.Visibility = Visibility.Visible;
        }
    }

    private System.Windows.Controls.Primitives.Popup? _spinPopup;
    private System.Windows.Controls.Image? _spinImage;
    private System.Windows.Controls.TextBlock? _spinLabel;
    private System.Windows.Media.Imaging.WriteableBitmap? _spinBmp;
    private System.Windows.Threading.DispatcherTimer? _spinTimer;
    private byte[]? _spinBgra;
    private string? _spinPath;
    private float _spinYaw;
    private const int SpinSize = 256;

    private string? _spinJustClosedPath;
    private int _spinJustClosedAt;
    private bool _spinIsWmo;

    private void ShowSpinPreview(string path, bool isWmo = false)
    {
        if (_viewport == null) return;
        _spinIsWmo = isWmo;

        if (_spinPopup?.IsOpen == true && string.Equals(path, _spinPath, StringComparison.OrdinalIgnoreCase))
        {
            CloseSpinPreview();
            return;
        }
        if (string.Equals(path, _spinJustClosedPath, StringComparison.OrdinalIgnoreCase) &&
            Environment.TickCount - _spinJustClosedAt < 500)
        {
            _spinJustClosedPath = null;
            return;
        }
        if (_spinPopup == null)
        {
            _spinBmp = new System.Windows.Media.Imaging.WriteableBitmap(
                SpinSize, SpinSize, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _spinBgra = new byte[SpinSize * SpinSize * 4];
            _spinImage = new System.Windows.Controls.Image { Width = SpinSize, Height = SpinSize, Source = _spinBmp };
            _spinLabel = new System.Windows.Controls.TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White, FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
            };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(_spinImage);
            panel.Children.Add(_spinLabel);
            var border = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xee, 0x1b, 0x1b, 0x22)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4a, 0x4a, 0x5a)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6), Child = panel,
            };
            border.MouseDown += (_, _) => CloseSpinPreview();
            _spinPopup = new System.Windows.Controls.Primitives.Popup
            {
                AllowsTransparency = true, StaysOpen = false,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
                Child = border,
            };
            _spinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _spinTimer.Tick += (_, _) => SpinTick();
            _spinPopup.Closed += (_, _) =>
            {
                _spinTimer.Stop();
                _spinJustClosedPath = _spinPath;
                _spinJustClosedAt = Environment.TickCount;
            };
        }

        _spinPath = path;
        _spinYaw = 0f;
        _spinLabel!.Text = System.IO.Path.GetFileNameWithoutExtension(path);
        _spinPopup.IsOpen = false;
        _spinPopup.IsOpen = true;
        SpinTick();
        _spinTimer!.Start();
    }

    private void SpinTick()
    {
        if (_spinPath == null || _viewport == null || _spinBmp == null || _spinBgra == null) return;
        _spinYaw = (_spinYaw + 2.4f) % 360f;
        var px = _spinIsWmo
            ? _viewport.RenderWmoThumbnail(_spinPath, SpinSize, _spinYaw)
            : _viewport.RenderDoodadThumbnail(_spinPath, SpinSize, _spinYaw);
        if (px == null || px.Length != SpinSize * SpinSize * 4) return;
        for (int y = 0; y < SpinSize; y++)
        for (int x = 0; x < SpinSize; x++)
        {
            int s = ((SpinSize - 1 - y) * SpinSize + x) * 4;
            int d = (y * SpinSize + x) * 4;
            _spinBgra[d] = px[s + 2]; _spinBgra[d + 1] = px[s + 1]; _spinBgra[d + 2] = px[s]; _spinBgra[d + 3] = px[s + 3];
        }
        _spinBmp.WritePixels(new Int32Rect(0, 0, SpinSize, SpinSize), _spinBgra, SpinSize * 4, 0);
    }

    private void CloseSpinPreview()
    {
        if (_spinPopup != null) _spinPopup.IsOpen = false;
    }

    private void FocusDoodadDrawer()
    {
        UpdateDoodadDrawer();
        if (DoodadDrawer.Visibility != Visibility.Visible || _drawerSearch == null) return;
        ArmHotkeySwallow(_drawerSearch, 'N');
        _drawerSearch.Focus();
        _drawerSearch.SelectAll();
    }

    private List<string>? _allDoodadsCache, _allWmosCache, _allTexturesCache;
    private readonly Dictionary<string, System.Windows.Media.ImageSource?> _textureThumbs = new(StringComparer.OrdinalIgnoreCase);

    private List<string> AllModels(bool isWmo)
    {
        if (isWmo) return _allWmosCache ??= _mpq?.ListModels(true) ?? new List<string>();
        return _allDoodadsCache ??= _mpq?.ListModels(false) ?? new List<string>();
    }

    private List<string> AllTextures() => _allTexturesCache ??= _mpq?.ListTextures() ?? new List<string>();

    private System.Windows.Media.ImageSource? GetTextureThumb(string path)
    {
        if (_textureThumbs.TryGetValue(path, out var cached)) return cached;
        return _textureThumbs[path] = DecodeThumbnail(path);
    }

    private static List<string> FavList(bool isWmo) => FavList(isWmo ? DrawerKind.Wmo : DrawerKind.Doodad);

    private static List<string> FavList(DrawerKind kind) => kind switch
    {
        DrawerKind.Wmo => Properties.Settings.Default.FavoriteWmos,
        DrawerKind.Texture => Properties.Settings.Default.FavoriteTextures,
        _ => Properties.Settings.Default.FavoriteDoodads,
    };

    private static bool IsFav(bool isWmo, string p) => IsFav(isWmo ? DrawerKind.Wmo : DrawerKind.Doodad, p);

    private static bool IsFav(DrawerKind kind, string p) =>
        FavList(kind).Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));

    private static void ToggleFav(DrawerKind kind, string p)
    {
        var favs = FavList(kind);
        int idx = favs.FindIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) favs.RemoveAt(idx); else favs.Add(p);
        Properties.Settings.Default.Save();
    }

    private static List<string> OrderedModelMatches(List<string> src, string filter, bool wmo)
    {
        if (filter.Length == 0) return src;
        if (wmo)
        {
            var r = new List<string>();
            foreach (var p in src) if (p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) r.Add(p);
            return r;
        }
        string ql = filter.ToLowerInvariant();
        var scored = new List<(string Path, int Key)>();
        foreach (var p in src)
        {
            int rank = Salmiak.Core.Formats.DoodadCategories.Rank(p, ql);
            if (rank == 0) continue;
            bool world = p.StartsWith("World\\", StringComparison.OrdinalIgnoreCase);
            scored.Add((p, rank * 2 + (world ? 1 : 0)));
        }
        scored.Sort((a, b) => a.Key != b.Key ? b.Key.CompareTo(a.Key)
            : string.Compare(System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(b.Path), StringComparison.OrdinalIgnoreCase));
        var res = new List<string>(scored.Count);
        foreach (var s in scored) res.Add(s.Path);
        return res;
    }

    private System.Windows.Media.ImageSource? GetModelThumb(string path, bool isWmo)
    {
        var cache = isWmo ? _wmoThumbs : _doodadThumbs;
        if (cache.TryGetValue(path, out var cached)) return cached;
        System.Windows.Media.ImageSource? src = null;
        const int sz = 64;
        var px = isWmo ? _viewport?.RenderWmoThumbnail(path, sz) : _viewport?.RenderDoodadThumbnail(path, sz);
        if (px != null && px.Length == sz * sz * 4)
        {
            var bgra = new byte[px.Length];
            for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                int s = ((sz - 1 - y) * sz + x) * 4;
                int d = (y * sz + x) * 4;
                bgra[d] = px[s + 2]; bgra[d + 1] = px[s + 1]; bgra[d + 2] = px[s]; bgra[d + 3] = px[s + 3];
            }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                sz, sz, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, sz * 4);
            bmp.Freeze();
            src = bmp;
        }
        cache[path] = src;
        return src;
    }

    private System.Windows.Media.ImageSource? DecodeThumbnail(string path)
    {
        try
        {
            using var s = _mpq!.OpenFile(path);
            var (rgba, w, h) = BlpReader.Decode(s);
            var bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3];
            }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
