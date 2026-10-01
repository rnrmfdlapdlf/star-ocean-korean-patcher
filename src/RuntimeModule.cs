using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SO4KoreanPatcher
{
    internal sealed class PeReader
    {
        internal readonly byte[] Data;
        internal readonly int Pe, Optional, Sections, SectionCount;
        internal PeReader(byte[] data) { Data = data; Pe = BinaryData.I32(data, 60); Storage.Require(BinaryData.Magic(data, Pe, "PE\0\0") && BinaryData.U16(data, Pe + 4) == 0x8664, "x64 실행 파일이 아닙니다."); Optional = Pe + 24; SectionCount = BinaryData.U16(data, Pe + 6); Sections = Optional + BinaryData.U16(data, Pe + 20); }
        internal int Offset(int rva)
        {
            if (rva < BinaryData.I32(Data, Optional + 60)) return rva;
            for (int i = 0; i < SectionCount; i++) { int p = Sections + i * 40, start = BinaryData.I32(Data, p + 12), size = BinaryData.I32(Data, p + 16); if (rva >= start && rva - start < size) return checked(BinaryData.I32(Data, p + 20) + rva - start); }
            throw new InvalidDataException("실행 파일 주소가 파일 범위를 벗어납니다.");
        }
        internal byte[] At(int rva, int count) { return BinaryData.Slice(Data, Offset(rva), count); }
        internal string String(int rva) { int p = Offset(rva), end = Array.IndexOf(Data, (byte)0, p); if (end < 0 || end - p > 1024) throw new InvalidDataException("PE 이름이 잘못되었습니다."); return Encoding.ASCII.GetString(Data, p, end - p); }
        internal PeImage.Export[] Exports()
        {
            int p = Offset(BinaryData.I32(Data, Optional + 112)), first = BinaryData.I32(Data, p + 16), count = BinaryData.I32(Data, p + 20), names = BinaryData.I32(Data, p + 24);
            Storage.Require(count > 0 && count < 65536 && names <= count, "Windows 내보내기 표가 잘못되었습니다.");
            int functions = Offset(BinaryData.I32(Data, p + 28)), nt = Offset(BinaryData.I32(Data, p + 32)), ot = Offset(BinaryData.I32(Data, p + 36));
            var result = new Dictionary<int, PeImage.Export>();
            for (int i = 0; i < count; i++) if (BinaryData.U32(Data, functions + i * 4) != 0) result[i] = new PeImage.Export { Ordinal = first + i };
            for (int i = 0; i < names; i++) { int index = BinaryData.U16(Data, ot + i * 2); Storage.Require(result.ContainsKey(index), "Windows 내보내기 이름이 잘못되었습니다."); result[index].Name = String(BinaryData.I32(Data, nt + i * 4)); }
            return result.Values.ToArray();
        }
    }
    internal sealed class RuntimeModule
    {
        internal byte[] Bytes;
        internal int BaseSlot;
        internal Dictionary<string, int> Functions;
        private static readonly int[] wideSlots = { 0x9dc430, 0x9dc6a0, 0xa64ed0, 0xa65140, 0xa82fa0, 0xa90b40, 0xaad6f0 };
        internal static readonly string[] Names = { "엣지", "레이미", "페이즈", "리무르", "박카스", "메리클", "사라", "뮤리아", "에일멧트" };
        private static readonly int[] signatureRvas = { 0x77c105, 0x6900a8, 0x67f00e, 0x67fb26, 0x77c000, 0x779660, 0x75f5f0, 0x76d510, 0x754de0, 0x4bede90, 0x783de0 };
        private static readonly string[] signatureHashes = {
            "edaf2679a69a5bc641906fc828002ac58bcabe13386d89961c4726033c6f034d", "93e416fd3ed9f94ae40192c11aed8c32de1b8921d91ac7489f4e3d908ed0a72e", "1dc9656b226315de5c23ff99bb5ac69c6fa74458d4f033d1c07f956bdb48ab4e", "307ee1b790a4ce027eafcf17745684169a76854d76c61c41a9a9f3f10621c01e",
            "ccc41c3761a3a4da4c67bf98a8e398bfb66d7a1f0fe124f767cdb6c4cb5d7192", "0828ce7cb5b86955e625a663ade13af085ce2e60773ef5c5b3d0e678ef8eb105", "a8c597ac905cd33e0f955d81715b412a8df85d9216353c53411e54e71bc71e98", "87a6231b20980b5fc0dbd6a2c091fefcfa9bee79c44db62ed74af44b0a8de9b5", "2e2b20d064ee34eb3d569681166fb21cff8057988598bc1d95d69fd84883ca4b", "97de461370e7cd95f6873b0503f6b75bcec64fd722f6d27fd183a89433e054de", "cc42c0bc351fedeea108f928801f827376f07b4839781d742e1666b043fdbed9" };
        private static ulong Fnv(byte[] bytes) { ulong v = 14695981039346656037; unchecked { foreach (byte b in bytes) { v ^= b; v *= 1099511628211; } } return v; }
        internal static RuntimeModule Build(byte[] executable, Dictionary<string, int> glyphs)
        {
            var game = new PeReader(executable);
            Storage.Require(BinaryData.U32(executable, game.Pe + 8) == 1517901502 && BinaryData.U32(executable, game.Optional + 56) == 92655616, "지원하지 않는 게임 실행 파일입니다.");
            var signatures = new List<Tuple<int, byte[]>>();
            for (int i = 0; i < signatureRvas.Length; i++) { int size = i < 4 ? 5 : i == 8 ? 12 : 16; var bytes = game.At(signatureRvas[i], size); Storage.Require(Storage.Hash(bytes) == signatureHashes[i], "게임 코드 검증 실패: " + signatureRvas[i].ToString("X")); signatures.Add(Tuple.Create(signatureRvas[i], bytes)); }
            foreach (int slot in wideSlots) Storage.Require(BitConverter.ToUInt64(game.At(slot, 8), 0) == 0x14077c000UL, "게임 이름 표가 다릅니다.");
            Storage.Require(BitConverter.ToUInt64(game.At(0xa81628, 8), 0) == 0x144bede90UL, "게임 설명 위치 표가 다릅니다.");
            var image = new PeImage();
            image.AddImports("GetModuleHandleW", "VirtualAlloc", "VirtualProtect", "FlushInstructionCache", "GetCurrentProcess");
            image.Align(8); int gameSlot = image.Bytes(new byte[8]); int exeName = image.Wide("StarOceanTheLastHope.exe"); image.Align(16); int names = image.Rva;
            foreach (string name in Names) { var b = Encoding.Unicode.GetBytes(name); image.Bytes(b); image.Bytes(new byte[16 - b.Length]); }
            image.Align(4096); image.CodeStart = image.Rva; var c = new X64Code(image.Rva);
            Action<string> begin = label => { c.Mark(label); c.Prolog(0x20); };
            Action finish = () => c.Epilog(0x20);
            c.Mark("jp"); c.Emit(0x48, 0x83, 0xec, 0x28); c.LoadBase(gameSlot); c.Emit(0x4d, 0x85, 0xdb); c.Branch(0x84, "jp_no"); c.Emit(0x49, 0x83, 0xbb); c.U32(0x1059008); c.Emit(0); c.Branch(0x84, "jp_no"); c.GameCall(0x754de0, gameSlot); c.Emit(0x85, 0xc0, 0x0f, 0x94, 0xc0, 0x0f, 0xb6, 0xc0, 0x48, 0x83, 0xc4, 0x28, 0xc3); c.Mark("jp_no"); c.Emit(0x31, 0xc0, 0x48, 0x83, 0xc4, 0x28, 0xc3); c.SimpleUnwind(c.Address("jp"), 4, 4, 0x42);

            begin("set"); c.Emit(0x48, 0x89, 0xcb, 0x48, 0x89, 0xd6, 0x48, 0x85, 0xf6); c.Branch(0x84, "set_done"); c.Call("jp"); c.Emit(0x85, 0xc0); c.Branch(0x84, "set_done"); c.LoadBase(gameSlot); c.Emit(0x49, 0x83, 0xbb); c.U32(0x105e850); c.Emit(0); c.Branch(0x84, "set_done"); c.Emit(0xbf); c.U32(1);
            c.Mark("set_loop"); c.Emit(0x89, 0xf9); c.GameCall(0x76d510, gameSlot); c.Emit(0x48, 0x85, 0xc0); c.Branch(0x84, "set_next"); c.Emit(0x48, 0x89, 0xc1); c.GameCall(0x75f5f0, gameSlot); c.Emit(0x48, 0x39, 0xf0); c.Branch(0x84, "set_found"); c.Mark("set_next"); c.Emit(0xff, 0xc7, 0x83, 0xff, 0x09); c.Branch(0x8e, "set_loop"); c.Jump("set_done");
            c.Mark("set_found"); c.LeaRax(names); c.Emit(0xff, 0xcf, 0xc1, 0xe7, 0x04, 0x48, 0x8d, 0x34, 0x38);
            c.Mark("set_done"); c.Emit(0x48, 0x89, 0xd9, 0x48, 0x89, 0xf2); c.GameCall(0x77c000, gameSlot); finish();

            begin("convert"); c.Emit(0x48, 0x89, 0xcb, 0x48, 0x89, 0xd6, 0x4c, 0x89, 0xc7); c.GameCall(0x779660, gameSlot); c.Emit(0x49, 0x89, 0xc4); c.LeaRax(names); c.Emit(0x48, 0x89, 0xf2, 0x48, 0x29, 0xc2, 0x48, 0x81, 0xfa); c.U32(144); c.Branch(0x83, "convert_done"); c.Emit(0xf6, 0xc2, 0x0f); c.Branch(0x85, "convert_done"); c.Call("jp"); c.Emit(0x85, 0xc0); c.Branch(0x84, "convert_done"); c.Emit(0x4c, 0x8b, 0x6b, 0x38, 0x4d, 0x85, 0xed); c.Branch(0x84, "convert_done"); c.Emit(0x45, 0x31, 0xf6);
            c.Mark("convert_loop"); c.Emit(0x4d, 0x39, 0xe6); c.Branch(0x83, "convert_done"); c.Emit(0x4c, 0x89, 0xf0, 0x48, 0xd1, 0xe0, 0x48, 0x01, 0xf0, 0x0f, 0xb7, 0x08);
            int serial = 0;
            foreach (char ch in string.Concat(Names).Distinct())
            {
                int glyph; Storage.Require(glyphs.TryGetValue(ch.ToString(), out glyph) && glyph >= 2099 && glyph < 3072, "고정 이름 폰트 매핑이 없습니다: " + ch);
                string next = "glyph_" + serial++; c.Emit(0x66, 0x81, 0xf9, (byte)ch, (byte)(ch >> 8)); c.Branch(0x85, next); c.Emit(0xba); c.U32((uint)(glyph + 1)); c.Jump("convert_found"); c.Mark(next);
            }
            c.Jump("convert_next"); c.Mark("convert_found");
            // CFontData::Load (game RVA 7a8e10) stores pDCM at +8 and metrics at +38.
            // Validate both declared glyph count and physical metrics extent before reading.
            c.Emit(0x89, 0xd0, 0xff, 0xc8, 0x4c, 0x8b, 0x5b, 0x08, 0x4d, 0x85, 0xdb); c.Branch(0x84, "convert_next");
            c.Emit(0x41, 0x81, 0x3b); c.U32(0x4d434470); c.Branch(0x85, "convert_next");
            c.Emit(0x45, 0x8b, 0x93); c.U32(0x90); c.Emit(0x45, 0x03, 0x93); c.U32(0xa0); c.Branch(0x82, "convert_next");
            c.Emit(0x44, 0x39, 0xd0); c.Branch(0x83, "convert_next");
            c.Emit(0x45, 0x8b, 0x53, 0x2c, 0x45, 0x2b, 0x53, 0x28); c.Branch(0x82, "convert_next");
            c.Emit(0x41, 0x83, 0xfa, 0x18); c.Branch(0x82, "convert_next");
            c.Emit(0x41, 0x83, 0xea, 0x18, 0x6b, 0xc0, 0x18, 0x44, 0x39, 0xd0); c.Branch(0x87, "convert_next");
            c.Emit(0x49, 0x8d, 0x44, 0x05, 0x00, 0x4d, 0x89, 0xf2, 0x4d, 0x6b, 0xd2, 0x28, 0x49, 0x01, 0xfa, 0x41, 0x89, 0x52, 0x1c, 0x49, 0x89, 0x42, 0x10, 0xf3, 0x0f, 0x2a, 0x00, 0xf3, 0x41, 0x0f, 0x11, 0x42, 0x08);
            c.Mark("convert_next"); c.Emit(0x49, 0xff, 0xc6); c.Jump("convert_loop"); c.Mark("convert_done"); c.Emit(0x4c, 0x89, 0xe0); finish();

            c.Mark("rename"); c.Emit(0x48, 0x83, 0xec, 0x28); c.Call("jp"); c.Emit(0x85, 0xc0); c.Branch(0x85, "rename_zero"); c.GameCall(0x754bf0, gameSlot); c.Jump("rename_end"); c.Mark("rename_zero"); c.Emit(0x31, 0xc0); c.Mark("rename_end"); c.Emit(0x48, 0x83, 0xc4, 0x28, 0xc3); c.SimpleUnwind(c.Address("rename"), 4, 4, 0x42);

            begin("memo"); c.Emit(0x48, 0x89, 0xcb, 0x48, 0x89, 0xd6, 0x4c, 0x89, 0xc7); c.Call("jp"); c.Emit(0x85, 0xc0); c.Branch(0x84, "memo_done"); c.Emit(0x83, 0xbb); c.U32(0x1a4); c.Emit(0); c.Branch(0x8e, "memo_done"); c.Emit(0x48, 0x8b, 0x43, 0x08, 0x48, 0x85, 0xc0); c.Branch(0x84, "memo_done"); c.LoadBase(gameSlot); c.Emit(0x49, 0x8d, 0x93); c.U32(0xa64f88); c.Emit(0x48, 0x39, 0x10); c.Branch(0x85, "memo_done");
            for (int i = 0; i < 6; i++) { c.Emit(0x48, 0x39, 0x58, (byte)(0x48 + i * 8)); c.Branch(0x84, "memo_dirty"); } c.Jump("memo_done"); c.Mark("memo_dirty"); c.Emit(0x83, 0x8b); c.U32(0x1d0); c.Emit(8); c.Mark("memo_done"); c.Emit(0x48, 0x89, 0xd9, 0x48, 0x89, 0xf2, 0x49, 0x89, 0xf8); c.GameCall(0x4bede90, gameSlot); finish();

            c.Mark("guide"); c.Prolog(0xb0); // 0xb0-byte aligned stack including a 132-byte temporary row.
            c.Emit(0x48, 0x89, 0xcb, 0x41, 0x89, 0xd5, 0x45, 0x89, 0xc6); c.Call("jp"); c.Emit(0x85, 0xc0); c.Branch(0x84, "guide_original"); c.Emit(0x41, 0x83, 0xfd, 0x14); c.Branch(0x85, "guide_original"); c.Emit(0x4c, 0x8b, 0xa3); c.U32(0xf0); c.Emit(0x4d, 0x85, 0xe4); c.Branch(0x84, "guide_original");
            c.Emit(0x4c, 0x89, 0xe6, 0x48, 0x8d, 0x7c, 0x24, 0x20, 0xb9); c.U32(20); c.Emit(0xf3, 0xa4, 0x49, 0x8d, 0xb4, 0x24); c.U32(20 + 20 * 112); c.Emit(0xb9); c.U32(112); c.Emit(0xf3, 0xa4, 0x45, 0x31, 0xff);
            for (int i = 0; i < 8; i++) { string next = "guide_entry_" + i; c.Emit(0x81, 0xbc, 0x24); c.U32((uint)(32 + 20 + i * 12 + 8)); c.U32(160003); c.Branch(0x85, next); c.Emit(0x66, 0xc7, 0x84, 0x24); c.U32((uint)(32 + 20 + i * 12)); c.Emit(0, 0, 0x41, 0xbf); c.U32(1); c.Mark(next); }
            c.Emit(0x45, 0x85, 0xff); c.Branch(0x84, "guide_original"); c.Emit(0x48, 0x8d, 0x44, 0x24, 0x20, 0x48, 0x89, 0x83); c.U32(0xf0); c.Emit(0x48, 0x89, 0xd9, 0x31, 0xd2, 0x45, 0x89, 0xf0); c.GameCall(0x67fba0, gameSlot); c.Emit(0x4c, 0x89, 0xa3); c.U32(0xf0); c.Jump("guide_end"); c.Mark("guide_original"); c.Emit(0x48, 0x89, 0xd9, 0x44, 0x89, 0xea, 0x45, 0x89, 0xf0); c.GameCall(0x67fba0, gameSlot); c.Mark("guide_end"); c.Epilog(0xb0);

            EmitInstaller(c, image, gameSlot, exeName, signatures);
            int entry = c.Address("entry"); var functions = new[] { "set", "convert", "rename", "memo", "guide" }.ToDictionary(n => n, c.Address);
            image.Bytes(c.Finish()); image.AddUnwind(c.Unwinds);
            string windows = Path.Combine(Environment.SystemDirectory, "wininet.dll"); var exports = new PeReader(File.ReadAllBytes(windows)).Exports();
            foreach (var e in exports) e.Forward = Path.Combine(Environment.SystemDirectory, "wininet") + "." + (e.Name ?? "#" + e.Ordinal);
            image.AddExports(exports);
            return new RuntimeModule { Bytes = image.Finish(entry), BaseSlot = gameSlot, Functions = functions };
        }
        private static void EmitInstaller(X64Code c, PeImage image, int gameSlot, int exeName, List<Tuple<int, byte[]>> signatures)
        {
            c.Mark("hash"); c.Emit(0x48, 0xb8); c.U64(14695981039346656037); c.Emit(0x49, 0xb8); c.U64(1099511628211); c.Mark("hash_loop"); c.Emit(0x44, 0x0f, 0xb6, 0x09, 0x4c, 0x31, 0xc8, 0x49, 0x0f, 0xaf, 0xc0, 0x48, 0xff, 0xc1, 0xff, 0xca); c.Branch(0x85, "hash_loop"); c.Emit(0xc3);
            c.Mark("replace"); c.Emit(0x53, 0x56, 0x57, 0x48, 0x83, 0xec, 0x30, 0x48, 0x89, 0xcb, 0x48, 0x89, 0xd6, 0x4c, 0x89, 0x44, 0x24, 0x28, 0x4c, 0x89, 0xc2, 0x41, 0xb8); c.U32(0x40); c.Emit(0x4c, 0x8d, 0x4c, 0x24, 0x20); c.CallImport(image.Imports["VirtualProtect"]); c.Emit(0x85, 0xc0); c.Branch(0x84, "replace_done");
            c.Emit(0x48, 0x89, 0xdf, 0x48, 0x8b, 0x4c, 0x24, 0x28, 0xf3, 0xa4); c.CallImport(image.Imports["GetCurrentProcess"]); c.Emit(0x48, 0x89, 0xc1, 0x48, 0x89, 0xda, 0x4c, 0x8b, 0x44, 0x24, 0x28); c.CallImport(image.Imports["FlushInstructionCache"]);
            c.Emit(0x48, 0x89, 0xd9, 0x48, 0x8b, 0x54, 0x24, 0x28, 0x44, 0x8b, 0x44, 0x24, 0x20, 0x4c, 0x8d, 0x4c, 0x24, 0x24); c.CallImport(image.Imports["VirtualProtect"]); c.Mark("replace_done"); c.Emit(0x48, 0x83, 0xc4, 0x30, 0x5f, 0x5e, 0x5b, 0xc3); c.SimpleUnwind(c.Address("replace"), 7, 7, 0x52, 3, 0x70, 2, 0x60, 1, 0x30);

            c.Mark("entry"); c.Emit(0x83, 0xfa, 0x01); c.Branch(0x84, "entry_attach"); c.Emit(0xb8); c.U32(1); c.Emit(0xc3); c.Mark("entry_attach"); c.Prolog(0x30); c.Emit(0x48, 0x8d, 0x0d); c.Rip(exeName); c.CallImport(image.Imports["GetModuleHandleW"]); c.Emit(0x48, 0x85, 0xc0); c.Branch(0x84, "entry_ok"); c.Emit(0x48, 0x89, 0xc3, 0x8b, 0x40, 0x3c, 0x48, 0x01, 0xd8, 0x81, 0x78, 0x08); c.U32(1517901502); c.Branch(0x85, "entry_fail"); c.Emit(0x81, 0x78, 0x50); c.U32(92655616); c.Branch(0x85, "entry_fail");
            foreach (var s in signatures) { c.Emit(0x48, 0x8d, 0x8b); c.U32((uint)s.Item1); c.Emit(0xba); c.U32((uint)s.Item2.Length); c.Call("hash"); c.Emit(0x49, 0xba); c.U64(Fnv(s.Item2)); c.Emit(0x4c, 0x39, 0xd0); c.Branch(0x85, "entry_fail"); }
            foreach (int slot in wideSlots.Concat(new[] { 0xa81628 })) { c.Emit(0x48, 0x8d, 0x83); c.U32(slot == 0xa81628 ? 0x4bede90U : 0x77c000U); c.Emit(0x48, 0x39, 0x83); c.U32((uint)slot); c.Branch(0x85, "entry_fail"); }
            c.Emit(0x48, 0x89, 0x1d); c.Rip(gameSlot); c.Emit(0x49, 0x89, 0xdc, 0x49, 0x81, 0xe4); c.U32(0xffff0000); c.Emit(0x49, 0x81, 0xc4); c.U32(0x6000000); c.Emit(0x4c, 0x8d, 0xab); c.U32(0x70000000);
            c.Mark("allocate"); c.Emit(0x4c, 0x89, 0xe1, 0xba); c.U32(4096); c.Emit(0x41, 0xb8); c.U32(0x3000); c.Emit(0x41, 0xb9); c.U32(4); c.CallImport(image.Imports["VirtualAlloc"]); c.Emit(0x48, 0x85, 0xc0); c.Branch(0x85, "allocated"); c.Emit(0x49, 0x81, 0xc4); c.U32(65536); c.Emit(0x4d, 0x39, 0xec); c.Branch(0x82, "allocate"); c.Jump("entry_fail");
            c.Mark("allocated"); c.Emit(0x48, 0x89, 0xc6);
            string[] bridge = { "convert", "rename", "guide" };
            for (int i = 0; i < 3; i++) { c.Emit(0x66, 0xc7, 0x46, (byte)(i * 16), 0xff, 0x25, 0xc7, 0x46, (byte)(i * 16 + 2)); c.U32(0); c.LeaRax(c.Address(bridge[i])); c.Emit(0x48, 0x89, 0x46, (byte)(i * 16 + 6)); }
            c.Emit(0x48, 0x89, 0xf1, 0xba); c.U32(4096); c.Emit(0x41, 0xb8); c.U32(0x20); c.Emit(0x4c, 0x8d, 0x4c, 0x24, 0x20); c.CallImport(image.Imports["VirtualProtect"]); c.Emit(0x85, 0xc0); c.Branch(0x84, "entry_fail");
            int[] sites = { 0x77c105, 0x6900a8, 0x67f00e, 0x67fb26 }, destinations = { 0, 16, 32, 32 };
            for (int i = 0; i < sites.Length; i++)
            {
                c.Emit(0xc6, 0x44, 0x24, 0x28, 0xe8, 0x48, 0x8d, 0x46, (byte)destinations[i], 0x48, 0x8d, 0x93); c.U32((uint)(sites[i] + 5)); c.Emit(0x48, 0x29, 0xd0, 0x89, 0x44, 0x24, 0x29, 0x48, 0x8d, 0x8b); c.U32((uint)sites[i]); c.Emit(0x48, 0x8d, 0x54, 0x24, 0x28, 0x41, 0xb8); c.U32(5); c.Call("replace"); c.Emit(0x85, 0xc0); c.Branch(0x84, "entry_fail");
            }
            foreach (int slot in wideSlots.Concat(new[] { 0xa81628 }))
            {
                c.LeaRax(c.Address(slot == 0xa81628 ? "memo" : "set")); c.Emit(0x48, 0x89, 0x44, 0x24, 0x28, 0x48, 0x8d, 0x8b); c.U32((uint)slot); c.Emit(0x48, 0x8d, 0x54, 0x24, 0x28, 0x41, 0xb8); c.U32(8); c.Call("replace"); c.Emit(0x85, 0xc0); c.Branch(0x84, "entry_fail");
            }
            c.Mark("entry_ok"); c.Emit(0xb8); c.U32(1); c.Jump("entry_end"); c.Mark("entry_fail"); c.Emit(0x31, 0xc0); c.Mark("entry_end"); c.Epilog(0x30);
        }
    }
}
