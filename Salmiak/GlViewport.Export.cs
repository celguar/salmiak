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
    public int ExportFlightPaths(string folder)
    {
        if (_flightData == null)
            throw new InvalidOperationException("No flight-path data loaded - enter flight-path mode on Azeroth/Kalimdor first.");
        var dbcs = _flightData.BuildPatchFiles();
        if (dbcs.Count == 0) throw new InvalidOperationException("Flight-path export produced no DBCs.");

        System.IO.Directory.CreateDirectory(folder);

        MpqArchiveWriter.Write(System.IO.Path.Combine(folder, "patch-flightpaths.MPQ"), dbcs);

        string dbcDir = System.IO.Path.Combine(folder, "server-dbc");
        System.IO.Directory.CreateDirectory(dbcDir);
        foreach (var kv in dbcs)
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dbcDir, System.IO.Path.GetFileName(kv.Key)), kv.Value);

        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "transports.sql"), _flightData.BuildTransportSql());

        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "flightmasters.sql"), _flightData.BuildFlightMasterSql());

        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "README.txt"), FlightExportReadme());
        return _flightData.EditCount;
    }

    public List<string> DeployWarnings()
    {
        var warns = new List<string>();
        if (_flightData != null)
        {
            foreach (var p in _flightData.Paths)
            {
                if (!p.IsNew && !p.Dirty) continue;
                if (!_flightData.RouteConnectsToNetwork(p))
                    warns.Add($"Taxi route '{p.Name}' is an isolated island - it never reaches a stock node. " +
                              "The 1.12 client crashes on unreachable taxi nodes; link it to the existing network.");
                if (p.MountHorde != 0 || p.MountAlliance != 0)
                {
                    if (p.FromNode == 0 || p.ToNode == 0)
                        warns.Add($"Taxi route '{p.Name}' has no from/to node - the export safety net will " +
                                  "re-resolve them, but verify the route in taxi mode.");
                }
                else if (p.IsNew && p.Points.Count < 3)
                    warns.Add($"Transport path '{p.Name}' has only {p.Points.Count} waypoint(s) - the server " +
                              "drops the first and last as spline lead-in and ASSERTS (crashes at startup) " +
                              "when nothing remains. Give it at least 3, ideally a closed loop.");
                else if (p.IsNew && (p.Points[0].Flags == 2 || p.Points[^1].Flags == 2))
                    warns.Add($"Transport path '{p.Name}' has a passenger STOP as its first/last waypoint - " +
                              "the server drops those as spline lead-in, so the vehicle will skip that dock. " +
                              "Move the stop inward (redrawn routes get lead-in/out points automatically).");
            }
            foreach (var n in _flightData.Nodes)
                if (n.IsNew && n.Id > 256)
                {
                    warns.Add("A new taxi node has id > 256 - the 1.12 taxi mask is 8×32 bits, so the " +
                              "server can never mark it known. The route will be invisible in-game.");
                    break;
                }
        }
        if (_wdt != null)
            foreach (var kv in _editedTiles)
            {
                var (tx, ty) = kv.Key;
                if (tx >= 0 && tx < 64 && ty >= 0 && ty < 64 && !_wdt.TileExists[ty, tx])
                    warns.Add($"Edited tile ({tx},{ty}) is not flagged in the WDT MAIN table - " +
                              "the client will not request its ADT.");
            }
        return warns;
    }

    public List<(string Name, string Sql)> DeploySqlStubs()
    {
        var list = new List<(string, string)>();
        if (_flightData == null || _flightData.EditCount == 0) return list;
        list.Add(("flightmasters.sql", _flightData.BuildFlightMasterSql()));
        list.Add(("transports.sql", _flightData.BuildTransportSql()));
        return list;
    }

    private void ShowWaypointEditor(int mx, int my)
    {
        if (_selectedPath == null || _selectedNode < 0 || _selectedNode >= _selectedPath.Points.Count) return;
        var path = _selectedPath;
        var wp = path.Points[_selectedNode];
        PushFlightUndo();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel($"Waypoint {_selectedNode + 1}  (map {wp.MapId})")
        { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());

        var lbl = new ToolStripLabel($"Stop delay: {wp.Delay}s");
        menu.Items.Add(lbl);
        var track = new System.Windows.Forms.TrackBar
        { Minimum = 0, Maximum = 60, Value = (int)Math.Min(wp.Delay, 60u), AutoSize = false, Width = 220, Height = 40, TickFrequency = 10 };
        track.ValueChanged += (_, _) => { wp.Delay = (uint)track.Value; path.Dirty = true; lbl.Text = $"Stop delay: {track.Value}s"; };
        menu.Items.Add(new ToolStripControlHost(track) { AutoSize = false, Width = 230, Height = 44 });

        var stop = new ToolStripMenuItem("Stop for passengers here") { Checked = wp.Flags == 2u, CheckOnClick = true };
        var tp = new ToolStripMenuItem("Teleport / map-change here") { Checked = wp.Flags == 1u, CheckOnClick = true };
        stop.CheckedChanged += (_, _) =>
        {
            wp.Flags = stop.Checked ? 2u : (wp.Flags == 2u ? 0u : wp.Flags);
            if (stop.Checked) { tp.Checked = false; if (wp.Delay == 0) { wp.Delay = StopDelay; track.Value = (int)Math.Min(StopDelay, 60u); } }
            path.Dirty = true;
            RefreshFlightSelection();
        };
        tp.CheckedChanged += (_, _) =>
        {
            wp.Flags = tp.Checked ? 1u : (wp.Flags == 1u ? 0u : wp.Flags);
            if (tp.Checked) stop.Checked = false;
            path.Dirty = true;
            RefreshFlightSelection();
        };
        menu.Items.Add(stop);
        menu.Items.Add(tp);
        menu.Items.Add(new ToolStripLabel("(transports: stop+delay = dock pause; teleport = cross-map boundary)")
        { ForeColor = System.Drawing.Color.Gray });
        menu.Show(ScreenPoint(mx, my));
    }

    private static string FlightExportReadme() =>
        "Flight / transport path export\n" +
        "==============================\n\n" +
        "patch-flightpaths.MPQ  - CLIENT patch. Rename it patch-3.MPQ (the stock 1.12 client ONLY loads\n" +
        "                         patch.MPQ and patch-2.MPQ ... patch-9.MPQ - single DIGITS; patch-X or\n" +
        "                         other names are silently ignored) and drop it in the client's Data\n" +
        "                         folder. WITHOUT it the server advertises taxi nodes the client doesn't\n" +
        "                         know → ERROR #132 crash when the taxi map opens.\n\n" +
        "server-dbc\\*.dbc       - SERVER DBCs. Copy these over the matching files in your mangos server's\n" +
        "                         dbc folder, then restart the server. The server reads ITS OWN DBCs (NOT\n" +
        "                         the client patch) to move transports and route flights.\n\n" +
        "transports.sql         - Run ONCE on the mangos world DB to register NEW zeppelin/boat paths as\n" +
        "                         moving transports: gameobject_template (type 15, data0 = taxiPathId,\n" +
        "                         display 3031 zeppelin / 3015 ship) + the `transports` row (entry, name,\n" +
        "                         period) the server requires. Period is estimated from the loop length;\n" +
        "                         tune it if the loop stutters. Restart the server afterwards.\n\n" +
        "flightmasters.sql      - Run on the mangos world DB for PLAYER TAXI routes drawn with the wizard:\n" +
        "                         spawns a flight master (npcflag 0x8) at every NEW taxi node. The exported\n" +
        "                         TaxiNodes/TaxiPath/TaxiPathNode DBCs carry the rest (endpoints, cost,\n" +
        "                         mount creature, waypoints). Talk to the NPC once to learn the node\n" +
        "                         (.taxicheat on to test without discovery).\n\n" +
        "Notes\n" +
        "-----\n" +
        "* Transports are SERVER-driven: the client patch alone won't move a boat - the server DBCs +\n" +
        "  the gameobject_template row are what matter.\n" +
        "* Taxi routes drawn with the wizard are ONE-WAY: draw the return route separately if players\n" +
        "  should be able to fly back (its endpoints will reuse the same nodes automatically).\n" +
        "* Per-waypoint stop delay and the teleport/map-change flag are exported (middle-click a waypoint\n" +
        "  in flight mode to set them). Cross-map (multi-continent) routes are only partially authorable\n" +
        "  here - the editor edits one continent at a time.\n";

    public int ExportMpq(string outputPath)
    {
        var files = BuildExportFiles();
        if (files.Count == 0) throw new InvalidOperationException("There are no edits to export.");
        MpqArchiveWriter.Write(outputPath, files);

        try
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)) ?? ".";
            foreach (var kv in files)
                if (kv.Key.StartsWith(@"DBFilesClient\", StringComparison.OrdinalIgnoreCase))
                    System.IO.File.WriteAllBytes(
                        System.IO.Path.Combine(dir, System.IO.Path.GetFileName(kv.Key)), kv.Value);
        }
        catch { }
        return files.Count;
    }

    public Dictionary<string, byte[]> BuildExportFiles()
    {
        if (_mpq == null || string.IsNullOrEmpty(_mapName)) throw new InvalidOperationException("No map is loaded.");

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        bool anyNewTile = false;
        foreach (var kv in _editedTiles)
        {
            var (tx, ty) = kv.Key;
            string adtPath = $@"World\Maps\{_mapName}\{_mapName}_{tx}_{ty}.adt";
            byte[]? original = null;
            try
            {
                using var s = _mpq.OpenFile(adtPath);
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                original = ms.ToArray();
            }
            catch { }
            if (original == null)
            {
                original = AdtWriter.BuildTemplate(kv.Value);
                anyNewTile = true;
            }
            files[adtPath] = AdtWriter.Build(original, kv.Value);
        }

        if (!_editedTiles.IsEmpty && files.Count == 0)
            throw new InvalidOperationException("Could not read any base ADTs to export.");

        if (anyNewTile && _wdt != null)
        {
            string wdtPath = $@"World\Maps\{_mapName}\{_mapName}.wdt";
            byte[]? wdtOriginal = null;
            try
            {
                using var s = _mpq.OpenFile(wdtPath);
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                wdtOriginal = ms.ToArray();
            }
            catch { }
            files[wdtPath] = wdtOriginal != null
                ? WdtFile.PatchMain(wdtOriginal, _wdt.TileExists)
                : WdtFile.BuildNew(_wdt.TileExists);

            if (wdtOriginal == null)
            {
                try
                {
                    var (dbc, newId) = BuildMapDbcPatch(_mapName);
                    if (dbc != null)
                    {
                        files[@"DBFilesClient\Map.dbc"] = dbc;
                        Notify?.Invoke($"New map '{_mapName}' registered as map id {newId} - Map.dbc bundled " +
                                       "(the server needs its own copy in the dbc folder).");
                    }
                }
                catch (Exception ex)
                {
                    Notify?.Invoke($"Couldn't patch Map.dbc ({ex.Message}); the client can't mount the new map.");
                }
            }
        }

        try
        {
            int mm = AddMinimapFiles(files);
            if (mm > 0) Notify?.Invoke($"Regenerated {mm} minimap tile(s) for the patch.");
        }
        catch (Exception ex) { Notify?.Invoke($"Minimap regen skipped: {ex.Message}"); }

        foreach (var (path, bytes) in _mpq.Overrides) files[path] = bytes;

        if (_areaTable?.BuildPatchFile() is byte[] areaDbc)
        {
            files[@"DBFilesClient\AreaTable.dbc"] = areaDbc;
            Notify?.Invoke($"{_areaTable.NewZones.Count} new zone(s) bundled in AreaTable.dbc.");
        }

        if (_flightData != null && _flightData.EditCount > 0)
        {
            foreach (var (name, bytes) in _flightData.BuildPatchFiles())
                files[name] = bytes;
        }

        return files;
    }

    private (byte[]? Dbc, int NewId) BuildMapDbcPatch(string mapName)
    {
        Salmiak.Core.Formats.DbcFile map;
        using (var s = _mpq!.OpenFile(@"DBFilesClient\Map.dbc")) map = Salmiak.Core.Formats.DbcFile.Read(s);

        uint maxId = 0;
        for (int i = 0; i < map.RecordCount; i++)
        {
            maxId = Math.Max(maxId, map.GetU(i, 0));
            uint dirOfs = map.GetU(i, 1);
            if (dirOfs > 0 && dirOfs < map.StringBlock.Length)
            {
                int end = (int)dirOfs;
                while (end < map.StringBlock.Length && map.StringBlock[end] != 0) end++;
                string dir = System.Text.Encoding.UTF8.GetString(map.StringBlock, (int)dirOfs, end - (int)dirOfs);
                if (string.Equals(dir, mapName, StringComparison.OrdinalIgnoreCase)) return (null, (int)map.GetU(i, 0));
            }
        }

        var strings = new List<byte>(map.StringBlock);
        if (strings.Count == 0) strings.Add(0);
        uint nameOfs = (uint)strings.Count;
        strings.AddRange(System.Text.Encoding.UTF8.GetBytes(mapName));
        strings.Add(0);

        var recs = new List<byte[]>(map.RecordCount + 1);
        for (int i = 0; i < map.RecordCount; i++) recs.Add((byte[])map.Records[i].Clone());
        var r = new byte[map.RecordSize];
        uint newId = maxId + 1;
        void WU(int field, uint v) => BitConverter.GetBytes(v).CopyTo(r, field * 4);
        WU(0, newId);
        WU(1, nameOfs);
        WU(2, 0);
        WU(4, nameOfs);
        if (map.FieldCount > 12 && map.RecordCount > 0) WU(12, map.GetU(0, 12));
        recs.Add(r);

        return (Salmiak.Core.Formats.DbcFile.WriteRecords(recs, map.FieldCount, map.RecordSize, strings.ToArray()), (int)newId);
    }

    public int ExportServerMaps(string folder)
    {
        if (_mpq == null || string.IsNullOrEmpty(_mapName)) throw new InvalidOperationException("No map is loaded.");
        if (_editedTiles.IsEmpty) throw new InvalidOperationException("There are no edited tiles to export.");

        int mapId = ResolveMapId();

        var areaFlags = new Dictionary<int, ushort>();
        try
        {
            Salmiak.Core.Formats.DbcFile at;
            using (var s = _mpq.OpenFile(@"DBFilesClient\AreaTable.dbc")) at = Salmiak.Core.Formats.DbcFile.Read(s);
            for (int i = 0; i < at.RecordCount; i++) areaFlags[(int)at.GetU(i, 0)] = (ushort)at.GetU(i, 3);
        }
        catch { }
        if (_areaTable != null)
            foreach (var z in _areaTable.NewZones) areaFlags[z.Id] = z.Flag;

        System.IO.Directory.CreateDirectory(folder);
        int n = 0;
        foreach (var kv in _editedTiles)
        {
            var (tx, ty) = kv.Key;
            var bytes = ServerMapFile.Build(kv.Value,
                id => areaFlags.TryGetValue(id, out var f) ? f : (ushort)0xFFFF);
            System.IO.File.WriteAllBytes(
                System.IO.Path.Combine(folder, ServerMapFile.FileName(mapId, tx, ty)), bytes);
            n++;
        }

        string mapDbcNote = "";
        try
        {
            byte[] mapDbc;
            var (patched, _) = BuildMapDbcPatch(_mapName);
            if (patched != null) mapDbc = patched;
            else
            {
                using var s = _mpq.OpenFile(@"DBFilesClient\Map.dbc");
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                mapDbc = ms.ToArray();
            }
            string? parent = System.IO.Directory.GetParent(folder.TrimEnd('\\', '/'))?.FullName;
            string dbcDir = parent != null && System.IO.Directory.Exists(System.IO.Path.Combine(parent, "dbc"))
                ? System.IO.Path.Combine(parent, "dbc")
                : folder;
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dbcDir, "Map.dbc"), mapDbc);
            mapDbcNote = $", plus Map.dbc to {dbcDir}";

            if (parent != null)
            {
                string mmapDir = System.IO.Path.Combine(parent, "mmaps");
                string mmapFile = System.IO.Path.Combine(mmapDir, $"{mapId:D3}.mmap");
                if (System.IO.Directory.Exists(mmapDir) && !System.IO.File.Exists(mmapFile))
                {
                    var donor = System.IO.Directory.GetFiles(mmapDir, "*.mmap");
                    if (donor.Length > 0)
                    {
                        System.IO.File.Copy(donor[0], mmapFile);
                        mapDbcNote += $" + empty {mapId:D3}.mmap (prevents the navmesh-assert crash)";
                    }
                }
            }
        }
        catch (Exception ex) { mapDbcNote = $" (Map.dbc skipped: {ex.Message})"; }

        Notify?.Invoke($"Wrote {n} server grid file(s) for map id {mapId} to {folder}{mapDbcNote}");
        return n;
    }

    private int ResolveMapId()
    {
        Salmiak.Core.Formats.DbcFile map;
        using (var s = _mpq!.OpenFile(@"DBFilesClient\Map.dbc")) map = Salmiak.Core.Formats.DbcFile.Read(s);
        uint maxId = 0;
        for (int i = 0; i < map.RecordCount; i++)
        {
            maxId = Math.Max(maxId, map.GetU(i, 0));
            uint dirOfs = map.GetU(i, 1);
            if (dirOfs == 0 || dirOfs >= map.StringBlock.Length) continue;
            int end = (int)dirOfs;
            while (end < map.StringBlock.Length && map.StringBlock[end] != 0) end++;
            string dir = System.Text.Encoding.UTF8.GetString(map.StringBlock, (int)dirOfs, end - (int)dirOfs);
            if (string.Equals(dir, _mapName, StringComparison.OrdinalIgnoreCase)) return (int)map.GetU(i, 0);
        }
        return (int)(maxId + 1);
    }
}
