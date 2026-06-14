using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class FlightPaths
{
    public sealed class Waypoint
    {
        public float X, Y, Z;
        public int MapId;
        public uint Flags;
        public uint Delay;
        public Waypoint() { }
        public Waypoint(float x, float y, float z, int mapId, uint flags = 0, uint delay = 0)
        { X = x; Y = y; Z = z; MapId = mapId; Flags = flags; Delay = delay; }
    }

    public sealed class Path
    {
        public uint Id;
        public int Continent;
        public string Name = "";
        public readonly List<Waypoint> Points = new();
        public bool Dirty;
        public bool IsNew;

        public string CustomName = "";
        public uint Cost;
        public uint MountHorde;
        public uint MountAlliance;
        public uint FromNode;
        public uint ToNode;
        public bool TwoWay = true;
        public string ReverseName = "";
        public string TransportKind = "";
    }

    public sealed class Node
    {
        public uint Id;
        public int MapId;
        public float X, Y, Z;
        public string Name = "";
        public uint MountHorde, MountAlliance;
        public bool IsNew;
    }

    public List<Path> Paths { get; } = new();

    public List<Node> Nodes { get; } = new();

    public List<(uint Horde, uint Alliance, string Sample, int Count)> MountPairs { get; } = new();

    private DbcFile? _pathNodeDbc;
    private DbcFile? _pathDbc;
    private DbcFile? _nodesDbc;

    public static FlightPaths Load(Func<string, Stream> open)
    {
        var fp = new FlightPaths();

        var nodeName = new Dictionary<uint, string>();
        try
        {
            DbcFile tn;
            using (var s = open(@"DBFilesClient\TaxiNodes.dbc")) tn = DbcFile.Read(s);
            fp._nodesDbc = tn;
            var pairs = new Dictionary<(uint H, uint A), (string Sample, int Count)>();
            for (int i = 0; i < tn.RecordCount; i++)
            {
                var node = new Node
                {
                    Id = tn.GetU(i, 0), MapId = tn.GetI(i, 1),
                    X = tn.GetF(i, 2), Y = tn.GetF(i, 3), Z = tn.GetF(i, 4),
                    Name = ReadStr(tn.StringBlock, tn.GetU(i, 5)),
                };
                if (tn.FieldCount > 15)
                {
                    node.MountHorde = tn.GetU(i, 14);
                    node.MountAlliance = tn.GetU(i, 15);
                    var key = (node.MountHorde, node.MountAlliance);
                    pairs[key] = pairs.TryGetValue(key, out var v)
                        ? (v.Sample, v.Count + 1)
                        : (node.Name, 1);
                }
                fp.Nodes.Add(node);
                nodeName[node.Id] = node.Name;
            }
            foreach (var kv in pairs)
                fp.MountPairs.Add((kv.Key.H, kv.Key.A, kv.Value.Sample, kv.Value.Count));
            fp.MountPairs.Sort((a, b) => b.Count.CompareTo(a.Count));
        }
        catch { }

        var pathEnds = new Dictionary<uint, (uint From, uint To)>();
        try
        {
            DbcFile tp;
            using (var s = open(@"DBFilesClient\TaxiPath.dbc")) tp = DbcFile.Read(s);
            fp._pathDbc = tp;
            for (int i = 0; i < tp.RecordCount; i++)
                pathEnds[tp.GetU(i, 0)] = (tp.GetU(i, 1), tp.GetU(i, 2));
        }
        catch { }

        DbcFile pn;
        using (var s = open(@"DBFilesClient\TaxiPathNode.dbc")) pn = DbcFile.Read(s);
        fp._pathNodeDbc = pn;

        var byPath = new Dictionary<uint, List<(int Idx, int Map, float X, float Y, float Z, uint Flags, uint Delay)>>();
        for (int i = 0; i < pn.RecordCount; i++)
        {
            uint pathId = pn.GetU(i, 1);
            int idx = pn.GetI(i, 2);
            int map = (int)pn.GetU(i, 3);
            float x = pn.GetF(i, 4), y = pn.GetF(i, 5), z = pn.GetF(i, 6);
            uint flags = pn.FieldCount > 7 ? pn.GetU(i, 7) : 0;
            uint delay = pn.FieldCount > 8 ? pn.GetU(i, 8) : 0;
            if (!byPath.TryGetValue(pathId, out var list)) byPath[pathId] = list = new();
            list.Add((idx, map, x, y, z, flags, delay));
        }

        foreach (var kv in byPath)
        {
            var list = kv.Value;
            list.Sort((a, b) => a.Idx.CompareTo(b.Idx));
            var p = new Path { Id = kv.Key, Continent = list.Count > 0 ? list[0].Map : -1 };
            foreach (var w in list) p.Points.Add(new Waypoint(w.X, w.Y, w.Z, w.Map, w.Flags, w.Delay));
            if (p.Points.Count < 2) continue;

            if (pathEnds.TryGetValue(kv.Key, out var ends))
            {
                string from = nodeName.TryGetValue(ends.From, out var f) && f.Length > 0 ? f : $"#{ends.From}";
                string to   = nodeName.TryGetValue(ends.To,   out var t) && t.Length > 0 ? t : $"#{ends.To}";
                p.Name = $"{from} → {to}";
                p.FromNode = ends.From;
                p.ToNode = ends.To;
            }
            else p.Name = $"Path {kv.Key}";

            fp.Paths.Add(p);
        }

        return fp;
    }

    public int EditCount { get { int n = 0; foreach (var p in Paths) if (p.Dirty || p.IsNew) n++; return n; } }

    public uint EnsureNodeAt(int mapId, float x, float y, float z, string name,
                             uint mountHorde, uint mountAlliance, out bool created, float reuseRadius = 80f)
    {
        Node? best = null;
        float bestD2 = reuseRadius * reuseRadius;
        foreach (var n in Nodes)
        {
            if (n.MapId != mapId) continue;
            float d2 = (n.X - x) * (n.X - x) + (n.Y - y) * (n.Y - y) + (n.Z - z) * (n.Z - z);
            if (d2 <= bestD2) { bestD2 = d2; best = n; }
        }
        if (best != null) { created = false; return best.Id; }

        uint id = 1;
        foreach (var n in Nodes) if (n.Id >= id) id = n.Id + 1;
        Nodes.Add(new Node
        {
            Id = id, MapId = mapId, X = x, Y = y, Z = z,
            Name = string.IsNullOrWhiteSpace(name) ? $"Node {id}" : name,
            MountHorde = mountHorde, MountAlliance = mountAlliance, IsNew = true,
        });
        created = true;
        return id;
    }

    public Dictionary<string, byte[]> BuildPatchFiles()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (_pathNodeDbc == null) return files;

        foreach (var p in Paths)
        {
            if (!p.IsNew || p.FromNode != 0 || p.Points.Count < 2) continue;
            if (p.MountHorde == 0 && p.MountAlliance == 0) continue;
            string nm = string.IsNullOrWhiteSpace(p.CustomName) ? p.Name : p.CustomName;
            var a = p.Points[0]; var b = p.Points[^1];
            p.FromNode = EnsureNodeAt(p.Continent, a.X, a.Y, a.Z, nm, p.MountHorde, p.MountAlliance, out _);
            p.ToNode   = EnsureNodeAt(p.Continent, b.X, b.Y, b.Z, nm, p.MountHorde, p.MountAlliance, out _);
        }
        PruneOrphanNewNodes();

        var reverses = new List<(uint Id, Path Src)>();
        {
            uint nextPathId = 1;
            if (_pathDbc != null)
                for (int i = 0; i < _pathDbc.RecordCount; i++)
                    nextPathId = Math.Max(nextPathId, _pathDbc.GetU(i, 0) + 1);
            foreach (var p in Paths) nextPathId = Math.Max(nextPathId, p.Id + 1);

            var edges = new HashSet<(uint, uint)>();
            foreach (var p in Paths)
                if (p.FromNode != 0 && p.ToNode != 0) edges.Add((p.FromNode, p.ToNode));
            foreach (var p in Paths)
            {
                if (!p.IsNew || !p.TwoWay || p.FromNode == 0 || p.ToNode == 0) continue;
                if (edges.Contains((p.ToNode, p.FromNode))) continue;
                edges.Add((p.ToNode, p.FromNode));
                reverses.Add((nextPathId++, p));
            }
        }

        var cleanIds = new HashSet<uint>();
        var allIds = new HashSet<uint>();
        var regen = new List<Path>();
        foreach (var p in Paths)
        {
            allIds.Add(p.Id);
            if (p.Dirty || p.IsNew) regen.Add(p); else cleanIds.Add(p.Id);
        }

        int nrs = _pathNodeDbc.RecordSize;
        var nodeRecs = new List<byte[]>();
        uint maxNodeId = 0;
        for (int i = 0; i < _pathNodeDbc.RecordCount; i++)
        {
            uint nodeId = _pathNodeDbc.GetU(i, 0);
            if (nodeId > maxNodeId) maxNodeId = nodeId;
            if (cleanIds.Contains(_pathNodeDbc.GetU(i, 1))) nodeRecs.Add((byte[])_pathNodeDbc.Records[i].Clone());
        }
        uint nextId = maxNodeId + 1;
        foreach (var p in regen)
            for (int idx = 0; idx < p.Points.Count; idx++)
            {
                var wp = p.Points[idx];
                var r = new byte[nrs];
                WU(r, 0, nextId++);
                WU(r, 4, p.Id);
                WU(r, 8, (uint)idx);
                WU(r, 12, (uint)wp.MapId);
                WF(r, 16, wp.X);
                WF(r, 20, wp.Y);
                WF(r, 24, wp.Z);
                if (nrs > 28) WU(r, 28, wp.Flags);
                if (nrs > 32) WU(r, 32, wp.Delay);
                nodeRecs.Add(r);
            }
        foreach (var (rid, src) in reverses)
            for (int idx = 0; idx < src.Points.Count; idx++)
            {
                var wp = src.Points[src.Points.Count - 1 - idx];
                var r = new byte[nrs];
                WU(r, 0, nextId++);
                WU(r, 4, rid);
                WU(r, 8, (uint)idx);
                WU(r, 12, (uint)wp.MapId);
                WF(r, 16, wp.X);
                WF(r, 20, wp.Y);
                WF(r, 24, wp.Z);
                nodeRecs.Add(r);
            }
        files[@"DBFilesClient\TaxiPathNode.dbc"] =
            DbcFile.WriteRecords(nodeRecs, _pathNodeDbc.FieldCount, nrs, _pathNodeDbc.StringBlock);

        if (_pathDbc != null)
        {
            int prs = _pathDbc.RecordSize;
            var pathRecs = new List<byte[]>();
            var existing = new HashSet<uint>();
            for (int i = 0; i < _pathDbc.RecordCount; i++)
            {
                uint id = _pathDbc.GetU(i, 0);
                existing.Add(id);
                if (allIds.Contains(id)) pathRecs.Add((byte[])_pathDbc.Records[i].Clone());
            }
            foreach (var p in Paths)
            {
                if (existing.Contains(p.Id)) continue;
                var r = new byte[prs];
                WU(r, 0, p.Id);
                if (prs > 4)  WU(r, 4, p.FromNode);
                if (prs > 8)  WU(r, 8, p.ToNode);
                if (prs > 12) WU(r, 12, p.Cost);
                pathRecs.Add(r);
            }
            foreach (var (rid, src) in reverses)
            {
                var r = new byte[prs];
                WU(r, 0, rid);
                if (prs > 4)  WU(r, 4, src.ToNode);
                if (prs > 8)  WU(r, 8, src.FromNode);
                if (prs > 12) WU(r, 12, src.Cost);
                pathRecs.Add(r);
            }
            files[@"DBFilesClient\TaxiPath.dbc"] =
                DbcFile.WriteRecords(pathRecs, _pathDbc.FieldCount, prs, _pathDbc.StringBlock);
        }

        if (_nodesDbc != null && Nodes.Exists(n => n.IsNew))
        {
            int nrs2 = _nodesDbc.RecordSize;
            var recs = new List<byte[]>(_nodesDbc.RecordCount);
            for (int i = 0; i < _nodesDbc.RecordCount; i++)
                recs.Add((byte[])_nodesDbc.Records[i].Clone());

            var strings = new List<byte>(_nodesDbc.StringBlock);
            if (strings.Count == 0) strings.Add(0);
            uint locFlags = _nodesDbc.RecordCount > 0 && _nodesDbc.FieldCount > 13 ? _nodesDbc.GetU(0, 13) : 0;

            foreach (var n in Nodes)
            {
                if (!n.IsNew) continue;
                uint nameOfs = (uint)strings.Count;
                strings.AddRange(Encoding.UTF8.GetBytes(n.Name));
                strings.Add(0);

                var r = new byte[nrs2];
                WU(r, 0, n.Id);
                WU(r, 4, (uint)n.MapId);
                WF(r, 8, n.X); WF(r, 12, n.Y); WF(r, 16, n.Z);
                WU(r, 5 * 4, nameOfs);
                if (nrs2 > 13 * 4) WU(r, 13 * 4, locFlags);
                if (nrs2 > 14 * 4) WU(r, 14 * 4, n.MountHorde);
                if (nrs2 > 15 * 4) WU(r, 15 * 4, n.MountAlliance);
                recs.Add(r);
            }
            files[@"DBFilesClient\TaxiNodes.dbc"] =
                DbcFile.WriteRecords(recs, _nodesDbc.FieldCount, nrs2, strings.ToArray());
        }

        return files;
    }

    public string BuildTransportSql()
    {
        const float MoveSpeed = 30f;

        var sb = new StringBuilder();
        sb.Append("-- mangos-classic: register new ZEPPELIN/BOAT paths as moving transports.\n");
        sb.Append("-- A transport needs BOTH a gameobject_template (type 15, data0 = taxi path id) AND a row in\n");
        sb.Append("-- `transports` (entry, name, period) - the server only instantiates entries in that table.\n");
        sb.Append("-- It must have NO `gameobject` spawn row. The server reads TaxiPathNode.dbc from ITS OWN dbc\n");
        sb.Append("-- folder. Deploy the DBCs, run this SQL, then restart mangosd.\n");
        sb.Append("-- The period below is estimated GENEROUSLY (length/speed + pauses + accel ramps + 10%):\n");
        sb.Append("-- a period longer than the real loop time just parks the vehicle at its last stop until the\n");
        sb.Append("-- schedule catches up, but a period that is TOO SHORT makes it teleport to stay on schedule.\n\n");
        sb.Append("SET @ENTRY = (SELECT entry FROM (SELECT MAX(entry) AS entry FROM gameobject_template) t);\n\n");

        bool any = false;
        int i = 0;
        foreach (var p in Paths)
        {
            if (!p.IsNew || p.MountHorde != 0 || p.MountAlliance != 0) continue;
            any = true;
            string nm = p.Name.Replace("'", "''");
            uint display = p.TransportKind == "zeppelin" ? 3031u : 3015u;

            double len = 0;
            int stops = 0;
            for (int k = 0; k < p.Points.Count; k++)
            {
                var a = p.Points[k];
                var b = p.Points[(k + 1) % p.Points.Count];
                len += Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
                if (a.Flags == 2) stops++;
            }
            double seconds = len / MoveSpeed;
            foreach (var w in p.Points) seconds += w.Delay;
            seconds += (stops + 1) * MoveSpeed;
            long period = (long)(seconds * 1.1 * 1000);

            sb.Append($"-- Path {p.Id} \"{nm}\" ({(p.TransportKind.Length > 0 ? p.TransportKind : "transport")}): " +
                      $"{p.Points.Count} waypoint(s), map {p.Continent}, ~{len:F0} yd loop\n");
            sb.Append("INSERT INTO gameobject_template (entry, type, displayId, name, faction, flags, size, data0, data1, data2) VALUES\n");
            sb.Append($"(@ENTRY+{++i}, 15, {display}, '{nm}', 0, 40, 1.0, {p.Id} /*taxiPathId*/, {(int)MoveSpeed} /*moveSpeed*/, 1 /*accelRate*/);\n");
            sb.Append($"INSERT INTO transports (entry, name, period) VALUES (@ENTRY+{i}, '{nm}', {period});\n\n");
        }
        if (!any)
            sb.Append("-- (No NEW transport paths in this export. Taxi routes are handled by flightmasters.sql;\n" +
                      "--  to make an existing path id a transport, add a type-15 gameobject_template with data0 =\n" +
                      "--  that path id plus a `transports` row.)\n");
        return sb.ToString();
    }

    public bool RouteConnectsToNetwork(Path p)
    {
        if (p.FromNode == 0 || p.ToNode == 0) return true;
        var adj = new Dictionary<uint, List<uint>>();
        foreach (var q in Paths)
        {
            if (q.FromNode == 0 || q.ToNode == 0) continue;
            (adj.TryGetValue(q.FromNode, out var l1) ? l1 : adj[q.FromNode] = new List<uint>()).Add(q.ToNode);
            (adj.TryGetValue(q.ToNode, out var l2) ? l2 : adj[q.ToNode] = new List<uint>()).Add(q.FromNode);
        }
        var newIds = new HashSet<uint>();
        foreach (var n in Nodes) if (n.IsNew) newIds.Add(n.Id);

        var seen = new HashSet<uint> { p.FromNode };
        var stack = new Stack<uint>();
        stack.Push(p.FromNode);
        while (stack.Count > 0)
        {
            uint id = stack.Pop();
            if (!newIds.Contains(id)) return true;
            if (adj.TryGetValue(id, out var next))
                foreach (var nx in next)
                    if (seen.Add(nx)) stack.Push(nx);
        }
        return false;
    }

    public int PruneOrphanNewNodes()
    {
        var used = new HashSet<uint>();
        foreach (var p in Paths) { used.Add(p.FromNode); used.Add(p.ToNode); }
        return Nodes.RemoveAll(n => n.IsNew && !used.Contains(n.Id));
    }

    public string BuildFlightMasterSql()
    {
        static string F(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("-- mangos-classic: flight masters for the NEW taxi nodes in this export.\n");
        sb.Append("-- A flight master is any creature with npcflag & 0x8 standing near the TaxiNodes position\n");
        sb.Append("-- (the server picks the NEAREST node on the map - no explicit node link exists).\n");
        sb.Append("-- Stock templates are reused below; change the entries if you want different NPCs.\n");
        sb.Append("-- Players must talk to the NPC once to learn the node (or use .taxicheat on to test).\n");
        sb.Append("-- DUPLICATE-SAFE: each spawn is skipped when a creature already stands within 1 yd of\n");
        sb.Append("-- its position (position check, not entry), so the script can be applied repeatedly.\n\n");
        sb.Append("SET @FM_ALLIANCE = 352;   -- Dungar Longdrink (Stormwind gryphon master) - replace as desired\n");
        sb.Append("SET @FM_HORDE    = 3310;  -- Devrak (Orgrimmar wind rider master)        - replace as desired\n");
        sb.Append("SET @GUID = (SELECT guid FROM (SELECT MAX(guid) AS guid FROM creature) g);\n\n");

        int i = 0;
        bool any = false;
        foreach (var n in Nodes)
        {
            if (!n.IsNew) continue;
            any = true;
            sb.Append($"-- Node {n.Id} \"{n.Name.Replace("'", "''")}\"  map {n.MapId}  ({F(n.X)}, {F(n.Y)}, {F(n.Z)})\n");
            void Spawn(string entry, float x, float y, float z) =>
                sb.Append("INSERT INTO creature (guid, id, map, spawnMask, position_x, position_y, position_z, " +
                          "orientation, spawntimesecsmin, spawntimesecsmax, spawndist, MovementType)\n" +
                          $"SELECT @GUID+{++i}, {entry}, {n.MapId}, 1, {F(x)}, {F(y)}, {F(z)}, 0, 120, 120, 0, 0 FROM DUAL\n" +
                          $"WHERE NOT EXISTS (SELECT 1 FROM creature c WHERE c.map = {n.MapId}\n" +
                          $"    AND ABS(c.position_x - {F(x)}) < 1 AND ABS(c.position_y - {F(y)}) < 1 " +
                          $"AND ABS(c.position_z - {F(z)}) < 1);\n");
            if (n.MountAlliance != 0) Spawn("@FM_ALLIANCE", n.X, n.Y, n.Z);
            if (n.MountHorde != 0) Spawn("@FM_HORDE", n.X + 2f, n.Y, n.Z);
            if (n.MountHorde == 0 && n.MountAlliance == 0)
                sb.Append("-- (no mount creature set - node is invisible to GetNearestTaxiNode; set a mount pair in the wizard)\n");
            sb.Append('\n');
        }
        foreach (var p in Paths)
        {
            if (!p.IsNew || p.FromNode == 0) continue;
            sb.Append($"-- Route {p.Id} \"{p.Name.Replace("'", "''")}\": node {p.FromNode} → {p.ToNode}, cost {p.Cost} copper.\n");
        }
        if (!any)
            sb.Append("-- (No new taxi nodes in this export - routes reused existing nodes, or only existing paths were edited.)\n");
        return sb.ToString();
    }

    private static void WU(byte[] b, int o, uint v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void WF(byte[] b, int o, float v) => BitConverter.GetBytes(v).CopyTo(b, o);

    private static string ReadStr(byte[] block, uint offset)
    {
        if (offset == 0 || offset >= (uint)block.Length) return "";
        int end = (int)offset;
        while (end < block.Length && block[end] != 0) end++;
        return Encoding.UTF8.GetString(block, (int)offset, end - (int)offset);
    }
}
