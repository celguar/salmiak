using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Salmiak.Core.IO;

namespace Salmiak.Rendering;

public sealed class TextureCache : IDisposable
{
    private readonly MpqManager _mpq;
    private readonly Dictionary<string, int> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _fallback = -1;

    public TextureCache(MpqManager mpq) => _mpq = mpq;

    public int Get(string blpPath)
    {
        blpPath = blpPath.Replace('/', '\\');
        if (_cache.TryGetValue(blpPath, out int id)) return id;
        try
        {
            using var s = _mpq.OpenFile(blpPath);
            var (rgba, w, h) = BlpReader.Decode(s);
            id = UploadRgba(rgba, w, h);
        }
        catch { id = Fallback(); }
        _cache[blpPath] = id;
        return id;
    }

    public int GetOrUploadDecoded(string blpPath, byte[] rgba, int w, int h)
    {
        blpPath = blpPath.Replace('/', '\\');
        if (_cache.TryGetValue(blpPath, out int id)) return id;
        try { id = UploadRgba(rgba, w, h); }
        catch { id = Fallback(); }
        _cache[blpPath] = id;
        return id;
    }

    public int UploadAlpha(byte[] alpha4096)
    {
        int id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, 64, 64, 0,
            PixelFormat.Red, PixelType.UnsignedByte, alpha4096);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        return id;
    }

    public static void UpdateAlpha(int id, byte[] alpha4096)
    {
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 64, 64,
            PixelFormat.Red, PixelType.UnsignedByte, alpha4096);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
    }

    public static void UpdateAlphaRect(int id, byte[] alpha4096, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 64);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, x);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, y);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, w, h,
            PixelFormat.Red, PixelType.UnsignedByte, alpha4096);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
    }

    public static void RegenMips(int id)
    {
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
    }

    private int Fallback()
    {
        if (_fallback >= 0) return _fallback;
        var d = new byte[4 * 4 * 4];
        for (int i = 0; i < d.Length; i += 4) { d[i]=120; d[i+1]=90; d[i+2]=60; d[i+3]=255; }
        _fallback = UploadRgba(d, 4, 4);
        return _fallback;
    }

    private static int UploadRgba(byte[] rgba, int w, int h)
    {
        int id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        return id;
    }

    public void Dispose()
    {
        foreach (var id in _cache.Values) GL.DeleteTexture(id);
        if (_fallback >= 0) GL.DeleteTexture(_fallback);
        _cache.Clear(); _fallback = -1;
    }
}
