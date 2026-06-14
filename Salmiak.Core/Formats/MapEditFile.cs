using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Salmiak.Core.Formats;

public static class MapEditFile
{
    private const uint Magic = 0x44454D57;
    private const int Version = 5;

    public readonly record struct CameraPose(Vector3 Position, float Yaw, float Pitch);

    public sealed record FlightEdits(List<FlightPaths.Node> Nodes, List<FlightPaths.Path> Paths)
    {
        public static readonly FlightEdits Empty = new(new List<FlightPaths.Node>(), new List<FlightPaths.Path>());
    }

    public static void Save(Stream stream, string mapName, IReadOnlyDictionary<(int X, int Y), AdtFile> tiles,
                            CameraPose camera, IReadOnlyList<(string Path, byte[] Bytes)>? overrides = null,
                            IReadOnlyList<AreaTable.NewZone>? zones = null, FlightPaths? flight = null)
    {
        using var gz = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        using var w = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);

        w.Write(Magic);
        w.Write(Version);
        w.Write(mapName);
        w.Write(camera.Position.X); w.Write(camera.Position.Y); w.Write(camera.Position.Z);
        w.Write(camera.Yaw); w.Write(camera.Pitch);

        overrides ??= System.Array.Empty<(string, byte[])>();
        w.Write(overrides.Count);
        foreach (var (path, bytes) in overrides)
        {
            w.Write(path);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        w.Write(tiles.Count);

        foreach (var ((tx, ty), adt) in tiles)
        {
            w.Write(tx);
            w.Write(ty);

            w.Write(adt.Textures.Count);
            foreach (var t in adt.Textures) w.Write(t);

            w.Write(adt.Doodads.Count);
            foreach (var d in adt.Doodads) WriteDoodad(w, d);

            w.Write(adt.Wmos.Count);
            foreach (var m in adt.Wmos) WriteWmo(w, m);

            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
            {
                var ch = adt.Chunks[cy, cx];
                w.Write(ch != null);
                if (ch != null) WriteChunk(w, ch);
            }
        }

        zones ??= System.Array.Empty<AreaTable.NewZone>();
        w.Write(zones.Count);
        foreach (var z in zones)
        {
            w.Write(z.Id); w.Write(z.Name); w.Write(z.MapId); w.Write(z.Flag);
            w.Write(z.Music); w.Write(z.Ambience); w.Write(z.IntroSound);
        }

        var nodes = new List<FlightPaths.Node>();
        var paths = new List<FlightPaths.Path>();
        if (flight != null)
        {
            foreach (var n in flight.Nodes) if (n.IsNew) nodes.Add(n);
            foreach (var p in flight.Paths) if (p.IsNew || p.Dirty) paths.Add(p);
        }
        w.Write(nodes.Count);
        foreach (var n in nodes)
        {
            w.Write(n.Id); w.Write(n.MapId); w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
            w.Write(n.Name); w.Write(n.MountHorde); w.Write(n.MountAlliance);
        }
        w.Write(paths.Count);
        foreach (var p in paths)
        {
            w.Write(p.Id); w.Write(p.Continent); w.Write(p.Name); w.Write(p.CustomName);
            w.Write(p.Cost); w.Write(p.MountHorde); w.Write(p.MountAlliance);
            w.Write(p.FromNode); w.Write(p.ToNode); w.Write(p.TwoWay); w.Write(p.ReverseName);
            w.Write(p.TransportKind); w.Write(p.IsNew); w.Write(p.Dirty);
            w.Write(p.Points.Count);
            foreach (var wp in p.Points)
            {
                w.Write(wp.X); w.Write(wp.Y); w.Write(wp.Z);
                w.Write(wp.MapId); w.Write(wp.Flags); w.Write(wp.Delay);
            }
        }
    }

    public static Dictionary<(int X, int Y), AdtFile> Load(Stream stream, out string mapName, out CameraPose? camera,
                                                           out List<(string Path, byte[] Bytes)> overrides)
        => Load(stream, out mapName, out camera, out overrides, out _, out _);

    public static Dictionary<(int X, int Y), AdtFile> Load(Stream stream, out string mapName, out CameraPose? camera,
                                                           out List<(string Path, byte[] Bytes)> overrides,
                                                           out List<AreaTable.NewZone> zones, out FlightEdits flight)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);

        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a Salmiak project file.");
        int version = r.ReadInt32();
        if (version < 1 || version > Version) throw new InvalidDataException($"Unsupported project version {version}.");
        mapName = r.ReadString();
        camera = version >= 2
            ? new CameraPose(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle(), r.ReadSingle())
            : null;

        overrides = new List<(string, byte[])>();
        if (version >= 3)
        {
            int nOv = r.ReadInt32();
            for (int i = 0; i < nOv; i++)
            {
                string path = r.ReadString();
                int len = r.ReadInt32();
                overrides.Add((path, r.ReadBytes(len)));
            }
        }

        int tileCount = r.ReadInt32();

        var result = new Dictionary<(int, int), AdtFile>();
        for (int i = 0; i < tileCount; i++)
        {
            int tx = r.ReadInt32(), ty = r.ReadInt32();
            var adt = new AdtFile();

            int nTex = r.ReadInt32();
            for (int t = 0; t < nTex; t++) adt.Textures.Add(r.ReadString());

            int nDood = r.ReadInt32();
            for (int d = 0; d < nDood; d++) adt.Doodads.Add(ReadDoodad(r));

            int nWmo = r.ReadInt32();
            for (int m = 0; m < nWmo; m++) adt.Wmos.Add(ReadWmo(r));

            for (int cy = 0; cy < AdtFile.ChunksPerSide; cy++)
            for (int cx = 0; cx < AdtFile.ChunksPerSide; cx++)
                if (r.ReadBoolean())
                    adt.Chunks[cy, cx] = ReadChunk(r, cx, cy, version);

            result[(tx, ty)] = adt;
        }

        zones = new List<AreaTable.NewZone>();
        var fNodes = new List<FlightPaths.Node>();
        var fPaths = new List<FlightPaths.Path>();
        flight = new FlightEdits(fNodes, fPaths);
        if (version >= 5)
        {
            int nZones = r.ReadInt32();
            for (int i = 0; i < nZones; i++)
                zones.Add(new AreaTable.NewZone(r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadUInt16(),
                                                r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));

            int nNodes = r.ReadInt32();
            for (int i = 0; i < nNodes; i++)
                fNodes.Add(new FlightPaths.Node
                {
                    Id = r.ReadUInt32(), MapId = r.ReadInt32(),
                    X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(),
                    Name = r.ReadString(), MountHorde = r.ReadUInt32(), MountAlliance = r.ReadUInt32(),
                    IsNew = true,
                });

            int nPaths = r.ReadInt32();
            for (int i = 0; i < nPaths; i++)
            {
                var p = new FlightPaths.Path
                {
                    Id = r.ReadUInt32(), Continent = r.ReadInt32(), Name = r.ReadString(),
                    CustomName = r.ReadString(), Cost = r.ReadUInt32(),
                    MountHorde = r.ReadUInt32(), MountAlliance = r.ReadUInt32(),
                    FromNode = r.ReadUInt32(), ToNode = r.ReadUInt32(),
                    TwoWay = r.ReadBoolean(), ReverseName = r.ReadString(),
                    TransportKind = r.ReadString(), IsNew = r.ReadBoolean(), Dirty = r.ReadBoolean(),
                };
                int nWp = r.ReadInt32();
                for (int k = 0; k < nWp; k++)
                    p.Points.Add(new FlightPaths.Waypoint(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                                                          r.ReadInt32(), r.ReadUInt32(), r.ReadUInt32()));
                fPaths.Add(p);
            }
        }
        return result;
    }

    private static void WriteChunk(BinaryWriter w, MapChunk ch)
    {
        w.Write(ch.Position.X); w.Write(ch.Position.Y); w.Write(ch.Position.Z);
        w.Write(ch.Holes);
        w.Write(ch.Flags);
        w.Write(ch.AreaId);
        w.Write(ch.NLayers);
        for (int i = 0; i < MapChunk.HeightCount; i++) w.Write(ch.Heights[i]);
        for (int l = 0; l < ch.NLayers; l++) w.Write(ch.Layers[l].TextureIndex);
        for (int i = 0; i < 3; i++)
        {
            var a = ch.AlphaMaps[i];
            w.Write(a != null);
            if (a != null) w.Write(a, 0, a.Length);
        }
        w.Write(ch.Liquid != null);
        if (ch.Liquid != null) WriteLiquid(w, ch.Liquid);
        w.Write(ch.ShadowMap != null);
        if (ch.ShadowMap != null) w.Write(ch.ShadowMap, 0, 4096);
    }

    private static MapChunk ReadChunk(BinaryReader r, int cx, int cy, int version)
    {
        var ch = new MapChunk
        {
            IndexX = cx,
            IndexY = cy,
            Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            Holes = r.ReadUInt16(),
            Flags = r.ReadUInt32(),
            AreaId = r.ReadInt32(),
            NLayers = r.ReadInt32(),
        };
        for (int i = 0; i < MapChunk.HeightCount; i++) ch.Heights[i] = r.ReadSingle();
        for (int l = 0; l < ch.NLayers; l++) ch.Layers[l] = new TextureLayer { TextureIndex = r.ReadUInt32() };
        for (int i = 0; i < 3; i++)
            if (r.ReadBoolean()) ch.AlphaMaps[i] = r.ReadBytes(4096);
        if (r.ReadBoolean())
        {
            var liq = new LiquidLayer { Type = r.ReadInt32(), MinHeight = r.ReadSingle(), MaxHeight = r.ReadSingle() };
            for (int i = 0; i < liq.Heights.Length; i++) liq.Heights[i] = r.ReadSingle();
            for (int i = 0; i < liq.Render.Length; i++) liq.Render[i] = r.ReadBoolean();
            ch.Liquid = liq;
        }
        if (version >= 4 && r.ReadBoolean()) ch.ShadowMap = r.ReadBytes(4096);
        return ch;
    }

    private static void WriteLiquid(BinaryWriter w, LiquidLayer liq)
    {
        w.Write(liq.Type); w.Write(liq.MinHeight); w.Write(liq.MaxHeight);
        foreach (var h in liq.Heights) w.Write(h);
        foreach (var b in liq.Render) w.Write(b);
    }

    private static void WriteDoodad(BinaryWriter w, DoodadDef d)
    {
        w.Write(d.ModelPath ?? "");
        w.Write(d.Position.X); w.Write(d.Position.Y); w.Write(d.Position.Z);
        w.Write(d.Rotation.X); w.Write(d.Rotation.Y); w.Write(d.Rotation.Z);
        w.Write(d.Scale);
        w.Write(d.UniqueId);
        w.Write(d.Flags);
    }

    private static DoodadDef ReadDoodad(BinaryReader r) => new()
    {
        ModelPath = r.ReadString(),
        Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        Rotation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        Scale = r.ReadSingle(),
        UniqueId = r.ReadUInt32(),
        Flags = r.ReadUInt16(),
    };

    private static void WriteWmo(BinaryWriter w, WmoDef m)
    {
        w.Write(m.ModelPath ?? "");
        w.Write(m.Position.X); w.Write(m.Position.Y); w.Write(m.Position.Z);
        w.Write(m.Rotation.X); w.Write(m.Rotation.Y); w.Write(m.Rotation.Z);
        w.Write(m.UniqueId);
        w.Write(m.Flags);
        w.Write(m.DoodadSet);
    }

    private static WmoDef ReadWmo(BinaryReader r) => new()
    {
        ModelPath = r.ReadString(),
        Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        Rotation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        UniqueId = r.ReadUInt32(),
        Flags = r.ReadUInt16(),
        DoodadSet = r.ReadUInt16(),
    };
}
