using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class AreaTable
{
    private const int F_Id = 0;
    private const int F_Map = 1;
    private const int F_Zone = 2;
    private const int F_Flag = 3;
    private const int F_Ambience = 7;
    private const int F_Music = 8;
    private const int F_Intro = 9;
    private const int F_Name = 11;

    private DbcFile _dbc = null!;
    private readonly List<(int Id, string Name)> _named = new();
    private readonly Dictionary<int, string> _byId = new();
    private readonly Dictionary<int, ushort> _flagById = new();

    public sealed record NewZone(int Id, string Name, int MapId, ushort Flag,
                                 uint Music, uint Ambience, uint IntroSound);

    public List<NewZone> NewZones { get; } = new();

    public IReadOnlyList<(int Id, string Name)> Named => _named;

    public string? Name(int id) =>
        _byId.TryGetValue(id, out var n) && n.Length > 0 ? n : null;

    public ushort ExploreFlag(int id) => _flagById.TryGetValue(id, out var f) ? f : (ushort)0xFFFF;

    public static AreaTable Load(Func<string, Stream> open)
    {
        var at = new AreaTable();
        using (var s = open(@"DBFilesClient\AreaTable.dbc")) at._dbc = DbcFile.Read(s);
        for (int i = 0; i < at._dbc.RecordCount; i++)
        {
            int id = (int)at._dbc.GetU(i, F_Id);
            string name = ReadStr(at._dbc.StringBlock, at._dbc.GetU(i, F_Name));
            at._byId[id] = name;
            at._flagById[id] = (ushort)at._dbc.GetU(i, F_Flag);
            if (name.Length > 0) at._named.Add((id, name));
        }
        at._named.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return at;
    }

    public int AddZone(string name, int mapId, uint music, uint ambience, uint introSound)
    {
        int maxId = 0;
        ushort maxFlag = 0;
        for (int i = 0; i < _dbc.RecordCount; i++)
        {
            maxId = Math.Max(maxId, (int)_dbc.GetU(i, F_Id));
            maxFlag = Math.Max(maxFlag, (ushort)_dbc.GetU(i, F_Flag));
        }
        foreach (var z in NewZones)
        {
            maxId = Math.Max(maxId, z.Id);
            maxFlag = Math.Max(maxFlag, z.Flag);
        }
        var nz = new NewZone(maxId + 1, name.Trim(), mapId, (ushort)(maxFlag + 1), music, ambience, introSound);
        NewZones.Add(nz);
        _byId[nz.Id] = nz.Name;
        _flagById[nz.Id] = nz.Flag;
        int at = _named.FindIndex(e => string.Compare(e.Name, nz.Name, StringComparison.OrdinalIgnoreCase) > 0);
        _named.Insert(at < 0 ? _named.Count : at, (nz.Id, nz.Name));
        return nz.Id;
    }

    public void RestoreZone(NewZone z)
    {
        if (_byId.ContainsKey(z.Id)) return;
        NewZones.Add(z);
        _byId[z.Id] = z.Name;
        _flagById[z.Id] = z.Flag;
        int at = _named.FindIndex(e => string.Compare(e.Name, z.Name, StringComparison.OrdinalIgnoreCase) > 0);
        _named.Insert(at < 0 ? _named.Count : at, (z.Id, z.Name));
    }

    public byte[]? BuildPatchFile()
    {
        if (NewZones.Count == 0) return null;
        var recs = new List<byte[]>(_dbc.RecordCount + NewZones.Count);
        for (int i = 0; i < _dbc.RecordCount; i++) recs.Add((byte[])_dbc.Records[i].Clone());

        var strings = new List<byte>(_dbc.StringBlock);
        if (strings.Count == 0) strings.Add(0);
        uint locFlags = _dbc.RecordCount > 0 ? _dbc.GetU(0, 19) : 0;

        foreach (var z in NewZones)
        {
            uint nameOfs = (uint)strings.Count;
            strings.AddRange(Encoding.UTF8.GetBytes(z.Name));
            strings.Add(0);

            var r = new byte[_dbc.RecordSize];
            void W(int field, uint v) => BitConverter.GetBytes(v).CopyTo(r, field * 4);
            W(F_Id, (uint)z.Id);
            W(F_Map, (uint)z.MapId);
            W(F_Zone, (uint)z.Id);
            W(F_Flag, z.Flag);
            W(F_Ambience, z.Ambience);
            W(F_Music, z.Music);
            W(F_Intro, z.IntroSound);
            W(F_Name, nameOfs);
            if (_dbc.FieldCount > 19) W(19, locFlags);
            recs.Add(r);
        }
        return DbcFile.WriteRecords(recs, _dbc.FieldCount, _dbc.RecordSize, strings.ToArray());
    }

    private static string ReadStr(byte[] block, uint offset)
    {
        if (offset == 0 || offset >= (uint)block.Length) return "";
        int e = (int)offset; while (e < block.Length && block[e] != 0) e++;
        return Encoding.UTF8.GetString(block, (int)offset, e - (int)offset);
    }
}
