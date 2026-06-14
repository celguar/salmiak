using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Salmiak.Core.IO;

namespace Salmiak.Core.Formats;

public static class M2ObjExporter
{
    public static List<string> Export(M2File m2, string objPath, Func<string, Stream?> openTexture)
    {
        if (m2.Indices.Length == 0) throw new InvalidOperationException("Model has no geometry to export.");

        var warnings = new List<string>();
        string dir = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(objPath);
        string mtlName = baseName + ".mtl";
        Directory.CreateDirectory(dir);

        var texMaterial = new Dictionary<string, (string Mtl, string Png)>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sm in m2.Submeshes)
        {
            string tex = sm.TexturePath ?? "";
            if (tex.Length == 0 || texMaterial.ContainsKey(tex)) continue;

            string stem = Sanitize(Path.GetFileNameWithoutExtension(tex));
            if (stem.Length == 0) stem = "tex";
            string mtl = Unique(stem, usedNames);
            string png = mtl + ".png";

            bool wrote = false;
            try
            {
                using var ts = openTexture(tex);
                if (ts != null)
                {
                    var (rgba, w, h) = BlpReader.Decode(ts);
                    PngWriter.WriteFile(Path.Combine(dir, png), rgba, w, h);
                    wrote = true;
                }
            }
            catch (Exception ex) { warnings.Add($"texture '{tex}': {ex.Message}"); }
            if (!wrote && !warnings.Exists(x => x.StartsWith($"texture '{tex}'")))
                warnings.Add($"texture '{tex}': not found");

            texMaterial[tex] = (mtl, wrote ? png : "");
        }

        WriteObj(m2, objPath, mtlName, texMaterial);
        WriteMtl(Path.Combine(dir, mtlName), texMaterial);
        return warnings;
    }

    private static void WriteObj(M2File m2, string objPath, string mtlName,
                                 Dictionary<string, (string Mtl, string Png)> texMaterial)
    {
        var v = m2.Vertices;
        int vcount = v.Length / 8;
        var sb = new StringBuilder(vcount * 48);
        sb.Append("# Exported by Salmiak - WoW 1.12 M2 doodad → Wavefront OBJ\n");
        sb.Append("# Y-up (Blender default OBJ import). Units: WoW yards.\n");
        sb.Append("mtllib ").Append(mtlName).Append('\n');

        for (int i = 0; i < vcount; i++)
        {
            float px = v[i * 8 + 0], py = v[i * 8 + 1], pz = v[i * 8 + 2];
            sb.Append("v ").Append(F(px)).Append(' ').Append(F(pz)).Append(' ').Append(F(-py)).Append('\n');
        }
        for (int i = 0; i < vcount; i++)
        {
            float u = v[i * 8 + 6], vv = v[i * 8 + 7];
            sb.Append("vt ").Append(F(u)).Append(' ').Append(F(1f - vv)).Append('\n');
        }
        for (int i = 0; i < vcount; i++)
        {
            float nx = v[i * 8 + 3], ny = v[i * 8 + 4], nz = v[i * 8 + 5];
            sb.Append("vn ").Append(F(nx)).Append(' ').Append(F(nz)).Append(' ').Append(F(-ny)).Append('\n');
        }

        var idx = m2.Indices;
        for (int s = 0; s < m2.Submeshes.Count; s++)
        {
            var sm = m2.Submeshes[s];
            if (sm.IndexCount < 3) continue;
            sb.Append("g submesh").Append(s.ToString(CultureInfo.InvariantCulture)).Append('\n');
            string tex = sm.TexturePath ?? "";
            if (tex.Length > 0 && texMaterial.TryGetValue(tex, out var mat))
                sb.Append("usemtl ").Append(mat.Mtl).Append('\n');
            else
                sb.Append("usemtl default\n");

            int end = Math.Min(sm.IndexStart + sm.IndexCount, idx.Length);
            for (int i = sm.IndexStart; i + 2 < end; i += 3)
            {
                long a = idx[i] + 1, b = idx[i + 1] + 1, c = idx[i + 2] + 1;
                sb.Append("f ")
                  .Append(a).Append('/').Append(a).Append('/').Append(a).Append(' ')
                  .Append(b).Append('/').Append(b).Append('/').Append(b).Append(' ')
                  .Append(c).Append('/').Append(c).Append('/').Append(c).Append('\n');
            }
        }

        File.WriteAllText(objPath, sb.ToString());
    }

    private static void WriteMtl(string mtlPath, Dictionary<string, (string Mtl, string Png)> texMaterial)
    {
        var sb = new StringBuilder();
        sb.Append("# Materials for the exported M2 doodad\n");
        sb.Append("newmtl default\nKd 0.8 0.8 0.8\nd 1\nillum 1\n\n");

        foreach (var (mtl, png) in texMaterial.Values)
        {
            sb.Append("newmtl ").Append(mtl).Append('\n');
            sb.Append("Ka 0 0 0\nKd 1 1 1\nd 1\nillum 1\n");
            if (png.Length > 0)
            {
                sb.Append("map_Kd ").Append(png).Append('\n');
                sb.Append("map_d ").Append(png).Append('\n');
            }
            sb.Append('\n');
        }
        File.WriteAllText(mtlPath, sb.ToString());
    }

    private static string F(float x) =>
        x.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }

    private static string Unique(string stem, HashSet<string> used)
    {
        string name = stem;
        int n = 1;
        while (!used.Add(name)) name = $"{stem}_{n++}";
        return name;
    }
}
