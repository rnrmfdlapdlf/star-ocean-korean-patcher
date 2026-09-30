using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SO4KoreanPatcher
{
    internal sealed class FontBuild { internal byte[] Metrics, Fia; internal int Width, Height; }
    internal sealed class GlyphPlacement
    {
        internal GlyphRule Rule; internal int x, y, width, height, sourceX, sourceY, metricX, metricY;
    }
    internal sealed class ResourceCompiler : IDisposable
    {
        private readonly NotoRenderer renderer;
        private readonly string version;
        internal ResourceCompiler(byte[] font, string version) { renderer = new NotoRenderer(font); this.version = version; }
        internal byte[] Compile(ResourceRecipe recipe, byte[] original, Func<string, byte[]> companion)
        {
            Storage.Require(original.Length == recipe.sourceSize && Storage.Hash(original) == recipe.sourceHash, "원본 멤버 검증 실패: " + recipe.key);
            if (recipe.kind == "text") return Text(recipe, original);
            if (recipe.kind == "companion")
            {
                var text = companion(recipe.companionOf); int p = BinaryData.I32(text, 48); var embedded = new FiaTexture(BinaryData.Slice(text, p, text.Length - p)); var own = new FiaTexture(original);
                Storage.Require(own.Levels.Length == embedded.Levels.Length && own.Levels[0].width == embedded.Levels[0].width, "이벤트 폰트 구조가 다릅니다.");
                using (var output = new MemoryStream()) { var header = own.HeaderForHeight(embedded.Levels[0].height); output.Write(header, 0, header.Length); output.Write(embedded.Data, embedded.HeaderSize, embedded.Data.Length - embedded.HeaderSize); return output.ToArray(); }
            }
            if (recipe.kind == "title") return StartupTextures.Title(original, renderer, version);
            if (recipe.kind == "warning") return StartupTextures.Warning(original, renderer);
            throw new InvalidDataException("지원하지 않는 리소스: " + recipe.kind);
        }
        internal byte[] Text(ResourceRecipe recipe, byte[] original)
        {
            Storage.Require(BinaryData.Magic(original, 0, "pDCM"), "텍스트 형식이 다릅니다.");
            var old = GameText.Records(original); var rows = recipe.records.ToDictionary(r => r.id, r => GameText.Encode(r.text, recipe.mapping));
            Storage.Require(rows.Keys.All(old.ContainsKey), "원본에 없는 텍스트 ID입니다.");
            int table = BinaryData.I32(original, 32), pool = BinaryData.I32(original, 36), oldMetric = BinaryData.I32(original, 40), oldMap = BinaryData.I32(original, 44), oldFia = BinaryData.I32(original, 48);
            using (var output = new MemoryStream())
            {
                int count = BinaryData.I32(original, 60); output.Write(original, 0, count == 0 ? Math.Max(pool, oldMetric) : pool); var locations = new Dictionary<string, int>();
                for (int i = 0; i < count; i++)
                {
                    int id = BinaryData.I32(original, table + i * 8); byte[] bytes; if (!rows.TryGetValue(id, out bytes)) bytes = old[id];
                    string key = Convert.ToBase64String(bytes); int offset;
                    if (!locations.TryGetValue(key, out offset)) { offset = checked((int)output.Length - pool); locations.Add(key, offset); output.Position = output.Length; output.Write(bytes, 0, bytes.Length); }
                    output.Position = table + i * 8 + 4; var pointer = BitConverter.GetBytes(offset); output.Write(pointer, 0, 4);
                }
                output.Position = output.Length;
                int met = 0, map = 0, fia = 0; FontBuild font = null;
                if (oldFia > 0)
                {
                    if (recipe.font != null) font = BuildFont(recipe.font, new FiaTexture(BinaryData.Slice(original, oldFia, original.Length - oldFia)), original);
                    Pad(output, 128); met = (int)output.Position;
                    byte[] metrics = font == null ? BinaryData.Slice(original, oldMetric, oldMap - oldMetric) : font.Metrics;
                    output.Write(metrics, 0, metrics.Length); Pad(output, 128); map = (int)output.Position;
                    byte[] mapBytes = BinaryData.Slice(original, oldMap, oldFia - oldMap); if (font != null) BinaryData.Put(mapBytes, 4, (uint)font.Height);
                    int used = mapBytes.Length; while (used > 8 && mapBytes[used - 1] == 0) used--; output.Write(mapBytes, 0, used); Pad(output, 4096); fia = (int)output.Position;
                    byte[] texture = font == null ? BinaryData.Slice(original, oldFia, original.Length - oldFia) : font.Fia; output.Write(texture, 0, texture.Length);
                }
                else Pad(output, 16);
                var result = output.ToArray(); BinaryData.Put(result, 20, (uint)result.Length);
                if (oldFia > 0)
                {
                    foreach (int p in new[] { 4, 12, 48 }) BinaryData.Put(result, p, (uint)fia); BinaryData.Put(result, 40, (uint)met); BinaryData.Put(result, 44, (uint)map);
                    if (font != null)
                    {
                        // Both banks share one contiguous metrics array. Keep their original split for common fonts.
                        if (recipe.font.common) BinaryData.Put(result, 160, (uint)(recipe.font.count - BinaryData.I32(original, 144)));
                        else { BinaryData.Put(result, 144, (uint)recipe.font.count); BinaryData.Put(result, 160, 0); }
                    }
                }
                var actual = GameText.Records(result); foreach (var kv in old) Storage.Require(actual[kv.Key].SequenceEqual(rows.ContainsKey(kv.Key) ? rows[kv.Key] : kv.Value), "텍스트 역변환 검증 실패: " + kv.Key);
                return result;
            }
        }
        internal static void Pad(Stream s, int alignment) { s.Position = s.Length; int count = BinaryData.Align(checked((int)s.Length), alignment) - (int)s.Length; s.Write(new byte[count], 0, count); }
        internal FontBuild BuildFont(FontRule rule, FiaTexture source, byte[] pdcm)
        {
            int width = source.Levels[0].width, height = source.Levels[0].height, align = 4 << (source.Levels.Length - 1);
            Storage.Require(source.Levels.All(l => l.pitch == (l.width + 3) / 4 * ((l.height + 3) / 4) * 16), "폰트 텍스처 압축 형식이 다릅니다.");
            var places = new List<GlyphPlacement>();
            foreach (var g in rule.glyphs)
            {
                Storage.Require(g.index >= 0 && g.index < rule.count && g.width > 0 && g.height > 0, "글리프 배치가 잘못되었습니다.");
                var p = new GlyphPlacement { Rule = g };
                if (g.character == null)
                {
                    p.sourceX = g.x; p.sourceY = g.y;
                    p.width = g.width + 1; p.height = g.height + 1;
                    p.metricX = g.x - p.sourceX; p.metricY = g.y - p.sourceY;
                    Storage.Require(p.sourceX >= 0 && p.sourceY >= 0 && p.sourceX + g.width <= width && p.sourceY + g.height <= height, "원본 글리프 영역이 잘못되었습니다.");
                }
                else { p.width = g.width + 1; p.height = g.height + 1; }
                places.Add(p);
            }
            if (rule.common) foreach (var p in places) { p.x = p.Rule.x; p.y = p.Rule.y; }
            else
            {
                int x = 0, y = 0, row = 0;
                foreach (var p in places.OrderByDescending(g => g.height).ThenByDescending(g => g.width).ThenBy(g => g.Rule.index))
                {
                    if (x + p.width > width) { x = 0; y += row; row = 0; }
                    Storage.Require(p.width <= width, "글리프가 텍스처보다 큽니다."); p.x = x; p.y = y; x += p.width; row = Math.Max(row, p.height);
                }
                while (height < y + row) height *= 2;
                Storage.Require(height <= 8192, "폰트 텍스처 높이를 초과했습니다.");
            }
            var alpha = new byte[width * height]; var metrics = new byte[rule.count * 24];
            if (rule.common)
            {
                int count = BinaryData.I32(pdcm, 144) + BinaryData.I32(pdcm, 160);
                Buffer.BlockCopy(pdcm, BinaryData.I32(pdcm, 40), metrics, 0, count * 24);
            }
            var originalAlpha = new Bc7Reader(source);
            foreach (var p in places)
            {
                var g = p.Rule; int at = g.index * 24;
                BinaryData.Put(metrics, at, (uint)g.advance); BinaryData.Put(metrics, at + 4, (uint)g.unused);
                float[] uv = { (p.x + p.metricX) / (float)width, (p.y + p.metricY) / (float)height, (p.x + p.metricX + g.width) / (float)width, (p.y + p.metricY + g.height) / (float)height };
                Buffer.BlockCopy(uv, 0, metrics, at + 8, 16);
                var glyph = g.character == null ? originalAlpha.Crop(g.x, g.y, g.width, g.height) : renderer.Glyph(g.character, rule.fontSize, g.width, g.height, rule.common, rule.alphaLevels);
                for (int y = 0; y < g.height; y++) Buffer.BlockCopy(glyph, y * g.width, alpha, (p.y + y) * width + p.x, g.width);
            }
            using (var output = new MemoryStream())
            {
                var header = source.HeaderForHeight(height); output.Write(header, 0, header.Length);
                for (int level = 0, w = width, h = height; level < source.Levels.Length; level++, w >>= 1, h >>= 1)
                {
                    int stride = (w + 3) / 4 * 16; var pixels = new byte[stride * ((h + 3) / 4)]; var zero = Bc7Alpha.Block(new byte[16]);
                    if (rule.common) Buffer.BlockCopy(source.Data, source.Levels[level].offset, pixels, 0, pixels.Length);
                    else for (int at = 0; at < pixels.Length; at += 16) Buffer.BlockCopy(zero, 0, pixels, at, 16);
                    var blocks = new HashSet<int>();
                    foreach (var p in places)
                    {
                        int x0 = (p.x >> level) / 4, y0 = (p.y >> level) / 4, x1 = BinaryData.Align((p.x + p.Rule.width + (1 << level) - 1) >> level, 4) / 4, y1 = BinaryData.Align((p.y + p.Rule.height + (1 << level) - 1) >> level, 4) / 4;
                        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) blocks.Add(y * ((w + 3) / 4) + x);
                    }
                    var block = new byte[16];
                    foreach (int index in blocks)
                    {
                        int x = index % ((w + 3) / 4) * 4, y = index / ((w + 3) / 4) * 4; bool any = false;
                        for (int yy = 0; yy < 4; yy++) for (int xx = 0; xx < 4; xx++) { byte a = x + xx < w && y + yy < h ? alpha[(y + yy) * w + x + xx] : (byte)0; block[yy * 4 + xx] = a; any |= a != 0; }
                        Buffer.BlockCopy(any ? Bc7Alpha.Block(block) : zero, 0, pixels, index * 16, 16);
                    }
                    output.Write(pixels, 0, pixels.Length);
                    if (level + 1 < source.Levels.Length)
                    {
                        var half = new byte[(w / 2) * (h / 2)];
                        for (int y = 0; y < h / 2; y++) for (int x = 0; x < w / 2; x++) { int p = y * 2 * w + x * 2; half[y * (w / 2) + x] = (byte)((alpha[p] + alpha[p + 1] + alpha[p + w] + alpha[p + w + 1] + 2) / 4); }
                        alpha = half;
                    }
                }
                return new FontBuild { Metrics = metrics, Fia = output.ToArray(), Width = width, Height = height };
            }
        }
        public void Dispose() { renderer.Dispose(); }
    }
}
