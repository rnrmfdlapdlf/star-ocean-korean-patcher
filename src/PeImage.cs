using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SO4KoreanPatcher
{
    // Builds our own small x64 PE module. No compiler, object file, or prebuilt DLL is embedded.
    internal sealed class PeImage
    {
        internal sealed class Export { internal string Name, Forward; internal int Ordinal, Rva = 0; }
        internal readonly MemoryStream Body = new MemoryStream();
        internal readonly Dictionary<string, int> Imports = new Dictionary<string, int>();
        private int exportRva, exportSize, importRva, importSize, exceptionRva, exceptionSize;
        internal int CodeStart;
        internal const int SectionRva = 0x1000;
        internal int Rva { get { return checked(SectionRva + (int)Body.Position); } }
        internal void Align(int n) { ResourceCompiler.Pad(Body, n); }
        internal int Bytes(byte[] bytes) { int at = Rva; Body.Write(bytes, 0, bytes.Length); return at; }
        internal int Ascii(string text) { return Bytes(Encoding.ASCII.GetBytes(text + "\0")); }
        internal int Wide(string text) { return Bytes(Encoding.Unicode.GetBytes(text + "\0")); }
        internal void Put(int rva, uint value) { long at = Body.Position; Body.Position = rva - SectionRva; byte[] b = BitConverter.GetBytes(value); Body.Write(b, 0, b.Length); Body.Position = at; }
        internal void Put64(int rva, ulong value) { long at = Body.Position; Body.Position = rva - SectionRva; byte[] b = BitConverter.GetBytes(value); Body.Write(b, 0, b.Length); Body.Position = at; }
        internal void AddImports(params string[] names)
        {
            Align(8); importRva = Rva; Bytes(new byte[40]); importSize = 40;
            int dll = Ascii("KERNEL32.dll"); var hints = new List<int>();
            foreach (var name in names) { Align(2); hints.Add(Rva); Bytes(new byte[2]); Ascii(name); }
            Align(8); int lookup = Rva; foreach (int hint in hints) Bytes(BitConverter.GetBytes((ulong)hint)); Bytes(new byte[8]);
            int table = Rva; for (int i = 0; i < hints.Count; i++) { Imports[names[i]] = Rva; Bytes(BitConverter.GetBytes((ulong)hints[i])); } Bytes(new byte[8]);
            Put(importRva, (uint)lookup); Put(importRva + 12, (uint)dll); Put(importRva + 16, (uint)table);
        }
        internal void AddExports(IEnumerable<Export> exports)
        {
            var all = exports.ToArray(); Align(4); exportRva = Rva; Bytes(new byte[40]);
            int dll = Ascii("wininet.dll"), first = all.Min(x => x.Ordinal), count = all.Max(x => x.Ordinal) - first + 1;
            Align(4); int functions = Rva; Bytes(new byte[count * 4]);
            var named = all.Where(x => x.Name != null).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            int names = Rva; Bytes(new byte[named.Length * 4]); int ordinals = Rva; Bytes(new byte[named.Length * 2]);
            for (int i = 0; i < named.Length; i++) { Put(names + i * 4, (uint)Ascii(named[i].Name)); long pos = Body.Position; Body.Position = ordinals - SectionRva + i * 2; byte[] o = BitConverter.GetBytes((ushort)(named[i].Ordinal - first)); Body.Write(o, 0, 2); Body.Position = pos; }
            foreach (var e in all) Put(functions + (e.Ordinal - first) * 4, (uint)(e.Forward == null ? e.Rva : Ascii(e.Forward)));
            exportSize = Rva - exportRva;
            Put(exportRva + 12, (uint)dll); Put(exportRva + 16, (uint)first); Put(exportRva + 20, (uint)count); Put(exportRva + 24, (uint)named.Length); Put(exportRva + 28, (uint)functions); Put(exportRva + 32, (uint)names); Put(exportRva + 36, (uint)ordinals);
        }
        internal void AddUnwind(IEnumerable<X64Code.Unwind> functions)
        {
            var entries = new List<Tuple<int, int, int>>();
            foreach (var f in functions.OrderBy(f => f.Start))
            {
                Align(4); int info = Rva;
                Bytes(new[] { (byte)1, f.PrologSize, (byte)(f.Codes.Length / 2), (byte)0 }); Bytes(f.Codes); Align(4);
                entries.Add(Tuple.Create(f.Start, f.End, info));
            }
            Align(4); exceptionRva = Rva;
            foreach (var e in entries) { Bytes(BitConverter.GetBytes(e.Item1)); Bytes(BitConverter.GetBytes(e.Item2)); Bytes(BitConverter.GetBytes(e.Item3)); }
            exceptionSize = Rva - exceptionRva;
        }
        internal byte[] Finish(int entry)
        {
            int raw = BinaryData.Align((int)Body.Length, 512); var result = new byte[512 + raw]; result[0] = 77; result[1] = 90; BinaryData.Put(result, 60, 128);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("PE\0\0"), 0, result, 128, 4);
            Action<int, ushort> put16 = (p, v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, result, p, 2);
            put16(132, 0x8664); put16(134, (ushort)(CodeStart == 0 ? 1 : 2)); put16(148, 240); put16(150, 0x2022);
            int opt = 152; put16(opt, 0x20b); BinaryData.Put(result, opt + 4, (uint)raw); BinaryData.Put(result, opt + 16, (uint)entry); BinaryData.Put(result, opt + 20, SectionRva);
            Buffer.BlockCopy(BitConverter.GetBytes(0x180000000UL), 0, result, opt + 24, 8);
            BinaryData.Put(result, opt + 32, 4096); BinaryData.Put(result, opt + 36, 512); put16(opt + 40, 6); put16(opt + 48, 6);
            BinaryData.Put(result, opt + 56, (uint)BinaryData.Align(SectionRva + (int)Body.Length, 4096)); BinaryData.Put(result, opt + 60, 512); put16(opt + 68, 2); put16(opt + 70, 0x160);
            Buffer.BlockCopy(BitConverter.GetBytes(1048576UL), 0, result, opt + 72, 8); Buffer.BlockCopy(BitConverter.GetBytes(4096UL), 0, result, opt + 80, 8); Buffer.BlockCopy(BitConverter.GetBytes(1048576UL), 0, result, opt + 88, 8); Buffer.BlockCopy(BitConverter.GetBytes(4096UL), 0, result, opt + 96, 8);
            BinaryData.Put(result, opt + 108, 16); BinaryData.Put(result, opt + 112, (uint)exportRva); BinaryData.Put(result, opt + 116, (uint)exportSize); BinaryData.Put(result, opt + 120, (uint)importRva); BinaryData.Put(result, opt + 124, (uint)importSize);
            BinaryData.Put(result, opt + 136, (uint)exceptionRva); BinaryData.Put(result, opt + 140, (uint)exceptionSize);
            int section = opt + 240; Buffer.BlockCopy(Encoding.ASCII.GetBytes(".patch"), 0, result, section, 6); BinaryData.Put(result, section + 8, (uint)Body.Length); BinaryData.Put(result, section + 12, SectionRva); BinaryData.Put(result, section + 16, (uint)raw); BinaryData.Put(result, section + 20, 512); BinaryData.Put(result, section + 36, 0xe0000060);
            if (CodeStart != 0)
            {
                int dataSize = CodeStart - SectionRva;
                Storage.Require(dataSize > 0 && dataSize % 4096 == 0, "PE 코드 정렬이 잘못되었습니다.");
                BinaryData.Put(result, section + 8, (uint)dataSize); BinaryData.Put(result, section + 16, (uint)dataSize); BinaryData.Put(result, section + 36, 0xc0000040);
                section += 40; Buffer.BlockCopy(Encoding.ASCII.GetBytes(".code"), 0, result, section, 5); BinaryData.Put(result, section + 8, (uint)(Body.Length - dataSize)); BinaryData.Put(result, section + 12, (uint)CodeStart); BinaryData.Put(result, section + 16, (uint)(raw - dataSize)); BinaryData.Put(result, section + 20, (uint)(512 + dataSize)); BinaryData.Put(result, section + 36, 0x60000020);
            }
            Buffer.BlockCopy(Body.ToArray(), 0, result, 512, (int)Body.Length); return result;
        }
    }
    internal sealed class X64Code
    {
        internal sealed class Unwind { internal int Start, End; internal byte PrologSize; internal byte[] Codes; }
        internal readonly List<Unwind> Unwinds = new List<Unwind>();
        private Unwind active;
        private readonly List<byte> bytes = new List<byte>();
        private readonly Dictionary<string, int> labels = new Dictionary<string, int>();
        private readonly List<Tuple<int, string>> fixups = new List<Tuple<int, string>>();
        private readonly int start;
        internal X64Code(int rva) { start = rva; }
        internal int Rva { get { return start + bytes.Count; } }
        internal void Mark(string name) { labels.Add(name, Rva); }
        internal int Address(string name) { return labels[name]; }
        internal void Emit(params byte[] b) { bytes.AddRange(b); }
        internal void Prolog(int stack)
        {
            int at = Rva; Emit(0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57); Stack(false, stack);
            var codes = new List<byte>(); byte size = (byte)(Rva - at);
            if (stack <= 128) codes.AddRange(new[] { size, (byte)(((stack / 8 - 1) << 4) | 2) });
            else { codes.AddRange(new[] { size, (byte)1 }); codes.AddRange(BitConverter.GetBytes((ushort)(stack / 8))); }
            codes.AddRange(new byte[] { 11,0xf0,9,0xe0,7,0xd0,5,0xc0,3,0x70,2,0x60,1,0x30 });
            active = new Unwind { Start = at, PrologSize = size, Codes = codes.ToArray() };
        }
        internal void Epilog(int stack) { Stack(true, stack); Emit(0x41, 0x5f, 0x41, 0x5e, 0x41, 0x5d, 0x41, 0x5c, 0x5f, 0x5e, 0x5b, 0xc3); active.End = Rva; Unwinds.Add(active); active = null; }
        private void Stack(bool add, int size) { Emit(0x48, size <= 127 ? (byte)0x83 : (byte)0x81, add ? (byte)0xc4 : (byte)0xec); if (size <= 127) Emit((byte)size); else U32((uint)size); }
        internal void SimpleUnwind(int start, byte size, params byte[] codes) { Unwinds.Add(new Unwind { Start = start, End = Rva, PrologSize = size, Codes = codes }); }
        internal void U32(uint v) { bytes.AddRange(BitConverter.GetBytes(v)); }
        internal void U64(ulong v) { bytes.AddRange(BitConverter.GetBytes(v)); }
        internal void Rip(int target) { U32(unchecked((uint)(target - (Rva + 4)))); }
        internal void Jump(string label) { Emit(0xe9); Label(label); }
        internal void Branch(byte condition, string label) { Emit(0x0f, condition); Label(label); }
        internal void Call(string label) { Emit(0xe8); Label(label); }
        internal void CallImport(int target) { Emit(0xff, 0x15); Rip(target); }
        internal void LeaRax(int target) { Emit(0x48, 0x8d, 0x05); Rip(target); }
        internal void LoadBase(int slot) { Emit(0x4c, 0x8b, 0x1d); Rip(slot); } // r11 = game base
        internal void GameCall(uint offset, int slot) { LoadBase(slot); Emit(0x49, 0x8d, 0x83); U32(offset); Emit(0xff, 0xd0); }
        internal void GameJump(uint offset, int slot) { LoadBase(slot); Emit(0x49, 0x8d, 0x83); U32(offset); Emit(0xff, 0xe0); }
        private void Label(string name) { fixups.Add(Tuple.Create(bytes.Count, name)); U32(0); }
        internal byte[] Finish()
        {
            byte[] result = bytes.ToArray(); foreach (var f in fixups) { if (!labels.ContainsKey(f.Item2)) throw new InvalidOperationException("Unknown code label: " + f.Item2); Buffer.BlockCopy(BitConverter.GetBytes(labels[f.Item2] - (start + f.Item1 + 4)), 0, result, f.Item1, 4); } return result;
        }
    }
}
