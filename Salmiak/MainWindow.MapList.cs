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
    private Dictionary<string, int>? _mapIds;

    private sealed record MapEntry(string Name, int? Id)
    {
        public override string ToString() => Id is int i ? $"{Name}  ·  {i}" : $"{Name}  ·  new";
    }

    private MapEntry MakeMapEntry(string name)
    {
        try { if (_mpq != null) _mapIds ??= CreatureModels.LoadMapIds(p => _mpq.OpenFile(p)); }
        catch { }
        return new MapEntry(name, _mapIds != null && _mapIds.TryGetValue(name, out int id) ? id : null);
    }

    private void PopulateMapList()
    {
        MapList.Items.Clear();
        _mapIds = null;

        var maps = _mpq!.ListMaps();

        if (maps.Count == 0)
        {
            string[] known =
            {
                "Azeroth", "Kalimdor", "Expansion01", "DeeprunTram", "Deadmines", "WailingCaverns",
                "Blackfathom", "Shadowfang", "OrgrimmarInstance", "StormwindJail", "Monastery",
                "Stratholme", "Scholomance", "SunkenTemple", "RazorfenKraulInstance", "RazorfenDowns",
                "GnomeragonInstance", "BlackRockSpire", "BlackrockDepths", "UldamanInstance",
            };
            foreach (var m in known)
                if (_mpq.FileExists($@"World\Maps\{m}\{m}.wdt")) maps.Add(m);
        }

        foreach (var m in maps) MapList.Items.Add(MakeMapEntry(m));

        foreach (var item in MapList.Items)
            if (item is MapEntry me && string.Equals(me.Name, "Azeroth", StringComparison.OrdinalIgnoreCase))
            { MapList.SelectedItem = item; break; }

        if (MapList.Items.Count == 0)
        {
            var diag = _mpq?.ArchiveDiagnostics("terrain.MPQ") ?? "mpq null";
            StatusText.Text = $"No maps found. terrain.MPQ hash table: {diag}";
        }
    }

    private bool _suppressMapSelect;

    private void MapList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressMapSelect) return;
        if (MapList.SelectedItem is not MapEntry entry || _mpq == null || _viewport == null)
            return;
        if (string.Equals(entry.Name, _viewport.MapName, StringComparison.OrdinalIgnoreCase)) return;

        LoadMap(entry.Name);
    }

    private void LoadMap(string mapName)
    {
        try
        {
            StatusText.Text = $"Loading {mapName}...";
            PopulateGoTo(mapName);

            var wdtPath = $@"World\Maps\{mapName}\{mapName}.wdt";
            WdtFile wdt;
            using (var s = _mpq!.OpenFile(wdtPath))
                wdt = WdtFile.Parse(s);

            if (wdt.IsWmoOnly)
            {
                if (wdt.GlobalWmoDef == null) { StatusText.Text = $"{mapName}: WMO-only map but no WMO reference."; return; }
                _viewport!.LoadGlobalWmo(wdt, mapName);
                StatusText.Text = $"Loaded {mapName} (WMO-only - view/doodads, no terrain)";
                return;
            }

            bool any = false;
            for (int y = 0; y < WdtFile.GridSize && !any; y++)
            for (int x = 0; x < WdtFile.GridSize && !any; x++)
                if (wdt.TileExists[y, x]) any = true;
            if (!any) { StatusText.Text = $"{mapName}: no tiles in WDT."; return; }

            _viewport!.StreamMap(wdt, mapName);
            StatusText.Text = $"Streaming {mapName} (radius {_viewport.LoadRadius})";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading {mapName}: {ex.Message}";
        }
    }

    private bool _suppressGoTo;

    private void PopulateGoTo(string mapName)
    {
        _suppressGoTo = true;
        GoToBox.Items.Clear();
        if (TeleportLocations.ByMap.TryGetValue(mapName, out var cities))
        {
            foreach (var c in cities)
                GoToBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = c.Name, Tag = c });
            GoToPanel.Visibility = Visibility.Visible;
        }
        else
        {
            GoToPanel.Visibility = Visibility.Collapsed;
        }
        _suppressGoTo = false;
    }

    private void GoTo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressGoTo || _viewport == null) return;
        if (GoToBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is ValueTuple<string, float, float, float> c)
        {
            _viewport.TeleportTo(c.Item2, c.Item3, c.Item4);
            StatusText.Text = $"Teleported to {c.Item1}";
        }
    }
}
