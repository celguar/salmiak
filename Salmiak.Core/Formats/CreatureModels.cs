using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class CreatureModels
{
    private DbcFile _cdi = null!;
    private DbcFile _cmd = null!;
    private DbcFile? _cdie;
    private readonly Dictionary<uint, int> _cdiById = new();
    private readonly Dictionary<uint, int> _cmdById = new();
    private readonly Dictionary<uint, int> _cdieById = new();

    private readonly Dictionary<(uint Race, uint Sex, uint Var, uint Color), string> _hairTex = new();
    private readonly Dictionary<(uint Race, uint Sex, uint Var), int> _hairGeo = new();

    public static CreatureModels Load(Func<string, Stream> open)
    {
        var c = new CreatureModels();
        using (var s = open(@"DBFilesClient\CreatureDisplayInfo.dbc")) c._cdi = DbcFile.Read(s);
        using (var s = open(@"DBFilesClient\CreatureModelData.dbc")) c._cmd = DbcFile.Read(s);
        for (int i = 0; i < c._cdi.RecordCount; i++) c._cdiById[c._cdi.GetU(i, 0)] = i;
        for (int i = 0; i < c._cmd.RecordCount; i++) c._cmdById[c._cmd.GetU(i, 0)] = i;
        try
        {
            using var s = open(@"DBFilesClient\CreatureDisplayInfoExtra.dbc");
            c._cdie = DbcFile.Read(s);
            for (int i = 0; i < c._cdie.RecordCount; i++) c._cdieById[c._cdie.GetU(i, 0)] = i;
        }
        catch { }
        try
        {
            DbcFile cs;
            using (var s = open(@"DBFilesClient\CharSections.dbc")) cs = DbcFile.Read(s);
            for (int i = 0; i < cs.RecordCount; i++)
            {
                if (cs.GetU(i, 3) != 3) continue;
                string tex = ReadStr(cs.StringBlock, cs.GetU(i, 6));
                if (tex.Length > 0)
                    c._hairTex[(cs.GetU(i, 1), cs.GetU(i, 2), cs.GetU(i, 4), cs.GetU(i, 5))] = tex.Replace('/', '\\');
            }
            DbcFile hg;
            using (var s = open(@"DBFilesClient\CharHairGeosets.dbc")) hg = DbcFile.Read(s);
            for (int i = 0; i < hg.RecordCount; i++)
                c._hairGeo[(hg.GetU(i, 1), hg.GetU(i, 2), hg.GetU(i, 3))] = hg.GetI(i, 4);
        }
        catch { }
        return c;
    }

    public sealed record Display(string ModelPath, string? Skin, string? Hair, int HairGeoset,
                                 bool Character, float Scale);

    public Display? Resolve(uint displayId)
    {
        if (!_cdiById.TryGetValue(displayId, out int ci)) return null;
        uint modelId = _cdi.GetU(ci, 1);
        float scale = _cdi.GetF(ci, 4);
        if (scale <= 0f) scale = 1f;
        if (!_cmdById.TryGetValue(modelId, out int mi)) return null;
        string modelPath = ReadStr(_cmd.StringBlock, _cmd.GetU(mi, 2)).Replace('/', '\\');
        if (modelPath.Length == 0) return null;

        uint extra = _cdi.FieldCount > 3 ? _cdi.GetU(ci, 3) : 0;
        if (extra != 0 && _cdie != null && _cdieById.TryGetValue(extra, out int ei))
        {
            string bake = ReadStr(_cdie.StringBlock, _cdie.GetU(ei, _cdie.FieldCount - 1));
            if (bake.Length > 0)
            {
                if (!bake.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) bake += ".blp";
                uint race = _cdie.GetU(ei, 1), sex = _cdie.GetU(ei, 2);
                uint hairStyle = _cdie.GetU(ei, 5), hairColor = _cdie.GetU(ei, 6);
                _hairTex.TryGetValue((race, sex, hairStyle, hairColor), out string? hair);
                int hairGeo = _hairGeo.TryGetValue((race, sex, hairStyle), out int g) ? g : 0;
                return new Display(modelPath, @"Textures\BakedNpcTextures\" + bake,
                    hair, hairGeo, Character: true, scale);
            }
        }

        string? skin = null;
        for (int f = 6; f <= 8 && f < _cdi.FieldCount; f++)
        {
            string variation = ReadStr(_cdi.StringBlock, _cdi.GetU(ci, f));
            if (variation.Length == 0) continue;
            string dir = Path.GetDirectoryName(modelPath) ?? "";
            skin = (dir.Length > 0 ? dir + "\\" : "") + variation + ".blp";
            break;
        }
        return new Display(modelPath, skin, null, -1, Character: false, scale);
    }

    public static Dictionary<string, int> LoadMapIds(Func<string, Stream> open)
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var s = open(@"DBFilesClient\Map.dbc");
        var map = DbcFile.Read(s);
        for (int i = 0; i < map.RecordCount; i++)
        {
            string dir = ReadStr(map.StringBlock, map.GetU(i, 1));
            if (dir.Length > 0) ids[dir] = map.GetI(i, 0);
        }
        return ids;
    }

    private static string ReadStr(byte[] block, uint offset)
    {
        if (offset == 0 || offset >= (uint)block.Length) return "";
        int end = (int)offset;
        while (end < block.Length && block[end] != 0) end++;
        return Encoding.UTF8.GetString(block, (int)offset, end - (int)offset);
    }
}
