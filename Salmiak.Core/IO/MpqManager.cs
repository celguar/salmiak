using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Salmiak.Core.IO;

public sealed class MpqManager : IDisposable
{
    private readonly List<(string Name, WowMpqArchive Archive)> _archives = new();

    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    private readonly Dictionary<string, byte[]> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public void SetFileOverride(string mpqPath, byte[] data) { lock (_gate) _overrides[mpqPath] = data; }

    public void RemoveFileOverride(string mpqPath) { lock (_gate) _overrides.Remove(mpqPath); }

    public IReadOnlyList<(string Path, byte[] Bytes)> Overrides
    {
        get { lock (_gate) return _overrides.Select(kv => (kv.Key, kv.Value)).ToList(); }
    }

    private static readonly string[] BaseOrder =
    [
        "base.MPQ", "dbc.MPQ", "fonts.MPQ", "interface.MPQ", "misc.MPQ",
        "model.MPQ", "sound.MPQ", "speech.MPQ", "terrain.MPQ", "texture.MPQ", "wmo.MPQ",
    ];

    public string DiagnosticLog { get; private set; } = string.Empty;

    public IReadOnlyList<(string Name, bool Enabled)> ListArchives()
    {
        lock (_gate) return _archives.Select(a => (a.Name, !_disabled.Contains(a.Name))).ToList();
    }

    public bool SetArchiveEnabled(string name, bool enabled)
    {
        lock (_gate) return enabled ? _disabled.Remove(name) : _disabled.Add(name);
    }

    public static MpqManager OpenWoWDirectory(string wowDataPath)
    {
        var mgr = new MpqManager();
        var log = new System.Text.StringBuilder();

        var present = Directory.Exists(wowDataPath)
            ? Directory.GetFiles(wowDataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
                .ToDictionary(p => Path.GetFileName(p)!, p => p, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var ordered = new List<string>();
        foreach (var name in BaseOrder)
            if (present.ContainsKey(name)) ordered.Add(name);
        var patches = present.Keys
            .Where(n => !BaseOrder.Contains(n, StringComparer.OrdinalIgnoreCase) && IsPatchName(n))
            .OrderBy(PatchPriority).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(patches);

        foreach (var name in ordered)
        {
            try
            {
                mgr._archives.Add((name, new WowMpqArchive(present[name])));
                log.AppendLine($"OK   {name}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"ERR  {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        foreach (var name in present.Keys.Where(n => !ordered.Contains(n, StringComparer.OrdinalIgnoreCase)))
            log.AppendLine($"SKIP {name} (not a stock base or patch-* archive)");

        mgr.DiagnosticLog = log.ToString();
        if (mgr._archives.Count == 0)
            throw new DirectoryNotFoundException($"No MPQ archives found in {wowDataPath}");
        return mgr;
    }

    private static bool IsPatchName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^patch(-.+)?\.mpq$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static double PatchPriority(string name)
    {
        string n = name.ToLowerInvariant();
        if (n == "patch.mpq") return 0;
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^patch-(\d+)\.mpq$");
        if (m.Success) return double.Parse(m.Groups[1].Value);
        m = System.Text.RegularExpressions.Regex.Match(n, @"^patch-([a-z])\.mpq$");
        if (m.Success) return 1000 + (m.Groups[1].Value[0] - 'a');
        return 2000;
    }

    public bool TryOpenFile(string mpqPath, out Stream stream)
    {
        lock (_gate)
        {
            if (_overrides.TryGetValue(mpqPath, out var ov)) { stream = new MemoryStream(ov, writable: false); return true; }
            for (int i = _archives.Count - 1; i >= 0; i--)
            {
                if (_disabled.Contains(_archives[i].Name)) continue;
                if (!_archives[i].Archive.FileExists(mpqPath)) continue;
                try
                {
                    stream = _archives[i].Archive.OpenFile(mpqPath);
                    return true;
                }
                catch { }
            }
            stream = Stream.Null;
            return false;
        }
    }

    public Stream OpenFile(string mpqPath)
    {
        if (!TryOpenFile(mpqPath, out var stream))
            throw new FileNotFoundException($"File not found in any MPQ archive: {mpqPath}");
        return stream;
    }

    public bool FileExists(string mpqPath)
    {
        lock (_gate)
        {
            if (_overrides.ContainsKey(mpqPath)) return true;
            for (int i = _archives.Count - 1; i >= 0; i--)
                if (!_disabled.Contains(_archives[i].Name) && _archives[i].Archive.FileExists(mpqPath)) return true;
            return false;
        }
    }

    public List<string> ListMaps()
    {
        var maps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (name, arc) in _archives)
            {
                if (_disabled.Contains(name)) continue;
                foreach (var raw in arc.ListFiles())
                {
                    var p = raw.Replace('/', '\\');
                    if (!p.StartsWith(@"World\Maps\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.EndsWith(".wdt", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = p.Split('\\');
                    if (parts.Length != 4) continue;
                    if (string.Equals(parts[3], parts[2] + ".wdt", StringComparison.OrdinalIgnoreCase))
                        maps.Add(parts[2]);
                }
            }
        }
        return maps.ToList();
    }

    public List<string> ListModels(bool wmo)
    {
        static bool Keep(string p, bool wmo)
        {
            if (wmo)
            {
                if (!p.EndsWith(".wmo", System.StringComparison.OrdinalIgnoreCase)) return false;
                var stem = p.Substring(0, p.Length - 4);
                return !(stem.Length >= 4 && stem[^4] == '_' &&
                         char.IsDigit(stem[^3]) && char.IsDigit(stem[^2]) && char.IsDigit(stem[^1]));
            }
            return p.EndsWith(".mdx", System.StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".m2", System.StringComparison.OrdinalIgnoreCase);
        }

        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (name, arc) in _archives)
            {
                if (_disabled.Contains(name)) continue;
                foreach (var raw in arc.ListFiles())
                {
                    var p = raw.Replace('/', '\\');
                    if (Keep(p, wmo)) set.Add(p);
                }
            }
            foreach (var path in _overrides.Keys)
                if (Keep(path, wmo)) set.Add(path);
        }
        return set.ToList();
    }

    public List<string> ListTextures()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (name, arc) in _archives)
            {
                if (_disabled.Contains(name)) continue;
                foreach (var raw in arc.ListFiles())
                {
                    var p = raw.Replace('/', '\\');
                    if (!p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.StartsWith(@"Tileset\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.EndsWith("_s.blp", StringComparison.OrdinalIgnoreCase)) continue;
                    set.Add(p);
                }
            }
        }
        return set.ToList();
    }

    public string ArchiveDiagnostics(string archiveName, string testFile = "(listfile)")
    {
        var entry = _archives.FirstOrDefault(a => a.Name.Equals(archiveName, StringComparison.OrdinalIgnoreCase));
        if (entry.Archive == null) return $"{archiveName}: not loaded";
        return $"{archiveName}: {entry.Archive.Diagnostics(testFile)}";
    }

    public void Dispose()
    {
        foreach (var (_, a) in _archives) a.Dispose();
        _archives.Clear();
    }
}
