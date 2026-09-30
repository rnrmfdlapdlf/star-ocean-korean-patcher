using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SO4KoreanPatcher
{
    public sealed class ResourceLocation { public string package, recipe; public int outerId, member; }
    public sealed class TextRecord { public int id; public string text; }
    public sealed class GlyphRule
    {
        public int index, advance, unused, x, y, width, height;
        public string character; // null means preserve the original glyph's alpha pixels.
    }
    public sealed class FontRule
    {
        public bool common;
        public int count, fontSize, alphaLevels = 16;
        public GlyphRule[] glyphs;
    }
    public sealed class ResourceRecipe
    {
        public string key, sourceHash, kind, companionOf;
        public int sourceSize, mode;
        public Dictionary<string, int> mapping;
        public TextRecord[] records;
        public FontRule font;
    }
    public sealed class SemanticManifest
    {
        public int schema = 2;
        public string format = "translation-resources-v2", version, builtAt, author = "by Gideon", buildId;
        public string fontHash;
        public ResourceLocation[] locations;
        public Dictionary<string, long> sourceLengths;
    }
    internal static class GameText
    {
        internal static void Code(Stream output, int g)
        {
            int v = checked(g + 1); if (v <= 0 || v >= 32768) throw new InvalidDataException("글리프 번호가 잘못되었습니다.");
            if (v < 128) output.WriteByte((byte)v); else { output.WriteByte((byte)((v & 127) | 128)); output.WriteByte((byte)(v >> 7)); }
        }
        internal static int ReadCode(byte[] bytes, ref int p)
        {
            BinaryData.Bounds(bytes, p, 1); int v = bytes[p++];
            if (v >= 128) { BinaryData.Bounds(bytes, p, 1); v = (v & 127) | (bytes[p++] << 7); }
            return v - 1;
        }
        // Recursively preserve embedded fallback names and control parameters, including zero bytes.
        internal static int End(byte[] bytes, int start)
        {
            int p = start;
            while (true)
            {
                int g = ReadCode(bytes, ref p); if (g == -1) return p;
                if (g == 16402) { p += 2; p = End(bytes, p); }
                else if (g == 16391 || g == 16427) p++;
                else if (g == 16423) p += 4;
                else if (g == 16403) p += 2;
                else if (g == 16399 && p + 2 < bytes.Length && bytes[p] == 0x90 && bytes[p + 1] == 0x80) { p += 2; p = End(bytes, p); }
                BinaryData.Bounds(bytes, p, 0);
            }
        }
        internal static Dictionary<int, byte[]> Records(byte[] data)
        {
            var result = new Dictionary<int, byte[]>(); int table = BinaryData.I32(data, 32), pool = BinaryData.I32(data, 36), count = BinaryData.I32(data, 60);
            if (count > 1000000) throw new InvalidDataException("텍스트 레코드 수가 잘못되었습니다.");
            for (int i = 0; i < count; i++) { int row = table + i * 8, id = BinaryData.I32(data, row), p = checked(pool + BinaryData.I32(data, row + 4)); result.Add(id, BinaryData.Slice(data, p, End(data, p) - p)); }
            return result;
        }
        internal static string Decode(byte[] bytes, Dictionary<int, string> mapping)
        {
            var text = new StringBuilder(); int p = 0;
            while (p < bytes.Length)
            {
                int g = ReadCode(bytes, ref p); string c;
                if (g == -1) { if (p != bytes.Length) text.Append("〔END〕"); continue; }
                if (g == 16383) text.Append('\n');
                else if (g < 16383 && mapping.TryGetValue(g, out c)) text.Append(c);
                else text.Append("〔G:").Append(g).Append('〕');
                int n = g == 16402 || g == 16403 ? 2 : g == 16391 || g == 16427 ? 1 : g == 16423 ? 4 : 0;
                if (n > 0) { BinaryData.Bounds(bytes, p, n); text.Append("〔ARG:").Append(Storage.Hex(BinaryData.Slice(bytes, p, n))).Append('〕'); p += n; }
            }
            return text.ToString();
        }
        internal static byte[] Encode(string text, Dictionary<string, int> mapping)
        {
            using (var output = new MemoryStream())
            {
                for (int p = 0; p < text.Length; p++)
                {
                    if (text[p] == '〔')
                    {
                        int end = text.IndexOf('〕', p + 1); if (end < 0) throw new InvalidDataException("닫히지 않은 제어 코드입니다.");
                        string tag = text.Substring(p + 1, end - p - 1);
                        if (tag == "END") output.WriteByte(0);
                        else if (tag.StartsWith("G:", StringComparison.Ordinal)) Code(output, int.Parse(tag.Substring(2)));
                        else if (tag.StartsWith("ARG:", StringComparison.Ordinal))
                        { string hex = tag.Substring(4); if (hex.Length % 2 != 0 || hex.Length > 8) throw new InvalidDataException("제어 인수 길이가 잘못되었습니다."); for (int i = 0; i < hex.Length; i += 2) output.WriteByte(Convert.ToByte(hex.Substring(i, 2), 16)); }
                        else throw new InvalidDataException("알 수 없는 제어 코드: " + tag);
                        p = end;
                    }
                    else if (text[p] == '\n') Code(output, 16383);
                    else { int glyph; if (!mapping.TryGetValue(text[p].ToString(), out glyph)) throw new InvalidDataException("폰트 매핑에 없는 글자입니다: " + text[p]); Code(output, glyph); }
                }
                output.WriteByte(0); var result = output.ToArray();
                if (End(result, 0) != result.Length) throw new InvalidDataException("문자열 종료·제어 구조가 올바르지 않습니다.");
                return result;
            }
        }
    }

    internal sealed class TextureLevel { internal int descriptor, width, height, pitch, offset; }
    internal sealed class FiaTexture
    {
        internal readonly byte[] Data;
        internal readonly TextureLevel[] Levels;
        internal readonly int HeaderSize;
        internal FiaTexture(byte[] data)
        {
            Data = data; if (!BinaryData.Magic(data, 0, " FIA")) throw new InvalidDataException("FIA 헤더가 아닙니다.");
            var levels = new List<TextureLevel>();
            for (int p = 128; BinaryData.Magic(data, p, "Xgmip"); p += 112)
                levels.Add(new TextureLevel { descriptor = p, width = BinaryData.U16(data, p + 40), height = BinaryData.U16(data, p + 42), pitch = BinaryData.I32(data, p + 60) });
            if (levels.Count < 1 || levels.Count > 8) throw new InvalidDataException("FIA 텍스처 수가 올바르지 않습니다.");
            Levels = levels.ToArray(); HeaderSize = data.Length - levels.Sum(x => x.pitch); int at = HeaderSize;
            if (HeaderSize < 128 + levels.Count * 112) throw new InvalidDataException("FIA 픽셀 영역이 잘못되었습니다.");
            foreach (var level in Levels) { level.offset = at; at += level.pitch; }
        }
        internal byte[] HeaderForHeight(int newHeight)
        {
            int oldHeight = Levels[0].height;
            if (newHeight < oldHeight || newHeight % oldHeight != 0 || (newHeight / oldHeight & (newHeight / oldHeight - 1)) != 0) throw new InvalidDataException("폰트 높이 비율이 올바르지 않습니다.");
            var header = BinaryData.Slice(Data, 0, HeaderSize); if (newHeight == oldHeight) return header;
            int ratio = newHeight / oldHeight, total = Levels.Sum(x => x.pitch), fma = BinaryData.I32(Data, 12);
            if (!BinaryData.Magic(Data, fma, " FMA")) throw new InvalidDataException("FIA 메타데이터가 잘못되었습니다.");
            BinaryData.Put(header, fma + 4, checked(BinaryData.U32(header, fma + 4) + (uint)(total * (ratio - 1))));
            int running = 0;
            foreach (var l in Levels)
            {
                int p = l.descriptor; Buffer.BlockCopy(BitConverter.GetBytes((ushort)(l.height * ratio)), 0, header, p + 42, 2); Buffer.BlockCopy(BitConverter.GetBytes((ushort)(l.height * ratio / 4)), 0, header, p + 52, 2); BinaryData.Put(header, p + 60, (uint)(l.pitch * ratio));
                var guid = BinaryData.Slice(Data, p + 64, 16); int address = -1;
                for (int q = fma; q < HeaderSize - 48; q++) if (BinaryData.Magic(Data, q, "rdda") && BinaryData.Slice(Data, q + 16, 16).SequenceEqual(guid)) { if (address != -1) throw new InvalidDataException("FIA 주소가 중복되었습니다."); address = q; }
                if (address < 0 || BinaryData.I32(Data, address + 32) != l.pitch) throw new InvalidDataException("FIA 주소를 찾을 수 없습니다.");
                BinaryData.Put(header, address + 32, (uint)(l.pitch * ratio)); BinaryData.Put(header, address + 40, (uint)running); running += l.pitch * ratio;
            }
            int buffers = 0;
            for (int p = fma; p < HeaderSize - 20; p++) if (BinaryData.Magic(Data, p, "ffub") && BinaryData.I32(Data, p + 16) == total) { BinaryData.Put(header, p + 16, (uint)(total * ratio)); buffers++; }
            if (buffers != 1) throw new InvalidDataException("FIA 버퍼 정보를 찾을 수 없습니다.");
            return header;
        }
    }

    internal sealed class NotoRenderer : IDisposable
    {
        private readonly PrivateFontCollection fonts = new PrivateFontCollection();
        private readonly IntPtr memory;
        internal FontFamily Family { get { return fonts.Families[0]; } }
        internal NotoRenderer(byte[] ttf)
        {
            memory = Marshal.AllocCoTaskMem(ttf.Length); Marshal.Copy(ttf, 0, memory, ttf.Length);
            try { fonts.AddMemoryFont(memory, ttf.Length); if (Family.Name.IndexOf("Noto Sans KR", StringComparison.Ordinal) < 0) throw new InvalidDataException("Noto Sans KR 폰트가 아닙니다."); }
            catch { fonts.Dispose(); Marshal.FreeCoTaskMem(memory); throw; }
        }
        internal GraphicsPath Path(string text, float size)
        {
            var p = new GraphicsPath(); p.AddString(text, Family, (int)FontStyle.Regular, size, new PointF(0, 0), StringFormat.GenericTypographic);
            // Noto's overlapping contours use the nonzero winding rule, including Korean stroke joins.
            p.FillMode = FillMode.Winding; return p;
        }
        internal byte[] Glyph(string c, int size, int width, int height, bool common, int levels)
        {
            if (width <= 0 || height <= 0 || levels < 2 || levels > 256) throw new InvalidDataException("글리프 크기·농도가 잘못되었습니다.");
            using (var image = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(image))
            using (var glyph = Path(c, size))
            using (var reference = Path("가", size))
            {
                if (glyph.PointCount == 0) return new byte[width * height];
                var box = glyph.GetBounds(); float x = common ? 3 - box.X : 1 - Math.Min(0, box.X), y = common ? 5 - box.Y : 3 - reference.GetBounds().Y;
                if ("~−-—×+=>…·・%".Contains(c)) y = (height - box.Height) / 2 - box.Y;
                else if (common && (c == "」" || c == "』")) y = 5 - reference.GetBounds().Y;
                else if (common && c == "_") y = height - 2 - box.Bottom;
                float scale = Math.Min(1, Math.Min((width - 2) / Math.Max(1, box.Width), (height - 2) / Math.Max(1, box.Height)));
                if (scale < 1) { using (var transform = new Matrix(scale, 0, 0, scale, 0, 0)) glyph.Transform(transform); box = glyph.GetBounds(); x = Math.Max(1 - box.X, Math.Min(x, width - 1 - box.Right)); y = Math.Max(1 - box.Y, Math.Min(y, height - 1 - box.Bottom)); }
                using (var transform = new Matrix(1, 0, 0, 1, x, y)) glyph.Transform(transform);
                g.SmoothingMode = SmoothingMode.AntiAlias; g.FillPath(Brushes.White, glyph);
                var rgba = Pixels(image); var alpha = new byte[width * height];
                for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)Math.Round(Math.Round(rgba[i * 4 + 3] * (levels - 1) / 255.0) * 255 / (levels - 1));
                return alpha;
            }
        }
        internal static byte[] Pixels(Bitmap image)
        {
            var bits = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { var bytes = new byte[image.Width * image.Height * 4]; for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), bytes, y * image.Width * 4, image.Width * 4); return bytes; }
            finally { image.UnlockBits(bits); }
        }
        public void Dispose() { fonts.Dispose(); Marshal.FreeCoTaskMem(memory); }
    }
    internal static class Bc7Alpha
    {
        private static readonly int[] weights = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
        private static readonly int[] nearest = Enumerable.Range(0, 256).Select(a => Enumerable.Range(0, 16).OrderBy(j => Math.Abs((int)Math.Round(weights[j] * 255.0 / 64) - a)).First()).ToArray();
        internal static byte[] Block(byte[] alpha)
        {
            int[] ix = new int[16]; for (int i = 0; i < 16; i++) ix[i] = nearest[alpha[i]];
            bool reverse = ix[0] >= 8; if (reverse) for (int i = 0; i < 16; i++) ix[i] = 15 - ix[i];
            byte[] result = new byte[16]; int bit = 0;
            Action<int, int> put = (v, n) => { for (int i = 0; i < n; i++, bit++) if ((v & (1 << i)) != 0) result[bit / 8] |= (byte)(1 << (bit % 8)); };
            put(64, 7); for (int channel = 0; channel < 4; channel++) for (int endpoint = 0; endpoint < 2; endpoint++) put(channel < 3 ? 127 : ((endpoint == 1) ^ reverse) ? 127 : 0, 7);
            put(reverse ? 1 : 0, 1); put(reverse ? 0 : 1, 1); for (int i = 0; i < 16; i++) put(ix[i], i == 0 ? 3 : 4);
            if (bit != 128) throw new InvalidOperationException(); return result;
        }
    }
}
