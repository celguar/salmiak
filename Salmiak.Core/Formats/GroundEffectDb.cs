using System;
using System.Collections.Generic;
using System.IO;

namespace Salmiak.Core.Formats;

public sealed class GroundEffectDb
{
    public sealed record Effect(uint Id, IReadOnlyList<string> Models, int Density);

    private readonly Dictionary<uint, Effect> _effects = new();

    public Effect? Get(uint effectId) => _effects.TryGetValue(effectId, out var e) ? e : null;

    public int Count => _effects.Count;

    public static GroundEffectDb Load(Func<string, Stream> open)
    {
        var db = new GroundEffectDb();
        DbcFile tex, dood;
        using (var s = open(@"DBFilesClient\GroundEffectTexture.dbc")) tex = DbcFile.Read(s);
        using (var s = open(@"DBFilesClient\GroundEffectDoodad.dbc")) dood = DbcFile.Read(s);

        var doodModel = new Dictionary<uint, string>();
        for (int i = 0; i < dood.RecordCount; i++)
        {
            string mp = ResolveModelPath(dood.GetString(i, 2));
            if (mp.Length > 0) doodModel[dood.GetU(i, 0)] = mp;
        }

        for (int i = 0; i < tex.RecordCount; i++)
        {
            var models = new List<string>();
            for (int d = 1; d <= 4; d++)
            {
                uint did = tex.GetU(i, d);
                if (did == 0 || did == 0xFFFFFFFF) continue;
                if (doodModel.TryGetValue(did, out var mp) && !models.Contains(mp)) models.Add(mp);
            }
            if (models.Count > 0)
                db._effects[tex.GetU(i, 0)] = new Effect(tex.GetU(i, 0), models, (int)tex.GetU(i, 5));
        }
        return db;
    }

    public static string ResolveModelPath(string mdlName)
    {
        if (string.IsNullOrEmpty(mdlName)) return "";
        return $@"World\NoDXT\Detail\{Path.GetFileNameWithoutExtension(mdlName)}.m2";
    }
}
