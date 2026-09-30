using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace SO4KoreanPatcher
{
    internal static class StartupTextures
    {
        internal const string WarningBody = "권리자의 허락 없이 게임 소프트웨어를 인터넷으로 전송하거나\n배포하는 것은 저작권을 침해하는 불법 행위이며, 민사·형사상\n법적 제재를 받을 수 있습니다.\n\n또한 불법으로 배포된 사실을 알면서 다운로드하는 행위도\n사적 이용을 위한 복제에 해당하지 않으며, 불법입니다.\n\n여러분의 이해와 협조를 부탁드립니다.";
        internal static Bitmap Paint(NotoRenderer font, string text, float size, int width, int height, float stroke, bool center, Color color, bool opaque, int lineHeight)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                if (opaque) g.Clear(Color.Black); g.SmoothingMode = SmoothingMode.AntiAlias;
                int y = 8;
                foreach (var line in text.Split('\n'))
                {
                    if (line.Length == 0) { y += Math.Max(1, lineHeight * 52 / 120); continue; }
                    using (var path = font.Path(line, size))
                    {
                        var box = path.GetBounds(); Storage.Require(box.Width + 2 * stroke <= width - (center ? 0 : 16), "시작 화면 문구가 지정 영역보다 깁니다.");
                        float dx = (center ? (width - box.Width) / 2 : 8) - box.X, dy = (center ? (height - box.Height) / 2 : y) - box.Y;
                        Storage.Require(dy + box.Bottom + stroke <= height, "시작 화면 문구가 지정 영역보다 높습니다.");
                        using (var m = new Matrix(1, 0, 0, 1, dx, dy)) path.Transform(m);
                        if (stroke > 0) using (var pen = new Pen(Color.Black, stroke * 2) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                        using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
                    }
                    y += lineHeight;
                }
            }
            return bitmap;
        }
        private static byte[] Resized(Bitmap image, int width, int height)
        {
            if (image.Width == width && image.Height == height) return NotoRenderer.Pixels(image);
            using (var half = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(half)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel); return NotoRenderer.Pixels(half); }
        }
        private static byte[] Bc3(byte[] pixels, int stride, int x, int y)
        {
            int[] alpha = { 255, 0, 218, 182, 145, 109, 72, 36 }, gray = { 255, 0, 170, 85 }; ulong a = 0; uint c = 0;
            for (int i = 0; i < 16; i++) { int p = (y + i / 4) * stride + (x + i % 4) * 4; int ai = 0, ci = 0; for (int j = 1; j < 8; j++) if (Math.Abs(alpha[j] - pixels[p + 3]) < Math.Abs(alpha[ai] - pixels[p + 3])) ai = j; for (int j = 1; j < 4; j++) if (Math.Abs(gray[j] - pixels[p + 2]) < Math.Abs(gray[ci] - pixels[p + 2])) ci = j; a |= (ulong)ai << (i * 3); c |= (uint)ci << (i * 2); }
            var result = new byte[16]; result[0] = 255; for (int i = 0; i < 6; i++) result[2 + i] = (byte)(a >> (i * 8)); result[8] = result[9] = 255; BinaryData.Put(result, 12, c); return result;
        }
        internal static byte[] Title(byte[] original, NotoRenderer font, string version)
        {
            var result = (byte[])original.Clone(); int fma = BinaryData.I32(original, 4);
            Storage.Require(BinaryData.Magic(original, fma, " FMA") && BinaryData.Magic(original, fma + 80, "ffub"), "타이틀 구조가 다릅니다.");
            long buffer = checked(fma + (long)BitConverter.ToUInt64(original, fma + 104));
            var rectangles = new[] { new Rectangle(1640, 960, 608, 128), new Rectangle(88, 1944, 816, 152) };
            using (var title = Paint(font, "스타 오션 4", 92, 608, 128, 4, true, Color.White, false, 72))
            using (var credit = Paint(font, "한글패치 " + version + "\nby Gideon", 54, 816, 152, 3, false, Color.White, false, 72))
            {
                for (int level = 0; level < 2; level++)
                {
                    int descriptor = 0x8800 + 112 * level, width = 4096 >> level;
                    Storage.Require(BinaryData.Magic(original, descriptor, "Xgmip") && BinaryData.U16(original, descriptor + 40) == width && BinaryData.U16(original, descriptor + 42) == width, "타이틀 텍스처가 다릅니다.");
                    var guid = BinaryData.Slice(original, descriptor + 64, 16); int address = -1;
                    for (int at = fma; at < buffer - 48; at++) if (BinaryData.Magic(original, at, "rdda") && BinaryData.Slice(original, at + 16, 16).SequenceEqual(guid)) { address = at; break; }
                    Storage.Require(address >= 0 && BitConverter.ToUInt64(original, address + 32) == (ulong)(width * width), "타이틀 주소를 찾을 수 없습니다.");
                    int start = checked((int)(buffer + (long)BitConverter.ToUInt64(original, address + 40)));
                    for (int index = 0; index < 2; index++)
                    {
                        var r = rectangles[index]; int x0 = r.X >> level, y0 = r.Y >> level, w = r.Width >> level, h = r.Height >> level;
                        var pixels = Resized(index == 0 ? title : credit, w, h);
                        for (int y = 0; y < h; y += 4) for (int x = 0; x < w; x += 4) { int at = start + ((y0 + y) / 4 * (width / 4) + (x0 + x) / 4) * 16; var block = Bc3(pixels, w * 4, x, y); BinaryData.Bounds(result, at, 16); Buffer.BlockCopy(block, 0, result, at, 16); }
                    }
                }
            }
            return result;
        }
        internal static byte[] Warning(byte[] original, NotoRenderer font)
        {
            var result = (byte[])original.Clone(); int cursor = 816;
            for (int level = 0; level < 2; level++)
            {
                int width = 4096 >> level, height = 2048 >> level;
                Storage.Require(BinaryData.U16(original, 128 + 112 * level + 40) == width && BinaryData.U16(original, 128 + 112 * level + 42) == height, "주의문 텍스처가 다릅니다.");
                var boxes = new[] { new Rectangle(1888 >> level, 320 >> level, 552 >> level, 200 >> level), new Rectangle(880 >> level, 800 >> level, 2368 >> level, 872 >> level) };
                for (int index = 0; index < 2; index++)
                {
                    var r = boxes[index]; int size = (index == 0 ? 160 : 76) >> level; string text = index == 0 ? "주의" : WarningBody;
                    if (index == 1) while (text.Split('\n').Max(line => { using (var p = font.Path(line, size)) return p.GetBounds().Width; }) > r.Width - 16) size--;
                    using (var canvas = Paint(font, text, size, r.Width, r.Height, 0, index == 0, index == 0 ? Color.FromArgb(235, 0, 0) : Color.White, true, 120 >> level))
                    {
                        var pixels = NotoRenderer.Pixels(canvas);
                        for (int y = 0; y < r.Height; y++) for (int x = 0; x < r.Width; x++) { int src = (y * r.Width + x) * 4, dst = cursor + ((r.Y + y) * width + r.X + x) * 4; result[dst] = pixels[src + 2]; result[dst + 1] = pixels[src + 1]; result[dst + 2] = pixels[src]; result[dst + 3] = 255; }
                    }
                }
                cursor += width * height * 4;
            }
            Storage.Require(cursor == original.Length, "주의문 길이가 다릅니다."); return result;
        }
    }
}
