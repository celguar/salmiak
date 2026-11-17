using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Salmiak.Core.Formats;

namespace Salmiak.Core.IO;

// Shadow-only .adt patching: MCSH payloads are overwritten directly in the original
// .adt bytes, so height data, texture layers, doodads and WMO definitions never pass
// through the writer. Ported from WoWViewer's validated 1.12 injector (wow-viewer
// docs/architecture/adt-shadowmap-injection-1-12-2026-11-13.md): the traps documented
// there (sub-chunk header at ofs-8, rebase by header position, only rebase real
// sub-chunk offsets) are reproduced exactly.
public sealed class AdtShadowInjector
{
    // ofsMCVT, ofsMCNR, ofsMCLY, ofsMCRF, ofsMCAL, ofsMCSH, ofsMCSE, ofsMCLQ
    private static readonly int[] SubOffsInject = new[] { 20, 24, 28, 32, 36, 44, 88, 96 };

    private struct TopChunkRef
    {
        public string Magic;
        public int MagicPos;
        public int DataStart;
        public int DataLen;
    }

    // Lower-level step of BakeShadowsForAdt: writes packed MCSH payloads into a pristine
    // .adt without checking the result. MCSH payloads that already exist are overwritten
    // in place (identical length); a chunk that has none gets an MCSH subheader inserted
    // and its MCNK/MCIN extents and offset fields rebased. Callers should prefer
    // BakeShadowsForAdt, which proves the result.
    public static byte[]? Inject(byte[] adtBytes, Dictionary<string, byte[]> packedByKey)
    {
        if (adtBytes == null || packedByKey == null || adtBytes.Length < 16) return null;
        var top = ParseTopChunks(adtBytes);
        if (top.Count == 0) return null;

        int mhdrRef = -1, mcinRef = -1;
        for (int i = 0; i < top.Count; i++)
        {
            if (top[i].Magic == "MHDR") mhdrRef = i;
            else if (top[i].Magic == "MCIN") mcinRef = i;
        }
        if (mhdrRef < 0 || mcinRef < 0) return null; // no way to fix up offsets

        bool subHdr = DetectSubHeaders(adtBytes, top);

        var newData = new byte[top.Count][];
        for (int i = 0; i < top.Count; i++)
        {
            var t = top[i];
            if (t.Magic != "MCNK" || t.DataLen < 128) continue;
            // Chunk coords live in the MCNK header itself: indexX at +4, indexY at +8.
            var key = $"{ReadU32(adtBytes, t.DataStart + 4)}_{ReadU32(adtBytes, t.DataStart + 8)}";
            if (!packedByKey.ContainsKey(key)) continue;
            var packed = packedByKey[key];
            if (packed.Length != 512) continue;
            var patched = PatchMcshBytes(Slice(adtBytes, t.DataStart, t.DataLen), packed, subHdr);
            if (patched != null) newData[i] = patched;
        }

        // Patched chunks, not length deltas: the in-place MCSH overwrite keeps MCNK
        // length identical, so a length-only test silently drops every file whose
        // ADT already carried an ofsMCSH (the common 1.12 case).
        var newLen = new int[top.Count];
        var newMagicPos = new int[top.Count];
        int pos = 0;
        int patchedChunks = 0;
        for (int i = 0; i < top.Count; i++)
        {
            newLen[i] = newData[i] != null ? newData[i].Length : top[i].DataLen;
            if (newData[i] != null) patchedChunks++;
            newMagicPos[i] = pos;
            pos += 8 + newLen[i];
        }
        if (patchedChunks == 0) return null;

        // Trailing bytes after the last parsed top-level chunk (if any) are kept verbatim.
        int lastEnd = top[top.Count - 1].DataStart + top[top.Count - 1].DataLen;
        int tail = adtBytes.Length - lastEnd;
        if (tail < 0) tail = 0;

        var outBytes = new byte[pos + tail];
        for (int i = 0; i < top.Count; i++)
        {
            Buffer.BlockCopy(adtBytes, top[i].MagicPos, outBytes, newMagicPos[i], 4); // magic bytes stay verbatim
            WriteU32(outBytes, newMagicPos[i] + 4, (uint)newLen[i]);
            if (newData[i] != null) Array.Copy(newData[i], 0, outBytes, newMagicPos[i] + 8, newData[i].Length);
            else Buffer.BlockCopy(adtBytes, top[i].DataStart, outBytes, newMagicPos[i] + 8, top[i].DataLen);
        }
        if (tail > 0) Buffer.BlockCopy(adtBytes, lastEnd, outBytes, pos, tail);

        // MHDR bookkeeping: every nonzero field points at a top-level chunk start;
        // rebase it against the chunk's new position. Unknown targets keep their bytes.
        var mhdr = top[mhdrRef];
        long mhdrBase = (long)mhdr.MagicPos + 8; // data start after "MHDR"+size
        uint mcinField = ReadU32(adtBytes, mhdr.DataStart + 4);
        if (mcinField != 0) mhdrBase = (long)top[mcinRef].MagicPos - mcinField; // honor the file's own base
        long mhdrBaseNew = mhdrBase - mhdr.MagicPos + newMagicPos[mhdrRef];
        for (int f = 4; f <= 60; f += 4)
        {
            uint v = ReadU32(adtBytes, mhdr.DataStart + f);
            if (v == 0) continue;
            long target = mhdrBase + (long)v;
            int hit = -1;
            for (int i = 0; i < top.Count; i++)
                if (top[i].MagicPos == target) { hit = i; break; }
            if (hit < 0) continue;
            long shifted = target - top[hit].MagicPos + newMagicPos[hit];
            WriteU32(outBytes, newMagicPos[mhdrRef] + 8 + f, (uint)(shifted - mhdrBaseNew));
        }

        // MCIN bookkeeping: entries follow the file order of MCNK chunks.
        // ofsDoodad/sizeDoodad point inside MDDF/MODF and are left untouched.
        var mcin = top[mcinRef];
        int k = 0;
        for (int i = 0; i < top.Count && k < 256; i++)
        {
            if (top[i].Magic != "MCNK") continue;
            int oldEntry = mcin.DataStart + k * 16;
            if (oldEntry + 8 <= mcin.DataStart + mcin.DataLen)
            {
                uint origOfs = ReadU32(adtBytes, oldEntry);
                uint origSize = ReadU32(adtBytes, oldEntry + 4);
                if (origOfs != 0) // never fabricate entries the original file left empty
                {
                    int entry = newMagicPos[mcinRef] + 8 + k * 16;
                    WriteU32(outBytes, entry, (uint)newMagicPos[i]);
                    WriteU32(outBytes, entry + 4, (uint)((long)origSize + newLen[i] - top[i].DataLen));
                }
            }
            k++;
        }
        return outBytes;
    }

    private static List<TopChunkRef> ParseTopChunks(byte[] data)
    {
        var list = new List<TopChunkRef>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            var mb = new byte[4];
            mb[0] = data[pos + 3]; mb[1] = data[pos + 2]; mb[2] = data[pos + 1]; mb[3] = data[pos];
            uint size = ReadU32(data, pos + 4);
            if ((long)pos + 8 + size > data.Length) break;
            list.Add(new TopChunkRef
            {
                Magic = Encoding.ASCII.GetString(mb, 0, 4),
                MagicPos = pos,
                DataStart = pos + 8,
                DataLen = (int)size
            });
            pos += 8 + (int)size;
        }
        return list;
    }

    // Same check as AdtWriter.DetectSubChunkHeaders: does the first MCNK carry sub-chunk headers.
    private static bool DetectSubHeaders(byte[] adtBytes, List<TopChunkRef> top)
    {
        foreach (var t in top)
        {
            if (t.Magic != "MCNK") continue;
            uint off = ReadU32(adtBytes, t.DataStart + 20); // ofsMCVT
            if (off < 8 || off > (uint)t.DataLen) return true;
            int h = t.DataStart + (int)off - 8;
            if (h + 4 > adtBytes.Length) return true;
            return adtBytes[h] == (byte)'T' && adtBytes[h + 1] == (byte)'V' &&
                   adtBytes[h + 2] == (byte)'C' && adtBytes[h + 3] == (byte)'M';
        }
        return true;
    }

    // Sub-chunk header magic for MCSH, as stored on disk (this codebase keeps top-level
    // and sub-chunk FourCCs byte-reversed relative to ASCII, see ParseTopChunks).
    private static bool IsMcshSubHeader(byte[] m, int at) =>
        at >= 0 && at + 4 <= m.Length &&
        m[at] == (byte)'H' && m[at + 1] == (byte)'S' && m[at + 2] == (byte)'C' && m[at + 3] == (byte)'M';

    // Patches one MCNK payload to carry the packed 512-byte MCSH (the only shadow
    // layout WoW 1.12 writes). Returns null if it cannot be done safely, in which
    // case the caller keeps the original chunk bytes untouched.
    private static byte[]? PatchMcshBytes(byte[] m, byte[] packed, bool subHdr)
    {
        int hdr = subHdr ? 8 : 0;
        uint ofs = ReadU32(m, 44); // ofsMCSH
        if (ofs > 0)
        {
            // MCSH existed: overwrite its payload in place, MCNK length is unchanged.
            if (ofs < 128 || ofs + 512 > m.Length) return null;
            uint declared = ReadU32(m, 48);
            if (declared != 0u && declared != 512u) return null; // not a packed 1.12 MCSH
            // ofsMCSH addresses the MCSH payload, so the sub-chunk header is the 8 bytes
            // in front of it: magic at ofs-8, declared size at ofs-4. Reading the size
            // off ofs-8 lands on the magic and rejects every real 1.12 chunk.
            if (subHdr)
            {
                if (!IsMcshSubHeader(m, (int)(ofs - 8u))) return null;
                uint subDeclared = ReadU32(m, (int)(ofs - 4u));
                if (subDeclared != 0u && subDeclared != 512u) return null;
            }
            int regionEnd = m.Length;
            foreach (int f in SubOffsInject)
            {
                if (f == 44) continue;
                uint v = ReadU32(m, f);
                if (v > ofs && v <= (uint)m.Length && (int)v - hdr < regionEnd) regionEnd = (int)v - hdr;
            }
            if (regionEnd - ofs < 512) return null; // next sub-chunk sits too close
            var patched = (byte[])m.Clone();
            Buffer.BlockCopy(packed, 0, patched, (int)ofs, 512);
            WriteU32(patched, 0, ReadU32(patched, 0) | 1u); // shadow flag
            if (subHdr) WriteU32(patched, (int)(ofs - 4u), 512u); // sub-chunk size field, not the magic
            WriteU32(patched, 48, 512u); // sizeMCSH
            return patched;
        }

        // No MCSH: insert before the first sub-chunk that must follow it (MCSE/MCLQ),
        // otherwise append at the end of the chunk body.
        int p = m.Length;
        foreach (int f in new[] { 88, 96 }) // ofsMCSE, ofsMCLQ
        {
            uint v = ReadU32(m, f);
            if (v >= 128u + (uint)hdr && v <= (uint)m.Length && (int)v - hdr < p) p = (int)v - hdr;
        }
        if (p < 128) return null; // never split the 128-byte MCNK header

        int added = hdr + 512;
        var r = new byte[m.Length + added];
        Array.Copy(m, 0, r, 0, p);
        if (subHdr)
        {
            r[p] = (byte)'H'; r[p + 1] = (byte)'S'; r[p + 2] = (byte)'C'; r[p + 3] = (byte)'M'; // "MCSH" verbatim
            WriteU32(r, p + 4, 512u);
        }
        Array.Copy(packed, 0, r, p + hdr, 512);
        Array.Copy(m, p, r, p + added, m.Length - p);

        WriteU32(r, 0, ReadU32(m, 0) | 1u);
        WriteU32(r, 44, (uint)(p + hdr));
        WriteU32(r, 48, 512u);
        // Offset fields address sub-chunk DATA, so a field moves only when its header
        // (v - hdr) sits at or past the insertion point. Testing v against p shifts sub-chunks
        // that end exactly where MCSH lands (real 1.12 chunks have zero-length MCAL/MCSE runs
        // there), which leaves ofsMCAL pointing past the MCSH payload and truncates the MCSH
        // region to under 512 bytes for every later reader.
        // Only fields that actually address a sub-chunk in this payload may be rebased: the
        // pristine corpus carries sentinels such as 0xFFFFFFFF in unused ofsMCLQ slots, and
        // shifting those turns garbage into an offset that looks valid but lands mid-MCSH.
        foreach (int f in SubOffsInject)
        {
            if (f == 44) continue;
            uint v = ReadU32(m, f);
            if (v >= (uint)(128 + hdr) && (long)v <= m.Length && (long)v - hdr >= p)
                WriteU32(r, f, (uint)((long)v + added));
        }
        return r;
    }

    public static byte[] PackShadowBits(byte[]? shadow)
    {
        var packed = new byte[512];
        if (shadow == null) return packed;
        for (int row = 0; row < 64; row++)
            for (int col = 0; col < 64; col++)
            {
                var v = shadow[row * 64 + col];
                if (v == 0) continue;
                packed[row * 8 + (col >> 3)] |= (byte)(1u << (col & 7));
            }
        return packed;
    }

    // Shadow-only export path: patch MCSH in the pristine .adt, then prove that the
    // result re-parses to exactly the edited tile (shadow maps aside). Returns null
    // when the proof fails, in which case the caller must use the full writer.
    public static byte[]? BakeShadowsForAdt(byte[] original, AdtFile edited)
    {
        // The pristine file has to be parsed as well, not only for the proof: a chunk the bake
        // left shadowless keeps its original MCSH payload, and unless those bits are overwritten
        // with zeros the shadow of an object that was removed stays in game. AdtWriter.ShadowChanged
        // counts that same case as a change ("a null map against a non-null original"), so keying
        // it here keeps the shadow-only path equivalent to the full writer. Turtle-Wow tiles are a
        // practical example: their ADTs already carry a full MCSH per chunk, so a cleared chunk is
        // invisible to the editor but still shadowed in game until the zeros are written.
        var source = ParseAdt(original);
        var sourceByKey = new Dictionary<string, MapChunk>();
        if (source != null)
            for (int y = 0; y < AdtFile.ChunksPerSide; y++)
                for (int x = 0; x < AdtFile.ChunksPerSide; x++)
                {
                    var sc = source.Chunks[y, x];
                    if (sc != null) sourceByKey[$"{sc.IndexX}_{sc.IndexY}"] = sc;
                }

        var packed = new Dictionary<string, byte[]>();
        for (int y = 0; y < AdtFile.ChunksPerSide; y++)
            for (int x = 0; x < AdtFile.ChunksPerSide; x++)
            {
                var ch = edited.Chunks[y, x];
                if (ch == null) continue;
                var key = $"{ch.IndexX}_{ch.IndexY}";
                if (ch.ShadowMap != null && !ch.ShadowIsCleared())
                {
                    packed[key] = PackShadowBits(ch.ShadowMap);
                    continue;
                }

                // Nothing to draw here: erase the original bits, but only when the original had
                // any, so a chunk that never had a shadow keeps its bytes untouched.
                var prev = sourceByKey.TryGetValue(key, out var sp) ? sp : null;
                if (prev != null && prev.ShadowTexelsSet() > 0) packed[key] = PackShadowBits(null);
            }
        if (packed.Count == 0) return null;

        var baked = Inject(original, packed);
        if (baked == null) return null;
        // Everything but the shadow maps must survive untouched, in both directions: the pristine
        // file must agree with the edited tile too, so a shadow-only patch cannot be what carried
        // an unrelated difference the writer path would have had to reproduce.
        var pb = ParseAdt(baked);
        if (source == null || pb == null) return null;
        if (!pb.SameExceptShadows(edited)) return null;
        if (!source.SameExceptShadows(edited)) return null;
        return baked;
    }

    private static byte[] Slice(byte[] data, int start, int len)
    {
        var d = new byte[len];
        Array.Copy(data, start, d, 0, len);
        return d;
    }

    private static uint ReadU32(byte[] d, int i) =>
        d[i] | (uint)d[i + 1] << 8 | (uint)d[i + 2] << 16 | (uint)d[i + 3] << 24;

    private static void WriteU32(byte[] d, int i, uint v)
    {
        d[i] = (byte)v;
        d[i + 1] = (byte)(v >> 8);
        d[i + 2] = (byte)(v >> 16);
        d[i + 3] = (byte)(v >> 24);
    }

    private static AdtFile? ParseAdt(byte[] bytes)
    {
        try
        {
            using var s = new System.IO.MemoryStream(bytes, writable: false);
            return AdtFile.Parse(s);
        }
        catch { return null; }
    }
}
