using System;
using System.IO;
using System.Text;

namespace Salmiak.Core.IO;

public static class BlpReader
{
    public static (byte[] rgba, int width, int height) Decode(Stream stream)
    {
        stream.Position = 0;
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var magic = new string(r.ReadChars(4));
        return magic switch
        {
            "BLP2" => DecodeBLP2(r),
            "BLP1" => DecodeBLP1(r),
            _ => throw new InvalidDataException($"Unknown BLP magic: {magic}")
        };
    }

    private static (byte[] rgba, int width, int height) DecodeBLP2(BinaryReader r)
    {
        r.ReadUInt32();
        byte encoding  = r.ReadByte();
        byte alphaBits = r.ReadByte();
        byte alphaType = r.ReadByte();
        r.ReadByte();
        int width  = (int)r.ReadUInt32();
        int height = (int)r.ReadUInt32();

        var mipOffsets = new uint[16];
        var mipSizes   = new uint[16];
        for (int i = 0; i < 16; i++) mipOffsets[i] = r.ReadUInt32();
        for (int i = 0; i < 16; i++) mipSizes[i]   = r.ReadUInt32();

        var palette = r.ReadBytes(1024);

        int mipIdx = 0;
        while (mipIdx < 15 && mipSizes[mipIdx] == 0) mipIdx++;
        r.BaseStream.Position = mipOffsets[mipIdx];
        var data = r.ReadBytes((int)mipSizes[mipIdx]);

        if (encoding == 1)
            return (DecodePaletted(data, palette, width, height, alphaBits), width, height);

        if (encoding == 2)
        {
            byte[] rgba = (alphaBits, alphaType) switch
            {
                (0, _) or (1, _) => DecodeDXT1(data, width, height, alphaBits == 1),
                (4, _)           => DecodeDXT3(data, width, height),
                _                => DecodeDXT5(data, width, height),
            };
            return (rgba, width, height);
        }

        throw new NotSupportedException($"BLP2 encoding {encoding} not supported");
    }

    private static (byte[] rgba, int width, int height) DecodeBLP1(BinaryReader r)
    {
        r.ReadUInt32();
        uint alphaBits = r.ReadUInt32();
        int width  = (int)r.ReadUInt32();
        int height = (int)r.ReadUInt32();
        r.ReadUInt32(); r.ReadUInt32();
        var mipOffsets = new uint[16]; for (int i=0;i<16;i++) mipOffsets[i]=r.ReadUInt32();
        var mipSizes   = new uint[16]; for (int i=0;i<16;i++) mipSizes[i]  =r.ReadUInt32();

        var palette = r.ReadBytes(1024);
        r.BaseStream.Position = mipOffsets[0];
        var data = r.ReadBytes((int)mipSizes[0]);
        return (DecodePaletted(data, palette, width, height, (byte)alphaBits), width, height);
    }

    private static byte[] DecodePaletted(byte[] data, byte[] palette, int w, int h, byte alphaBits)
    {
        var rgba = new byte[w * h * 4];
        int alphaStart = w * h;
        for (int i = 0; i < w * h; i++)
        {
            int pi = data[i] * 4;
            rgba[i*4+0] = palette[pi+2];
            rgba[i*4+1] = palette[pi+1];
            rgba[i*4+2] = palette[pi+0];
            rgba[i*4+3] = alphaBits == 0 ? (byte)255 : data[alphaStart + i];
        }
        return rgba;
    }

    private static byte[] DecodeDXT1(byte[] data, int w, int h, bool punchAlpha)
    {
        var rgba = new byte[w * h * 4];
        int bx = Math.Max(1, (w + 3) / 4), by = Math.Max(1, (h + 3) / 4);
        int ofs = 0;
        for (int blockY = 0; blockY < by; blockY++)
        for (int blockX = 0; blockX < bx; blockX++, ofs += 8)
        {
            ushort c0 = Read16(data, ofs),   c1 = Read16(data, ofs+2);
            uint   lk = Read32(data, ofs+4);
            var (r0,g0,b0) = Rgb565(c0);
            var (r1,g1,b1) = Rgb565(c1);
            Span<byte> cr=[r0,r1,0,0], cg=[g0,g1,0,0], cb=[b0,b1,0,0], ca=[255,255,255,255];
            if (c0 > c1) { cr[2]=(byte)((2*r0+r1)/3); cr[3]=(byte)((r0+2*r1)/3); cg[2]=(byte)((2*g0+g1)/3); cg[3]=(byte)((g0+2*g1)/3); cb[2]=(byte)((2*b0+b1)/3); cb[3]=(byte)((b0+2*b1)/3); }
            else         { cr[2]=(byte)((r0+r1)/2);    cg[2]=(byte)((g0+g1)/2);    cb[2]=(byte)((b0+b1)/2);    ca[3]=punchAlpha?(byte)0:(byte)255; }
            WriteBlock4x4(rgba, w, h, blockX, blockY, cr, cg, cb, ca, lk, 2);
        }
        return rgba;
    }

    private static byte[] DecodeDXT3(byte[] data, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        int bx = Math.Max(1,(w+3)/4), by=Math.Max(1,(h+3)/4);
        int ofs = 0;
        for (int blockY = 0; blockY < by; blockY++)
        for (int blockX = 0; blockX < bx; blockX++, ofs += 16)
        {
            ulong alphaData = Read64(data, ofs);
            ushort c0=Read16(data,ofs+8), c1=Read16(data,ofs+10); uint lk=Read32(data,ofs+12);
            var (r0,g0,b0)=Rgb565(c0); var (r1,g1,b1)=Rgb565(c1);
            Span<byte> cr=[r0,r1,(byte)((2*r0+r1)/3),(byte)((r0+2*r1)/3)],
                       cg=[g0,g1,(byte)((2*g0+g1)/3),(byte)((g0+2*g1)/3)],
                       cb=[b0,b1,(byte)((2*b0+b1)/3),(byte)((b0+2*b1)/3)];
            for (int py=0;py<4;py++) for (int px=0;px<4;px++)
            {
                int ix=blockX*4+px, iy=blockY*4+py;
                if (ix>=w||iy>=h) continue;
                int ci=(int)((lk>>(2*(py*4+px)))&3);
                byte a=(byte)(((alphaData>>(4*(py*4+px)))&0xF)*17);
                int ri=(iy*w+ix)*4;
                rgba[ri]=cr[ci]; rgba[ri+1]=cg[ci]; rgba[ri+2]=cb[ci]; rgba[ri+3]=a;
            }
        }
        return rgba;
    }

    private static byte[] DecodeDXT5(byte[] data, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        int bx=Math.Max(1,(w+3)/4), by=Math.Max(1,(h+3)/4);
        int ofs=0;
        for (int blockY=0;blockY<by;blockY++)
        for (int blockX=0;blockX<bx;blockX++, ofs+=16)
        {
            byte a0=data[ofs], a1=data[ofs+1];
            ulong abits=0; for(int i=0;i<6;i++) abits|=((ulong)data[ofs+2+i])<<(i*8);
            Span<byte> at=[a0,a1,0,0,0,0,0,0];
            if(a0>a1){at[2]=(byte)((6*a0+a1)/7);at[3]=(byte)((5*a0+2*a1)/7);at[4]=(byte)((4*a0+3*a1)/7);at[5]=(byte)((3*a0+4*a1)/7);at[6]=(byte)((2*a0+5*a1)/7);at[7]=(byte)((a0+6*a1)/7);}
            else{at[2]=(byte)((4*a0+a1)/5);at[3]=(byte)((3*a0+2*a1)/5);at[4]=(byte)((2*a0+3*a1)/5);at[5]=(byte)((a0+4*a1)/5);at[6]=0;at[7]=255;}
            ushort c0=Read16(data,ofs+8),c1=Read16(data,ofs+10); uint lk=Read32(data,ofs+12);
            var (r0,g0,b0)=Rgb565(c0); var (r1,g1,b1)=Rgb565(c1);
            Span<byte> cr=[r0,r1,(byte)((2*r0+r1)/3),(byte)((r0+2*r1)/3)],
                       cg=[g0,g1,(byte)((2*g0+g1)/3),(byte)((g0+2*g1)/3)],
                       cb=[b0,b1,(byte)((2*b0+b1)/3),(byte)((b0+2*b1)/3)];
            for(int py=0;py<4;py++) for(int px=0;px<4;px++)
            {
                int ix=blockX*4+px, iy=blockY*4+py;
                if(ix>=w||iy>=h) continue;
                int ci=(int)((lk>>(2*(py*4+px)))&3);
                int ai=(int)((abits>>(3*(py*4+px)))&7);
                int ri=(iy*w+ix)*4;
                rgba[ri]=cr[ci];rgba[ri+1]=cg[ci];rgba[ri+2]=cb[ci];rgba[ri+3]=at[ai];
            }
        }
        return rgba;
    }

    private static (byte r, byte g, byte b) Rgb565(ushort c)
    {
        byte r=(byte)((c>>11)&0x1F); r=(byte)((r<<3)|(r>>2));
        byte g=(byte)((c>> 5)&0x3F); g=(byte)((g<<2)|(g>>4));
        byte b=(byte)( c     &0x1F); b=(byte)((b<<3)|(b>>2));
        return (r,g,b);
    }

    private static void WriteBlock4x4(byte[] rgba, int w, int h, int blockX, int blockY,
        Span<byte> cr, Span<byte> cg, Span<byte> cb, Span<byte> ca, uint lookup, int bitsPerIdx)
    {
        uint mask = (uint)((1<<bitsPerIdx)-1);
        for (int py=0;py<4;py++) for (int px=0;px<4;px++)
        {
            int ix=blockX*4+px, iy=blockY*4+py;
            if(ix>=w||iy>=h) continue;
            int idx=(int)((lookup>>(bitsPerIdx*(py*4+px)))&mask);
            int ri=(iy*w+ix)*4;
            rgba[ri]=cr[idx];rgba[ri+1]=cg[idx];rgba[ri+2]=cb[idx];rgba[ri+3]=ca[idx];
        }
    }

    private static ushort Read16(byte[] d, int o) => (ushort)(d[o]|(d[o+1]<<8));
    private static uint   Read32(byte[] d, int o) => (uint)(d[o]|(d[o+1]<<8)|(d[o+2]<<16)|(d[o+3]<<24));
    private static ulong  Read64(byte[] d, int o) => (ulong)Read32(d,o)|((ulong)Read32(d,o+4)<<32);
}
