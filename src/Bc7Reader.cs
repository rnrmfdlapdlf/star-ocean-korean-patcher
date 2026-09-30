using System;
using System.Collections.Generic;

namespace SO4KoreanPatcher
{
    // Alpha-only BC7 reader. Format tables and decoding reference: Pillow's
    // public-domain/CC0 BcnDecode.c (https://github.com/python-pillow/Pillow/blob/main/src/libImaging/BcnDecode.c).
    // No Pillow library or game pixels are part of the distribution.
    internal sealed class Bc7Reader
    {
        private static readonly ushort[] partitions = {
            0xcccc,0x8888,0xeeee,0xecc8,0xc880,0xfeec,0xfec8,0xec80,0xc800,0xffec,0xfe80,0xe800,0xffe8,0xff00,0xfff0,0xf000,
            0xf710,0x008e,0x7100,0x08ce,0x008c,0x7310,0x3100,0x8cce,0x088c,0x3110,0x6666,0x366c,0x17e8,0x0ff0,0x718e,0x399c,
            0xaaaa,0xf0f0,0x5a5a,0x33cc,0x3c3c,0x55aa,0x9696,0xa55a,0x73ce,0x13c8,0x324c,0x3bdc,0x6996,0xc33c,0x9966,0x0660,
            0x0272,0x04e4,0x4e40,0x2720,0xc936,0x936c,0x39c6,0x639c,0x9336,0x9cc6,0x817e,0xe718,0xccf0,0x0fcc,0x7744,0xee22 };
        private static readonly byte[] anchors = {15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,2,8,2,2,8,8,15,2,8,2,2,8,8,2,2,15,15,6,8,2,8,15,15,2,8,2,2,2,15,15,6,6,2,6,8,15,15,2,2,15,15,15,15,15,2,2,15};
        private static readonly int[][] weights = { new int[0], new int[0], new[] {0,21,43,64}, new[] {0,9,18,27,37,46,55,64}, new[] {0,4,9,13,17,21,26,30,34,38,43,47,51,55,60,64} };
        private readonly byte[] data;
        private readonly int start, width;
        private readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();
        internal Bc7Reader(FiaTexture texture) { data = texture.Data; start = texture.Levels[0].offset; width = texture.Levels[0].width; }
        private static int Read(byte[] b, int offset, ref int bit, int n)
        { int v = 0; for (int i = 0; i < n; i++, bit++) v |= ((b[offset + bit / 8] >> (bit % 8)) & 1) << i; return v; }
        internal static byte[] Block(byte[] b, int offset)
        {
            BinaryData.Bounds(b, offset, 16); int mode = 0; while (mode < 8 && (b[offset] & (1 << mode)) == 0) mode++;
            var result = new byte[16]; if (mode < 4 || mode == 8) { for (int i = 0; i < 16; i++) result[i] = 255; return result; }
            int bit = mode + 1, partition = mode == 7 ? Read(b, offset, ref bit, 6) : 0, rotation = mode <= 5 ? Read(b, offset, ref bit, 2) : 0, select = mode == 4 ? Read(b, offset, ref bit, 1) : 0;
            int count = mode == 7 ? 4 : 2, cb = mode == 4 || mode == 7 ? 5 : 7, ab = mode == 4 ? 6 : mode == 5 ? 8 : mode == 6 ? 7 : 5;
            int channel = rotation == 0 ? 3 : rotation - 1; var endpoints = new int[count];
            for (int c = 0; c < 4; c++) for (int e = 0; e < count; e++) { int value = Read(b, offset, ref bit, c == 3 ? ab : cb); if (c == channel) endpoints[e] = value; }
            int bits = channel == 3 ? ab : cb;
            if (mode >= 6) { for (int e = 0; e < count; e++) endpoints[e] = (endpoints[e] << 1) | Read(b, offset, ref bit, 1); bits++; }
            for (int e = 0; e < count; e++) { int value = endpoints[e] << (8 - bits); endpoints[e] = value | (value >> bits); }
            int primaryBits = mode == 6 ? 4 : 2, secondaryBits = mode == 4 ? 3 : mode == 5 ? 2 : 0;
            int secondaryPosition = bit + 16 * primaryBits - count / 2;
            for (int i = 0; i < 16; i++)
            {
                int subset = mode == 7 ? (partitions[partition] >> i) & 1 : 0;
                int ix = Read(b, offset, ref bit, primaryBits - (i == 0 || mode == 7 && i == anchors[partition] ? 1 : 0));
                int secondary = secondaryBits == 0 ? ix : Read(b, offset, ref secondaryPosition, secondaryBits - (i == 0 ? 1 : 0));
                bool useSecondary = secondaryBits != 0 && ((channel == 3) ^ (select != 0)); int weight = weights[useSecondary ? secondaryBits : primaryBits][useSecondary ? secondary : ix];
                result[i] = (byte)(((64 - weight) * endpoints[subset * 2] + weight * endpoints[subset * 2 + 1] + 32) >> 6);
            }
            return result;
        }
        internal byte[] Crop(int x, int y, int w, int h)
        {
            var result = new byte[w * h];
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++)
            {
                int bx = x + xx, by = y + yy, index = by / 4 * ((width + 3) / 4) + bx / 4; byte[] block;
                if (!cache.TryGetValue(index, out block)) cache[index] = block = Block(data, start + index * 16);
                result[yy * w + xx] = block[by % 4 * 4 + bx % 4];
            }
            return result;
        }
    }
}
