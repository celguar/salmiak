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

    private WorldDb? _db;

    private WorldDb Db => _db ??= new WorldDb(new DbSettings
    {
        Host = Properties.Settings.Default.DbHost,
        Port = Properties.Settings.Default.DbPort,
        User = Properties.Settings.Default.DbUser,
        Password = Properties.Settings.Default.DbPassword,
        WorldDb = Properties.Settings.Default.DbWorldName,
        CharactersDb = Properties.Settings.Default.DbCharactersName,
    });

    private void DatabaseConnection_Click(object sender, RoutedEventArgs e)
    {
        var s = Properties.Settings.Default;
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        void Label(string t) => panel.Children.Add(new System.Windows.Controls.TextBlock
        { Text = t, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2) });

        Label("Host");
        var host = new System.Windows.Controls.TextBox { Text = s.DbHost };
        panel.Children.Add(host);
        Label("Port");
        var port = new System.Windows.Controls.TextBox { Text = s.DbPort.ToString() };
        panel.Children.Add(port);
        Label("User");
        var user = new System.Windows.Controls.TextBox { Text = s.DbUser };
        panel.Children.Add(user);
        Label("Password (stored in plain text in settings.json)");
        var pass = new System.Windows.Controls.PasswordBox { Password = s.DbPassword };
        panel.Children.Add(pass);
        Label("World database");
        var world = new System.Windows.Controls.TextBox { Text = s.DbWorldName };
        panel.Children.Add(world);
        Label("Characters database");
        var chars = new System.Windows.Controls.TextBox { Text = s.DbCharactersName };
        panel.Children.Add(chars);

        var result = new System.Windows.Controls.TextBlock
        { Foreground = System.Windows.Media.Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(result);

        DbSettings Collect() => new()
        {
            Host = host.Text.Trim(),
            Port = int.TryParse(port.Text.Trim(), out int pp) ? pp : 3306,
            User = user.Text.Trim(), Password = pass.Password,
            WorldDb = world.Text.Trim(), CharactersDb = chars.Text.Trim(),
        };

        var buttons = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var test = new System.Windows.Controls.Button { Content = "Test connection", Padding = new Thickness(10, 3, 10, 3) };
        var save = new System.Windows.Controls.Button { Content = "Save", Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(test);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        test.Click += (_, _) =>
        {
            string? err = new WorldDb(Collect()).TestConnection(out string info);
            result.Text = err == null ? $"Connected - {info}, both databases found." : $"Failed: {err}";
            result.Foreground = err == null ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.OrangeRed;
        };

        var win = new Window
        {
            Title = "Database connection", Width = 360, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray, Content = panel,
        };
        save.Click += (_, _) => win.DialogResult = true;
        if (_viewport != null) _viewport.ModalOpen = true;
        bool go;
        try { go = win.ShowDialog() == true; } finally { if (_viewport != null) _viewport.ModalOpen = false; }
        if (!go) return;

        var ns = Collect();
        s.DbHost = ns.Host; s.DbPort = ns.Port; s.DbUser = ns.User; s.DbPassword = ns.Password;
        s.DbWorldName = ns.WorldDb; s.DbCharactersName = ns.CharactersDb;
        s.Save();
        _db = null;
        _ctModelColumn = null;
        StatusText.Text = "Database settings saved.";
    }

    private string? _ctModelColumn;

    private string DetectModelColumn()
    {
        var rows = Db.Query(
            "SELECT COLUMN_NAME c FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'creature_template' " +
            "AND COLUMN_NAME IN ('DisplayId1', 'ModelId1', 'modelid_1')",
            characters: false, ("@db", Db.Settings.WorldDb));
        if (rows.Count == 0) throw new InvalidOperationException("creature_template has no known display-id column");
        string col = (string)rows[0]["c"]!;
        return col.Substring(0, col.Length - 1);
    }

    private void LoadNpcSpawns()
    {
        if (_viewport == null) return;
        if (_viewport.MapName == null || _mpq == null)
        { StatusText.Text = "NPC view: load a map first."; _viewport.SetPrimaryMode(GlViewport.EditorMode.None); return; }

        try
        {
            _mapIds ??= CreatureModels.LoadMapIds(p => _mpq.OpenFile(p));
            if (!_mapIds.TryGetValue(_viewport.MapName, out int mapId))
            { StatusText.Text = $"NPC view: no map id for '{_viewport.MapName}' in Map.dbc."; return; }

            _ctModelColumn ??= DetectModelColumn();
            string mc = _ctModelColumn;
            var rows = Db.Query(
                "SELECT c.guid g, c.id e, c.MovementType mt, ct.Name nm, " +
                "c.position_x px, c.position_y py, c.position_z pz, c.orientation o, ct.Scale sc, " +
                $"ct.{mc}1 m1, ct.{mc}2 m2, ct.{mc}3 m3, ct.{mc}4 m4 " +
                "FROM creature c JOIN creature_template ct ON ct.Entry = c.id WHERE c.map = @map",
                characters: false, ("@map", mapId));

            var spawns = new List<GlViewport.NpcSpawn>(rows.Count);
            foreach (var r in rows)
            {
                uint display = 0;
                foreach (var key in new[] { "m1", "m2", "m3", "m4" })
                    if (Convert.ToUInt32(r[key]) is uint d && d != 0) { display = d; break; }
                if (display == 0) continue;
                spawns.Add(new GlViewport.NpcSpawn(
                    Convert.ToUInt32(r["g"]), Convert.ToUInt32(r["e"]), display,
                    Convert.ToString(r["nm"]) ?? "", Convert.ToInt32(r["mt"]),
                    Convert.ToSingle(r["px"]), Convert.ToSingle(r["py"]), Convert.ToSingle(r["pz"]),
                    Convert.ToSingle(r["o"]), Convert.ToSingle(r["sc"])));
            }
            _viewport.ShowNpcs(spawns);
            StatusText.Text = $"NPC view - {spawns.Count} spawn(s) on {_viewport.MapName} (models stream in).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"NPC view: database error - {ex.Message} (check File > Database Connection).";
            _viewport.SetPrimaryMode(GlViewport.EditorMode.None);
        }
    }

    private void LoadHerbSpawns()
    {
        if (_viewport == null) return;
        if (_viewport.MapName == null || _mpq == null)
        { StatusText.Text = "Herb view: load a map first."; _viewport.SetPrimaryMode(GlViewport.EditorMode.None); return; }

        try
        {
            _mapIds ??= CreatureModels.LoadMapIds(p => _mpq.OpenFile(p));
            if (!_mapIds.TryGetValue(_viewport.MapName, out int mapId))
            { StatusText.Text = $"Herb view: no map id for '{_viewport.MapName}' in Map.dbc."; return; }

            var rows = Db.Query(
                "SELECT g.guid gu, g.id e, gt.name nm, gt.displayId d, gt.size sc, gt.data0 lk, " +
                "g.position_x px, g.position_y py, g.position_z pz, g.orientation o " +
                "FROM gameobject g JOIN gameobject_template gt ON gt.entry = g.id " +
                "WHERE g.map = @map AND gt.type = 3",
                characters: false, ("@map", mapId));

            var spawns = new List<GlViewport.HerbSpawn>(rows.Count);
            foreach (var r in rows)
                spawns.Add(new GlViewport.HerbSpawn(
                    Convert.ToUInt32(r["gu"]), Convert.ToUInt32(r["e"]), Convert.ToUInt32(r["d"]),
                    Convert.ToString(r["nm"]) ?? "", Convert.ToUInt32(r["lk"]),
                    Convert.ToSingle(r["px"]), Convert.ToSingle(r["py"]), Convert.ToSingle(r["pz"]),
                    Convert.ToSingle(r["o"]), Convert.ToSingle(r["sc"])));
            _viewport.ShowHerbs(spawns);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Herb view: database error - {ex.Message} (check File > Database Connection).";
            _viewport.SetPrimaryMode(GlViewport.EditorMode.None);
        }
    }

    private void LoadNpcPatrol(GlViewport.NpcSpawn s)
    {
        if (_viewport == null) return;
        try
        {
            const string cols = "SELECT Point p, PositionX x, PositionY y, PositionZ z, Orientation o, " +
                                "WaitTime w, ScriptId sc, Comment cm FROM ";
            var rows = Db.Query(cols + "creature_movement WHERE Id = @g ORDER BY Point",
                characters: false, ("@g", s.Guid));
            if (rows.Count == 0)
                rows = Db.Query(cols + "creature_movement_template WHERE Entry = @e ORDER BY PathId, Point",
                    characters: false, ("@e", s.Entry));

            var pts = new List<GlViewport.PatrolPoint>(rows.Count);
            foreach (var r in rows)
                pts.Add(new GlViewport.PatrolPoint(
                    Convert.ToInt32(r["p"]),
                    Convert.ToSingle(r["x"]), Convert.ToSingle(r["y"]), Convert.ToSingle(r["z"]),
                    Convert.ToSingle(r["o"]),
                    Convert.ToUInt32(r["w"]), Convert.ToUInt32(r["sc"]),
                    Convert.ToString(r["cm"])));
            _viewport.ShowPatrol(pts);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Patrol: database error - {ex.Message}";
        }
    }

    private void Deploy_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null || _mpq == null)
        { StatusText.Text = "Deploy: open a WoW directory and a map first."; return; }

        var s = Properties.Settings.Default;
        if (string.IsNullOrWhiteSpace(s.DeployClientPatch) && !string.IsNullOrEmpty(_wowDataPath))
        {
            string data = Directory.Exists(System.IO.Path.Combine(_wowDataPath, "Data"))
                ? System.IO.Path.Combine(_wowDataPath, "Data") : _wowDataPath;
            s.DeployClientPatch = System.IO.Path.Combine(data, "patch-3.mpq");
        }

        int tiles = _viewport.EditedTileCount;
        int flights = _viewport.FlightEditCount;
        int zones = _viewport.NewZoneCount;
        int wmos = _mpq.Overrides.Count;
        if (tiles + flights + zones + wmos == 0)
        { StatusText.Text = "Deploy: there are no pending edits."; return; }

        List<string> warns;
        try { warns = _viewport.DeployWarnings(); }
        catch (Exception ex) { warns = new List<string> { $"Validation itself failed: {ex.Message}" }; }

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
        void Label(string t) => panel.Children.Add(new System.Windows.Controls.TextBlock
        { Text = t, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 8, 0, 2) });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Pending: {tiles} edited tile(s), {flights} flight path(s), {zones} new zone(s), {wmos} WMO override(s).",
            Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
        });

        if (warns.Count > 0)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Pre-flight warnings:\n- " + string.Join("\n- ", warns),
                Foreground = System.Windows.Media.Brushes.Orange, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0), MaxWidth = 520,
            });
        }
        else
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Pre-flight checks passed (taxi network, WDT tile flags).",
                Foreground = System.Windows.Media.Brushes.LightGreen, Margin = new Thickness(0, 8, 0, 0),
            });
        }

        (System.Windows.Controls.TextBox Box, System.Windows.Controls.Button Btn) PathRow(string text)
        {
            var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var btn = new System.Windows.Controls.Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
            System.Windows.Controls.DockPanel.SetDock(btn, System.Windows.Controls.Dock.Right);
            var tb = new System.Windows.Controls.TextBox { Text = text, Padding = new Thickness(3) };
            row.Children.Add(btn);
            row.Children.Add(tb);
            panel.Children.Add(row);
            return (tb, btn);
        }

        Label("Client patch archive (merged into - digit-named slots only: patch-2...patch-9.mpq)");
        var (patchBox, patchBtn) = PathRow(s.DeployClientPatch);
        patchBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Pick the client patch slot to merge into",
                Filter = "MPQ archive (*.mpq)|*.mpq", DefaultExt = ".mpq", OverwritePrompt = false,
                FileName = System.IO.Path.GetFileName(patchBox.Text),
                InitialDirectory = SafeDir(patchBox.Text),
            };
            if (dlg.ShowDialog() == true) patchBox.Text = dlg.FileName;
        };

        Label("Server folder (mangosd's folder, containing dbc\\, maps\\ and mmaps\\)");
        var (serverBox, serverBtn) = PathRow(s.DeployServerDir);
        serverBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Pick the mangosd folder (with dbc\\, maps\\, mmaps\\)" };
            if (dlg.ShowDialog() == true) serverBox.Text = dlg.FolderName;
        };

        var applySql = new System.Windows.Controls.CheckBox
        {
            Content = "Spawn flight masters in the world DB",
            Foreground = System.Windows.Media.Brushes.White, IsChecked = s.DeployApplySql, Margin = new Thickness(0, 10, 0, 0),
        };
        panel.Children.Add(applySql);

        var restart = new System.Windows.Controls.CheckBox
        {
            Content = "Restart mangosd after deploying",
            Foreground = System.Windows.Media.Brushes.White, IsChecked = s.DeployRestartMangosd, Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(restart);

        var deployBtn = new System.Windows.Controls.Button
        { Content = warns.Count > 0 ? "Deploy anyway" : "Deploy", Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.Bold };
        panel.Children.Add(deployBtn);

        var win = new Window
        {
            Title = "Deploy", Width = 580, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.DimGray,
            Content = new System.Windows.Controls.ScrollViewer
            { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, MaxHeight = 640 },
        };

        deployBtn.Click += (_, _) =>
        {
            string patchPath = patchBox.Text.Trim();
            string serverDir = serverBox.Text.Trim();
            if (patchPath.Length == 0 && serverDir.Length == 0)
            { System.Windows.MessageBox.Show(win, "Set a client patch path and/or a server folder.", "Deploy", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            string slot = System.IO.Path.GetFileNameWithoutExtension(patchPath);
            if (patchPath.Length > 0 &&
                !System.Text.RegularExpressions.Regex.IsMatch(slot, @"^patch(-[2-9])?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (System.Windows.MessageBox.Show(win,
                        $"'{slot}.mpq' is not a name the 1.12 client loads (only patch.MPQ and patch-2...patch-9.MPQ are read).\n\nDeploy to it anyway?",
                        "Deploy", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            if (serverDir.Length > 0 && !Directory.Exists(serverDir))
            { System.Windows.MessageBox.Show(win, "The server folder does not exist.", "Deploy", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            s.DeployClientPatch = patchPath;
            s.DeployServerDir = serverDir;
            s.DeployRestartMangosd = restart.IsChecked == true;
            s.DeployApplySql = applySql.IsChecked == true;
            s.Save();

            try
            {
                var report = RunDeploy(patchPath, serverDir, restart.IsChecked == true, applySql.IsChecked == true);
                win.Close();
                System.Windows.MessageBox.Show(this, string.Join("\n", report), "Deploy complete", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "Deploy complete.";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(win, ex.Message, "Deploy failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        _viewport.ModalOpen = true;
        try { win.ShowDialog(); } finally { _viewport.ModalOpen = false; }
    }

    private static string? SafeDir(string path)
    {
        try { return System.IO.Path.GetDirectoryName(path); } catch { return null; }
    }

    private List<string> RunDeploy(string patchPath, string serverDir, bool restartMangosd, bool applySql)
    {
        var report = new List<string>();
        var files = _viewport!.BuildExportFiles();
        if (files.Count == 0) throw new InvalidOperationException("The export produced no files.");

        if (serverDir.Length > 0)
        {
            string dbcDir = System.IO.Path.Combine(serverDir, "dbc");
            if (Directory.Exists(dbcDir))
            {
                int n = 0;
                foreach (var kv in files)
                    if (kv.Key.StartsWith(@"DBFilesClient\", StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllBytes(System.IO.Path.Combine(dbcDir, System.IO.Path.GetFileName(kv.Key)), kv.Value);
                        n++;
                    }
                report.Add(n > 0 ? $"Server: copied {n} DBC(s) to dbc\\" : "Server: no DBC changes.");
            }
            else report.Add("Server: dbc\\ folder not found, DBCs not copied.");

            if (_viewport.EditedTileCount > 0)
            {
                int n = _viewport.ExportServerMaps(System.IO.Path.Combine(serverDir, "maps"));
                report.Add($"Server: wrote {n} .map grid file(s) to maps\\ (plus mmap stub for new maps).");
            }

            var stubs = _viewport.DeploySqlStubs();
            if (stubs.Count > 0)
            {
                string sqlDir = System.IO.Path.Combine(serverDir, "deploy-sql");
                Directory.CreateDirectory(sqlDir);
                foreach (var (name, sql) in stubs)
                    File.WriteAllText(System.IO.Path.Combine(sqlDir, name), sql);
                report.Add($"Server: wrote {stubs.Count} SQL stub(s) to deploy-sql\\.");

                if (applySql)
                {
                    var fm = stubs.FirstOrDefault(t => t.Name == "flightmasters.sql");
                    if (fm.Sql != null)
                    {
                        try
                        {
                            Db.Execute(fm.Sql);
                            report.Add("Server: flightmasters.sql applied to the world DB (existing spawns skipped).");
                        }
                        catch (Exception ex)
                        {
                            report.Add($"Server: flightmasters.sql failed ({ex.Message}); apply it manually from deploy-sql\\.");
                        }
                    }
                    if (stubs.Any(t => t.Name == "transports.sql"))
                        report.Add("transports.sql was not auto-applied (it allocates new gameobject_template " +
                                   "entries each run, so it is not re-runnable); apply it once from deploy-sql\\.");
                }
                else
                    report.Add("SQL stubs not applied; review and run them on the world DB.");
            }
        }

        if (patchPath.Length > 0)
        {
            var merged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(patchPath))
            {
                using var arc = new WowMpqArchive(patchPath);
                foreach (var name in arc.ListFiles())
                {
                    if (string.Equals(name, "(listfile)", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var st = arc.OpenFile(name);
                        using var ms = new MemoryStream();
                        st.CopyTo(ms);
                        merged[name] = ms.ToArray();
                    }
                    catch { }
                }
            }
            int replaced = 0;
            foreach (var kv in files) { if (merged.ContainsKey(kv.Key)) replaced++; merged[kv.Key] = kv.Value; }

            string tmp = patchPath + ".deploy-tmp";
            MpqArchiveWriter.Write(tmp, merged);

            _mpq!.Dispose();
            try
            {
                if (File.Exists(patchPath)) File.Delete(patchPath);
                File.Move(tmp, patchPath);
            }
            catch (IOException ex)
            {
                try { File.Delete(tmp); } catch { }
                TryOpenDirectory(_wowDataPath);
                throw new IOException($"Couldn't replace {System.IO.Path.GetFileName(patchPath)} - close the WoW client and retry. ({ex.Message})");
            }
            report.Add($"Client: merged {files.Count} file(s) into {System.IO.Path.GetFileName(patchPath)} " +
                       $"({merged.Count} total, {replaced} replaced).");

            TryOpenDirectory(_wowDataPath);
        }

        if (restartMangosd && serverDir.Length > 0)
            report.Add(RestartMangosd(serverDir));
        else if (serverDir.Length > 0)
            report.Add("Restart mangosd to pick up the new server data.");

        return report;
    }

    private static string RestartMangosd(string serverDir)
    {
        string exe = System.IO.Path.Combine(serverDir, "mangosd.exe");
        int killed = 0;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("mangosd"))
        {
            try { p.Kill(); p.WaitForExit(10000); killed++; }
            catch { }
            finally { p.Dispose(); }
        }
        if (!File.Exists(exe))
            return killed > 0 ? "mangosd stopped, but mangosd.exe was not found in the server folder; start it manually."
                              : "mangosd.exe not found in the server folder; start the server manually.";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = serverDir,
                UseShellExecute = true,
            });
            return killed > 0 ? "mangosd restarted." : "mangosd started.";
        }
        catch (Exception ex)
        {
            return $"mangosd start failed: {ex.Message}";
        }
    }
}
