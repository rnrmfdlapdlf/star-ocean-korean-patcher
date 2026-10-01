using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SO4KoreanPatcher
{
    internal static class BinaryData
    {
        internal static int Align(int n, int a) { return checked((n + a - 1) / a * a); }
        internal static uint U32(byte[] b, int p) { Bounds(b, p, 4); return BitConverter.ToUInt32(b, p); }
        internal static int I32(byte[] b, int p) { return checked((int)U32(b, p)); }
        internal static ushort U16(byte[] b, int p) { Bounds(b, p, 2); return BitConverter.ToUInt16(b, p); }
        internal static void Put(byte[] b, int p, uint v) { Bounds(b, p, 4); Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, p, 4); }
        internal static void Bounds(byte[] b, int p, int n) { if (p < 0 || n < 0 || p > b.Length - n) throw new InvalidDataException("리소스 범위를 벗어났습니다."); }
        internal static byte[] Slice(byte[] b, int p, int n) { Bounds(b, p, n); var r = new byte[n]; Buffer.BlockCopy(b, p, r, 0, n); return r; }
        internal static byte[] Read(Stream s, long p, int n) { if (p < 0 || n < 0 || p > s.Length - n) throw new InvalidDataException("파일 범위를 벗어났습니다."); var r = new byte[n]; s.Position = p; Storage.ReadExactly(s, r, n); return r; }
        internal static bool Magic(byte[] b, int p, string text) { if (p < 0 || p > b.Length - text.Length) return false; for (int i = 0; i < text.Length; i++) if (b[p + i] != text[i]) return false; return true; }
    }

    internal sealed class OuterTable
    {
        internal const int Size = 0xc0000;
        internal readonly byte[] Encoded;
        internal readonly uint[] Values;
        internal OuterTable(byte[] bytes)
        {
            if (bytes.Length != Size) throw new InvalidDataException("외부 목차 크기가 다릅니다.");
            Encoded = (byte[])bytes.Clone(); Values = new uint[Size / 4];
            Buffer.BlockCopy(bytes, 0, Values, 0, Size);
            unchecked
            {
                uint key = 0x13578642, seed = key;
                for (int i = 0; i < Values.Length; i += 3)
                {
                    Values[i] ^= key; key ^= key << 1;
                    Values[i + 1] ^= key; key = ~key ^ seed;
                    Values[i + 2] ^= key; key ^= (key << 2) ^ seed; key ^= key << 1;
                }
            }
        }
        internal string FileName(int id) { return (Values[checked(id * 3 + 2)] & 0x10000000) != 0 ? "0001.bin" : "0000.bin"; }
        internal long Offset(int id) { return (Values[checked(id * 3 + 2)] & 0x0fffffff) * 2048L; }
        internal int Length(int id) { return checked((int)Values[checked(id * 3 + 1)]); }
        internal void Relocate(int id, long offset, int length)
        {
            if (offset % 2048 != 0 || length % 2048 != 0 || offset < 0 || offset / 2048 >= 0x10000000) throw new InvalidDataException("패키지 정렬이 올바르지 않습니다.");
            uint[] v = { (uint)(length / 2048), (uint)length, (uint)(offset / 2048) };
            for (int i = 0; i < 3; i++) { int p = (id * 3 + i) * 4; BinaryData.Put(Encoded, p, BinaryData.U32(Encoded, p) ^ Values[id * 3 + i] ^ v[i]); Values[id * 3 + i] = v[i]; }
        }
    }

    internal sealed class KcapMember
    {
        internal int Index, Offset, Size, Extent;
    }
    internal sealed class Kcap
    {
        internal readonly byte[] Header;
        internal readonly KcapMember[] Members;
        internal readonly int Length;
        internal Kcap(Stream stream, long offset, int length)
        {
            var first = BinaryData.Read(stream, offset, 16);
            if (!BinaryData.Magic(first, 0, "KCAP")) throw new InvalidDataException("KCAP 헤더가 아닙니다.");
            int count = BinaryData.I32(first, 8); Length = BinaryData.I32(first, 12);
            if (count < 1 || count > 100000 || Length > length || Length < 16 + count * 16) throw new InvalidDataException("KCAP 목차가 올바르지 않습니다.");
            Header = BinaryData.Read(stream, offset, 16 + count * 16); Members = new KcapMember[count];
            for (int i = 0; i < count; i++)
            {
                int p = 16 + i * 16;
                var m = new KcapMember { Index = i, Size = BinaryData.I32(Header, p + 8), Offset = BinaryData.I32(Header, p + 12) };
                if (m.Offset < Header.Length || m.Offset > Length || m.Size < 0) throw new InvalidDataException("KCAP 멤버 위치가 올바르지 않습니다.");
                Members[i] = m;
            }
            foreach (var m in Members) m.Extent = Members.Where(x => x.Offset > m.Offset).Select(x => x.Offset).DefaultIfEmpty(Length).Min() - m.Offset;
        }
        internal byte[] ReadPacked(Stream s, long packageOffset, int index)
        {
            var m = Members[index];
            byte[] head = BinaryData.Read(s, packageOffset + m.Offset, Math.Min(32, m.Extent));
            int n = BinaryData.Magic(head, 0, "SLZ") ? checked(BinaryData.I32(head, 8) + BinaryData.I32(head, 20)) : m.Size;
            if (n > m.Extent) throw new InvalidDataException("압축 멤버가 다음 리소스를 침범합니다.");
            return BinaryData.Read(s, packageOffset + m.Offset, n);
        }
    }

    internal static class Slz
    {
        internal sealed class Chunk { internal byte[] Raw, Packed; }
        internal static List<Chunk> Chunks(byte[] input)
        {
            if (!BinaryData.Magic(input, 0, "SLZ") || input.Length < 32 || input[3] > 3) throw new InvalidDataException("SLZ 헤더가 올바르지 않습니다.");
            int mode = input[3], size = BinaryData.I32(input, 12), p = BinaryData.I32(input, 20), end = checked(p + BinaryData.I32(input, 8));
            if (size > 256 * 1024 * 1024 || p < 32 || end > input.Length) throw new InvalidDataException("SLZ 크기가 올바르지 않습니다.");
            var result = new List<Chunk>();
            if (mode == 0) { BinaryData.Bounds(input, p, size); result.Add(new Chunk { Raw = BinaryData.Slice(input, p, size) }); return result; }
            for (int done = 0; done < size; )
            {
                if (p > end - 2) throw new InvalidDataException("SLZ 청크 길이가 없습니다.");
                int n = BinaryData.U16(input, p), extent = n == 0 ? 65536 : n, expected = Math.Min(65536, size - done);
                if (p + 2 > end - extent) throw new InvalidDataException("SLZ 청크가 잘렸습니다.");
                byte[] raw = n == 0 ? BinaryData.Slice(input, p + 2, expected) : DecodeChunk(BinaryData.Slice(input, p + 2, extent), expected, mode);
                result.Add(new Chunk { Raw = raw, Packed = BinaryData.Slice(input, p, extent + 2) }); p += extent + 2; done += expected;
            }
            return result;
        }
        internal static byte[] Decode(byte[] input)
        {
            if (!BinaryData.Magic(input, 0, "SLZ")) return (byte[])input.Clone();
            using (var output = new MemoryStream()) { foreach (var c in Chunks(input)) output.Write(c.Raw, 0, c.Raw.Length); return output.ToArray(); }
        }
        internal static byte[] DecodeChunk(byte[] b, int expected, int mode)
        {
            var output = new byte[expected + 300]; int p = 0, at = 0; uint flags = 0;
            while (p < b.Length && at < expected)
            {
                flags >>= 1;
                if (flags <= 0xffff) { flags = 0x00ff0000U | b[p++]; if (mode == 3) { if (p >= b.Length) throw new InvalidDataException("SLZ 플래그가 잘렸습니다."); flags |= 0xff000000U | ((uint)b[p++] << 8); } }
                if ((flags & 1) != 0)
                {
                    int count = mode == 3 ? 2 : 1; BinaryData.Bounds(b, p, count); Buffer.BlockCopy(b, p, output, at, count); p += count; at += count;
                }
                else
                {
                    BinaryData.Bounds(b, p, 2); int lo = b[p++], hi = b[p++];
                    if (mode == 2 && hi >= 0xf0)
                    {
                        int n, value; if (hi == 0xf0) { n = lo + 19; BinaryData.Bounds(b, p, 1); value = b[p++]; } else { n = (hi & 15) + 3; value = lo; }
                        for (int i = 0; i < n; i++) output[at++] = (byte)value;
                    }
                    else
                    {
                        int distance = lo | ((hi & 15) << 8), n = (hi >> 4) + 3;
                        if (mode == 3) { distance *= 2; n = (n - 1) * 2; }
                        if (distance == 0) break;
                        // This unused token occurs in original game streams. It emits no bytes.
                        if (distance > at) continue;
                        for (int i = 0; i < n; i++, at++) output[at] = output[at - distance];
                    }
                }
            }
            if (at < expected) throw new InvalidDataException("SLZ 해제 길이가 부족합니다.");
            return BinaryData.Slice(output, 0, expected);
        }
        private sealed class Token { internal bool Literal; internal byte[] Bytes; }
        private static int Key(byte[] b, int at, int size) { int k = 0; for (int i = 0; i < size && at + i < b.Length; i++) k |= b[at + i] << (8 * i); return k; }
        internal static byte[] EncodeChunk(byte[] raw, int mode, bool optimal)
        {
            int step = mode == 3 ? 2 : 1, keySize = mode == 3 ? 4 : 3, window = mode == 3 ? 8190 : 4095, limit = mode == 3 ? 34 : 17;
            if ((mode != 2 && mode != 3) || raw.Length % step != 0) throw new InvalidDataException("지원하지 않는 SLZ 인코딩 단위입니다.");
            int n = raw.Length; var lengths = new int[n]; var distances = new int[n]; var runs = new int[n]; var positions = new Dictionary<int, List<int>>();
            for (int at = n - 1; at >= 0; at--) runs[at] = at + 1 < n && raw[at] == raw[at + 1] ? Math.Min(274, runs[at + 1] + 1) : 1;
            for (int at = 0; at < n; at += step)
            {
                int key = Key(raw, at, keySize); List<int> q;
                if (!positions.TryGetValue(key, out q)) positions[key] = q = new List<int>();
                for (int j = q.Count - 1; j >= 0 && at - q[j] <= window && (optimal || mode != 2 || runs[at] < 17); j--)
                {
                    int count = keySize, max = Math.Min(limit, n - at); if (max < keySize) break;
                    while (count < max && raw[q[j] + count] == raw[at + count] && (step == 1 || raw[q[j] + count + 1] == raw[at + count + 1])) count += step;
                    if (count > lengths[at]) { lengths[at] = count; distances[at] = at - q[j]; }
                    if (count == max) break;
                }
                q.Add(at);
                int expired = 0; while (expired < q.Count && at - q[expired] > window) expired++;
                if (expired > 0) q.RemoveRange(0, expired);
            }
            var choices = new int[n]; var kind = new int[n]; var cost = new int[n + 1];
            for (int at = n - step; at >= 0; at -= step)
            {
                if (!optimal)
                {
                    choices[at] = step;
                    if (lengths[at] >= keySize) { choices[at] = lengths[at]; kind[at] = 1; }
                    if (mode == 2 && runs[at] >= 4 && runs[at] >= lengths[at]) { choices[at] = runs[at]; kind[at] = 2; }
                    continue;
                }
                int best = (step == 2 ? 17 : 9) + cost[at + step]; choices[at] = step;
                for (int count = keySize; count <= lengths[at]; count += step)
                { int c = 17 + cost[at + count]; if (c < best) { best = c; choices[at] = count; kind[at] = 1; } }
                if (mode == 2) for (int count = 4; count <= runs[at]; count++)
                { int c = (count <= 18 ? 17 : 25) + cost[at + count]; if (c < best) { best = c; choices[at] = count; kind[at] = 2; } }
                cost[at] = best;
            }
            var tokens = new List<Token>();
            for (int at = 0; at < n; )
            {
                int count = choices[at], type = kind[at]; byte[] bytes;
                if (type == 0) bytes = BinaryData.Slice(raw, at, step);
                else if (type == 1) { int d = distances[at] / step; bytes = new[] { (byte)d, (byte)(((mode == 3 ? count / 2 - 2 : count - 3) << 4) | (d >> 8)) }; }
                else bytes = count <= 18 ? new[] { raw[at], (byte)(0xf0 + count - 3) } : new[] { (byte)(count - 19), (byte)0xf0, raw[at] };
                tokens.Add(new Token { Literal = type == 0, Bytes = bytes }); at += count;
            }
            using (var output = new MemoryStream())
            {
                int group = mode == 3 ? 16 : 8;
                for (int at = 0; at < tokens.Count; at += group)
                {
                    int flags = 0; for (int j = 0; j < group && at + j < tokens.Count; j++) if (tokens[at + j].Literal) flags |= 1 << j;
                    output.WriteByte((byte)flags); if (mode == 3) output.WriteByte((byte)(flags >> 8));
                    for (int j = 0; j < group && at + j < tokens.Count; j++) output.Write(tokens[at + j].Bytes, 0, tokens[at + j].Bytes.Length);
                }
                return output.ToArray();
            }
        }
        internal static byte[] Encode(byte[] raw, byte[] original, bool optimal = false, int forcedMode = -1)
        {
            if (!BinaryData.Magic(original, 0, "SLZ")) return (byte[])raw.Clone();
            int mode = forcedMode == 2 ? 2 : original[3] == 0 ? 0 : raw.Length % 2 == 0 ? 3 : 2; var old = Chunks(original);
            if (mode == original[3] && old.SelectMany(c => c.Raw).SequenceEqual(raw))
                return BinaryData.Slice(original, 0, BinaryData.I32(original, 20) + BinaryData.I32(original, 8));
            using (var output = new MemoryStream())
            {
                int headerSize = BinaryData.I32(original, 20); var head = BinaryData.Slice(original, 0, headerSize); output.Write(head, 0, head.Length);
                if (mode == 0) output.Write(raw, 0, raw.Length);
                else for (int at = 0, index = 0; at < raw.Length; at += 65536, index++)
                {
                    var chunk = BinaryData.Slice(raw, at, Math.Min(65536, raw.Length - at));
                    var packed = EncodeChunk(chunk, mode, optimal);
                    if (packed.Length < 65536) { output.WriteByte((byte)packed.Length); output.WriteByte((byte)(packed.Length >> 8)); output.Write(packed, 0, packed.Length); }
                    else { output.WriteByte(0); output.WriteByte(0); output.Write(chunk, 0, chunk.Length); output.Write(new byte[65536 - chunk.Length], 0, 65536 - chunk.Length); }
                }
                var result = output.ToArray(); result[3] = (byte)mode; BinaryData.Put(result, 8, (uint)(result.Length - head.Length)); BinaryData.Put(result, 12, (uint)raw.Length);
                if (!Decode(result).SequenceEqual(raw)) throw new InvalidDataException("SLZ 역변환 검증에 실패했습니다.");
                return result;
            }
        }
        internal static byte[] Stored(byte[] raw, byte[] original)
        {
            if (!BinaryData.Magic(original, 0, "SLZ")) return (byte[])raw.Clone();
            int header = BinaryData.I32(original, 20); var result = new byte[header + raw.Length]; Buffer.BlockCopy(original, 0, result, 0, header); result[3] = 0; BinaryData.Put(result, 8, (uint)raw.Length); BinaryData.Put(result, 12, (uint)raw.Length); Buffer.BlockCopy(raw, 0, result, header, raw.Length); return result;
        }
    }
}
