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
    private void ImportHeightmap_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null || _viewport.MapName == null)
        { StatusText.Text = "Import heightmap: open a terrain map first."; return; }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import heightmap image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        float[] lum; int iw, ih;
        try { lum = LoadLuminance(dlg.FileName, out iw, out ih); }
        catch (Exception ex)
        { System.Windows.MessageBox.Show(this, ex.Message, "Image load failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        _viewport.LoadedHeightRange(out float curMin, out float curMax);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Image {iw}×{ih}px - stretched across a rectangle you'll mark on the terrain.\nBlack = min height, white = max height.",
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        System.Windows.Controls.TextBox NumRow(string label, double val)
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Width = 120, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            var tb = new System.Windows.Controls.TextBox { Text = val.ToString("F1"), Width = 100, Padding = new Thickness(3) };
            row.Children.Add(tb); panel.Children.Add(row); return tb;
        }
        var minBox = NumRow("Min height (yd):", Math.Floor(curMin));
        var maxBox = NumRow("Max height (yd):", Math.Ceiling(curMax));
        var flip = new System.Windows.Controls.CheckBox
        { Content = "Flip vertically", Foreground = System.Windows.Media.Brushes.White, IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(flip);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After clicking Apply, click two corners on the terrain to place it. Ctrl+Z to undo.",
            Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        var ok = new System.Windows.Controls.Button { Content = "Place on terrain...", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Import heightmap", Width = 340, Height = 280, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        _viewport.ModalOpen = true;
        try { if (win.ShowDialog() != true) return; } finally { _viewport.ModalOpen = false; }

        if (!float.TryParse(minBox.Text.Trim(), out float minH) || !float.TryParse(maxBox.Text.Trim(), out float maxH))
        { StatusText.Text = "Import heightmap: min/max must be numbers."; return; }
        if (maxH <= minH) { StatusText.Text = "Import heightmap: max height must be greater than min."; return; }

        if (_viewport.BeginHeightmapPlacement(lum, iw, ih, minH, maxH, flip.IsChecked == true))
            StatusText.Text = "Heightmap armed - click two terrain corners to mark the rectangle (Esc to cancel).";
    }

    private static float[] LoadLuminance(string path, out int w, out int h)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        w = bmp.Width; h = bmp.Height;
        var lum = new float[w * h];
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var bytes = new byte[Math.Abs(stride) * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = rowOff + x * 4;
                    lum[y * w + x] = (0.299f * bytes[o + 2] + 0.587f * bytes[o + 1] + 0.114f * bytes[o]) / 255f;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return lum;
    }
}
