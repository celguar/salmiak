using System;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public sealed class DbcFile
{
    private const uint Magic = 0x43424457;

    public int FieldCount { get; private set; }
    public int RecordSize { get; private set; }
    public byte[][] Records { get; private set; } = Array.Empty<byte[]>();
    public byte[] StringBlock { get; private set; } = Array.Empty<byte>();

    public int RecordCount => Records.Length;

    public static DbcFile Read(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    public static DbcFile Parse(byte[] b)
    {
        if (b.Length < 20 || BitConverter.ToUInt32(b, 0) != Magic)
            throw new InvalidDataException("Not a WDBC file.");
        int recordCount = BitConverter.ToInt32(b, 4);
        int fieldCount = BitConverter.ToInt32(b, 8);
        int recordSize = BitConverter.ToInt32(b, 12);
        int stringSize = BitConverter.ToInt32(b, 16);

        var dbc = new DbcFile { FieldCount = fieldCount, RecordSize = recordSize };
        var recs = new byte[recordCount][];
        int off = 20;
        for (int i = 0; i < recordCount; i++)
        {
            var r = new byte[recordSize];
            Buffer.BlockCopy(b, off, r, 0, recordSize);
            recs[i] = r;
            off += recordSize;
        }
        dbc.Records = recs;
        dbc.StringBlock = new byte[stringSize];
        Buffer.BlockCopy(b, off, dbc.StringBlock, 0, Math.Min(stringSize, b.Length - off));
        return dbc;
    }

    public static byte[] WriteRecords(System.Collections.Generic.IReadOnlyList<byte[]> records, int fieldCount, int recordSize, byte[] stringBlock)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(records.Count);
        w.Write(fieldCount);
        w.Write(recordSize);
        w.Write(stringBlock.Length);
        foreach (var r in records)
        {
            if (r.Length == recordSize) w.Write(r);
            else { var rec = new byte[recordSize]; Buffer.BlockCopy(r, 0, rec, 0, Math.Min(r.Length, recordSize)); w.Write(rec); }
        }
        w.Write(stringBlock);
        w.Flush();
        return ms.ToArray();
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Records.Length);
        w.Write(FieldCount);
        w.Write(RecordSize);
        w.Write(StringBlock.Length);
        foreach (var r in Records) w.Write(r);
        w.Write(StringBlock);
        w.Flush();
        return ms.ToArray();
    }

    public uint GetU(int rec, int field) => BitConverter.ToUInt32(Records[rec], field * 4);
    public int GetI(int rec, int field) => BitConverter.ToInt32(Records[rec], field * 4);
    public float GetF(int rec, int field) => BitConverter.ToSingle(Records[rec], field * 4);

    public string GetString(int rec, int field)
    {
        uint off = GetU(rec, field);
        if (off == 0 || off >= StringBlock.Length) return string.Empty;
        int end = (int)off;
        while (end < StringBlock.Length && StringBlock[end] != 0) end++;
        return Encoding.UTF8.GetString(StringBlock, (int)off, end - (int)off);
    }

    public void SetU(int rec, int field, uint v) => BitConverter.GetBytes(v).CopyTo(Records[rec], field * 4);
    public void SetI(int rec, int field, int v) => BitConverter.GetBytes(v).CopyTo(Records[rec], field * 4);
    public void SetF(int rec, int field, float v) => BitConverter.GetBytes(v).CopyTo(Records[rec], field * 4);

    public uint AddString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var bytes = Encoding.UTF8.GetBytes(s);
        int offset = StringBlock.Length;
        var nb = new byte[offset + bytes.Length + 1];
        Buffer.BlockCopy(StringBlock, 0, nb, 0, offset);
        Buffer.BlockCopy(bytes, 0, nb, offset, bytes.Length);
        StringBlock = nb;
        return (uint)offset;
    }

    public int AddRecord(byte[] payload)
    {
        var r = new byte[RecordSize];
        Buffer.BlockCopy(payload, 0, r, 0, Math.Min(payload.Length, RecordSize));
        var arr = new byte[Records.Length + 1][];
        Array.Copy(Records, arr, Records.Length);
        arr[Records.Length] = r;
        Records = arr;
        return Records.Length - 1;
    }

    public int IndexOfId(uint id)
    {
        for (int i = 0; i < Records.Length; i++) if (GetU(i, 0) == id) return i;
        return -1;
    }

    public void CopyRecordPreservingId(int destRec, DbcFile src, int srcRec)
    {
        uint id = GetU(destRec, 0);
        Buffer.BlockCopy(src.Records[srcRec], 0, Records[destRec], 0, Math.Min(RecordSize, src.RecordSize));
        SetU(destRec, 0, id);
    }
}
