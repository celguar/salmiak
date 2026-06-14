using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Salmiak.Core.IO;

public static class PngWriter
{
    public static void Write(Stream output, byte[] rgba, int width, int height)
    {
        if (rgba.Length < (long)width * height * 4)
            throw new ArgumentException("rgba buffer too small for the given dimensions.");

        Span<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        output.Write(sig);

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(output, "IHDR", ihdr);

        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);

        using var comp = new MemoryStream();
        using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        Chunk(output, "IDAT", comp.ToArray());

        Chunk(output, "IEND", Array.Empty<byte>());
    }

    public static void WriteFile(string path, byte[] rgba, int width, int height)
    {
        using var fs = File.Create(path);
        Write(fs, rgba, width, height);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        var body = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, body, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, body, typeBytes.Length, data.Length);
        s.Write(body, 0, body.Length);

        Span<byte> crc = stackalloc byte[4];
        WriteBE(crc, 0, Crc32(body));
        s.Write(crc);
    }

    private static void WriteBE(Span<byte> dst, int offset, uint v)
    {
        dst[offset] = (byte)(v >> 24);
        dst[offset + 1] = (byte)(v >> 16);
        dst[offset + 2] = (byte)(v >> 8);
        dst[offset + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
