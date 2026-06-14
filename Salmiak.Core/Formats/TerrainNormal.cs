using System.Numerics;

namespace Salmiak.Core.Formats;

public static class TerrainNormal
{
    private const float Step = AdtFile.ChunkSize / 8f;

    public static Vector3[] Compute(MapChunk chunk)
    {
        var pos = new Vector3[145];
        int vi = 0;
        for (int row = 0; row < 9; row++)
        {
            for (int col = 0; col < 9; col++) pos[vi++] = WorldPos(chunk, col, row);
            if (row < 8)
                for (int col = 0; col < 8; col++) pos[vi++] = WorldPos(chunk, col + 0.5f, row + 0.5f);
        }
        for (int i = 0; i < 145; i++) pos[i].Z += chunk.Heights[i];

        var nrm = new Vector3[145];
        for (int row = 0; row < 8; row++)
        for (int col = 0; col < 8; col++)
        {
            int tl = row * 17 + col, tr = tl + 1, bl = (row + 1) * 17 + col, br = bl + 1, c = row * 17 + 9 + col;
            Acc(nrm, pos, tl, c, tr); Acc(nrm, pos, tr, c, br);
            Acc(nrm, pos, br, c, bl); Acc(nrm, pos, bl, c, tl);
        }
        for (int i = 0; i < 145; i++)
        {
            float l = nrm[i].Length();
            var n = l > 1e-5f ? nrm[i] / l : Vector3.UnitZ;
            if (n.Z < 0) n = -n;
            nrm[i] = n;
        }
        return nrm;
    }

    private static Vector3 WorldPos(MapChunk ch, float col, float row) =>
        new(ch.Position.X - row * Step, ch.Position.Y - col * Step, ch.Position.Z);

    private static void Acc(Vector3[] n, Vector3[] p, int a, int b, int c)
    {
        var fn = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
        n[a] += fn; n[b] += fn; n[c] += fn;
    }
}
