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

public partial class MainWindow : Window
{
    private GlViewport? _viewport;
    private MpqManager? _mpq;
    private string _wowDataPath = string.Empty;

    public static string AppVersion
    {
        get { var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; return v == null ? "" : $"{v.Major}.{v.Minor}"; }
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Salmiak {AppVersion}";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewport = new GlViewport();
        GlHost.Child = _viewport;
        _viewport.CameraUpdated += pos =>
            Dispatcher.Invoke(() => CamText.Text = $"X:{pos.X:F0} Y:{pos.Y:F0} Z:{pos.Z:F0}");
        _viewport.DebugModeChanged += name =>
            Dispatcher.Invoke(() => DebugText.Text = $"View: {name}  (G)");
        _viewport.TileCountChanged += n =>
            Dispatcher.Invoke(() => StatusText.Text = $"{n} tiles loaded");
        _viewport.LoadingChanged += loading =>
            Dispatcher.Invoke(() => LoadingText.Visibility =
                loading ? Visibility.Visible : Visibility.Collapsed);
        _viewport.HudChanged += UpdateHud;
        _viewport.ModeChanged += _ => Dispatcher.Invoke(RefreshToolbar);
        _viewport.HelpToggled += () => Dispatcher.Invoke(ToggleHelp);
        _viewport.BrushRadiusChanged += _ => Dispatcher.Invoke(RefreshToolbar);
        _viewport.OpenDoodadPalette += () => Dispatcher.BeginInvoke(FocusDoodadDrawer);
        _viewport.OpenWmoPalette += () => Dispatcher.BeginInvoke(FocusDoodadDrawer);
        _viewport.OpenFlightPathWizard += () => Dispatcher.BeginInvoke(ShowNewFlightPathWizard);
        _viewport.NpcModeEntered += () => Dispatcher.BeginInvoke(LoadNpcSpawns);
        _viewport.HerbModeEntered += () => Dispatcher.BeginInvoke(LoadHerbSpawns);
        _viewport.OpenNewZoneWizard += () => Dispatcher.BeginInvoke(ShowNewZoneWizard);
        _viewport.NpcSelected += s => Dispatcher.BeginInvoke(() => LoadNpcPatrol(s));

        _viewport.SaveRequested += () => Dispatcher.BeginInvoke(() => SaveEdits_Click(this, new RoutedEventArgs()));
        _viewport.LoadRequested += () => Dispatcher.BeginInvoke(() => LoadEdits_Click(this, new RoutedEventArgs()));
        _viewport.CameraSpeedChanged += s => Dispatcher.Invoke(() => SpeedText.Text = $"Speed: {s:F0}");
        _viewport.FlightPathsChanged += on => Dispatcher.Invoke(() => FlightPathsMenu.IsChecked = on);
        _viewport.Notify += msg => Dispatcher.Invoke(() => StatusText.Text = msg);
        _viewport.OpenGeneratePicker += () => Dispatcher.BeginInvoke(ShowGeneratePicker);
        _viewport.OpenAreaPicker += () => Dispatcher.BeginInvoke(ShowAreaIdPicker);
        _viewport.OpenRoadPicker += () => Dispatcher.BeginInvoke(ShowRoadPicker);
        _viewport.OpenAutoPaintPicker += () => Dispatcher.BeginInvoke(ShowAutoPaintPicker);
        _viewport.OpenRiverPicker += () => Dispatcher.BeginInvoke(ShowRiverPicker);
        _viewport.OpenLiquidPicker += () => Dispatcher.BeginInvoke(ShowLiquidPicker);
        _viewport.OpenTexturePicker += () => Dispatcher.BeginInvoke(FocusDoodadDrawer);
        _viewport.OpenBlendSettings += () => Dispatcher.BeginInvoke(ShowBlendSettings);

        BuildToolbar();
        BuildDoodadDrawer();
        SpeedText.Text = $"Speed: {_viewport.CameraSpeed:F0}";

        var saved = Properties.Settings.Default.WoWDataPath;
        if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
            TryOpenDirectory(saved);
    }

    private string? _hudPaintShown;

    private void UpdateHud(GlViewport.HudState hud)
    {
        HudLoadingWash.Visibility = hud.Loading ? Visibility.Visible : Visibility.Collapsed;
        HudLoading.Visibility = hud.Loading ? Visibility.Visible : Visibility.Collapsed;

        HudBanner.Visibility = hud.Banner.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hud.Banner.Length > 0)
        {
            HudBanner.Text = hud.Banner;
            HudBanner.Foreground = new System.Windows.Media.SolidColorBrush(hud.BannerColor);
        }
        HudBannerSub.Visibility = hud.BannerSub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hud.BannerSub.Length > 0)
        {
            HudBannerSub.Text = hud.BannerSub;
            HudBannerSub.Foreground = new System.Windows.Media.SolidColorBrush(hud.BannerSubColor);
        }

        HudLayers.Visibility = hud.LayerInfo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hud.LayerInfo.Length > 0) HudLayers.Text = hud.LayerInfo;

        HudPaint.Visibility = hud.PaintTexturePath != null ? Visibility.Visible : Visibility.Collapsed;
        if (hud.PaintTexturePath != null)
        {
            HudPaintName.Text = hud.PaintName;
            HudPaintSub.Text = hud.PaintSub;
            if (!string.Equals(_hudPaintShown, hud.PaintTexturePath, StringComparison.OrdinalIgnoreCase))
            {
                _hudPaintShown = hud.PaintTexturePath;
                HudPaintImg.Source = GetTextureThumb(hud.PaintTexturePath);
            }
        }

        HudCoords.Visibility = hud.Coords.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hud.Coords.Length > 0) HudCoords.Text = hud.Coords;

        HudHelp.Visibility = hud.Help.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hud.Help.Length > 0) HudHelp.Text = hud.Help;

        HudStats.Text = hud.Stats;
    }

    private Action? _drawerRefresh;

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _viewport?.Dispose();
        _mpq?.Dispose();
    }
}
