using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class GameObjectModels
{
    public const int LockHerbalism = 2;
    public const int LockMining = 3;

    private readonly Dictionary<uint, string> _displays = new();
    private readonly Dictionary<uint, (int LockType, int ReqSkill)> _locks = new();

    public static GameObjectModels Load(Func<string, Stream> open)
    {
        var g = new GameObjectModels();
        DbcFile d;
        using (var s = open(@"DBFilesClient\GameObjectDisplayInfo.dbc")) d = DbcFile.Read(s);
        for (int i = 0; i < d.RecordCount; i++)
        {
            string path = ReadStr(d.StringBlock, d.GetU(i, 1));
            if (path.Length > 0) g._displays[d.GetU(i, 0)] = path.Replace('/', '\\');
        }
        using (var s = open(@"DBFilesClient\Lock.dbc")) d = DbcFile.Read(s);
        for (int i = 0; i < d.RecordCount; i++)
        {
            for (int slot = 0; slot < 8; slot++)
            {
                if (d.GetU(i, 1 + slot) != 2) continue;
                g._locks[d.GetU(i, 0)] = ((int)d.GetU(i, 9 + slot), (int)d.GetU(i, 17 + slot));
                break;
            }
        }
        return g;
    }

    public string? DisplayModel(uint displayId) =>
        _displays.TryGetValue(displayId, out var p) ? p : null;

    public bool LockInfo(uint lockId, out int lockType, out int reqSkill)
    {
        if (_locks.TryGetValue(lockId, out var v)) { lockType = v.LockType; reqSkill = v.ReqSkill; return true; }
        lockType = 0; reqSkill = 0;
        return false;
    }

    private static string ReadStr(byte[] block, uint offset)
    {
        if (offset == 0 || offset >= (uint)block.Length) return "";
        int e = (int)offset; while (e < block.Length && block[e] != 0) e++;
        return Encoding.UTF8.GetString(block, (int)offset, e - (int)offset);
    }
}
