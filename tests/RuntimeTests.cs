using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SO4KoreanPatcher;

internal static class RuntimeTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("ntdll.dll")] private static extern IntPtr RtlLookupFunctionEntry(ulong pc, out ulong imageBase, IntPtr history);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr ModuleHandle(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Entry(IntPtr module, uint reason, IntPtr reserved);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Language();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Character(int id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Stored(IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte SetWide(IntPtr self, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate UIntPtr ConvertWide(IntPtr font, IntPtr name, IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate ushort Rename();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Memo(IntPtr self, IntPtr x, IntPtr y);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Guide(IntPtr self, int row, byte flag);
    private static readonly List<Delegate> callbacks = new List<Delegate>();
    private static readonly List<IntPtr> allocations = new List<IntPtr>();
    private static int language, guideCalls, memoCalls, assertions;
    private static IntPtr game, chosen;
    private static readonly IntPtr[] saves = new IntPtr[9];
    private static IntPtr Alloc(int size) { var p = Marshal.AllocHGlobal(size); Marshal.Copy(new byte[size], 0, p, size); allocations.Add(p); return p; }
    private static void Require(bool condition, string label) { assertions++; if (!condition) throw new Exception(label); }
    private static void Stub(int rva, Delegate callback)
    {
        callbacks.Add(callback); var b = new byte[14]; b[0] = 0xff; b[1] = 0x25; Buffer.BlockCopy(BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(callback).ToInt64()), 0, b, 6, 8); Marshal.Copy(b, 0, IntPtr.Add(game, rva), b.Length);
    }
    internal static void Run(string[] args)
    {
        IntPtr module = IntPtr.Zero;
        try
        {
            string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            var mapping = File.ReadLines(Path.Combine(root, "SO4KoreanPatcher/Assets/Translations.jsonl")).Select(Storage.Json<ResourceRecipe>).First(r => r.font != null && r.font.common).mapping;
            var executable = File.ReadAllBytes(Path.Combine(root, "StarOceanTheLastHope.exe")); var gamePe = new PeReader(executable);
            var built = RuntimeModule.Build(executable, mapping);
            string path = Path.Combine(output, "generated-runtime.dll"); File.WriteAllBytes(path, built.Bytes);
            module = LoadLibraryW(path); Require(module != IntPtr.Zero, "LoadLibrary: " + Marshal.GetLastWin32Error());
            IntPtr system = LoadLibraryW(Path.Combine(Environment.SystemDirectory, "wininet.dll"));
            Require(system != IntPtr.Zero && GetProcAddress(module, "InternetCloseHandle") == GetProcAddress(system, "InternetCloseHandle"), "System32 forwarding"); FreeLibrary(system);
            foreach (var fn in built.Functions) { ulong imageBase; Require(RtlLookupFunctionEntry((ulong)IntPtr.Add(module, fn.Value + 20).ToInt64(), out imageBase, IntPtr.Zero) != IntPtr.Zero && imageBase == (ulong)module.ToInt64(), "Unwind table: " + fn.Key); }
            game = VirtualAlloc(IntPtr.Zero, new UIntPtr(0x6000000), 0x3000, 0x40); Require(game != IntPtr.Zero, "Synthetic image allocation");
            Marshal.Copy(executable, 0, game, 4096);
            int[] signatures = { 0x77c105,0x6900a8,0x67f00e,0x67fb26,0x77c000,0x779660,0x75f5f0,0x76d510,0x754de0,0x4bede90,0x783de0 };
            foreach (int rva in signatures) { byte[] bytes = gamePe.At(rva, 16); Marshal.Copy(bytes, 0, IntPtr.Add(game, rva), bytes.Length); }
            int[] slots = {0x9dc430,0x9dc6a0,0xa64ed0,0xa65140,0xa82fa0,0xa90b40,0xaad6f0,0xa81628};
            foreach (int slot in slots) Marshal.WriteIntPtr(game, slot, IntPtr.Add(game, slot == 0xa81628 ? 0x4bede90 : 0x77c000));
            var pe = new PeReader(built.Bytes); int import = pe.Offset(BinaryData.I32(built.Bytes, pe.Optional + 120)), thunk = BinaryData.I32(built.Bytes, import + 16);
            var handle = new ModuleHandle(name => game); callbacks.Add(handle); Marshal.WriteIntPtr(module, thunk, Marshal.GetFunctionPointerForDelegate(handle));
            var entry = (Entry)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module, BinaryData.I32(built.Bytes, pe.Optional + 16)), typeof(Entry));
            Marshal.WriteByte(game, signatures[0], 0); Require(entry(module, 1, IntPtr.Zero) == 0, "Mismatched code rejected before hooks");
            Require(Marshal.ReadIntPtr(game, slots[0]) == IntPtr.Add(game, 0x77c000), "No partial hooks on signature failure");
            Marshal.WriteByte(game, signatures[0], gamePe.At(signatures[0], 1)[0]); Require(entry(module, 1, IntPtr.Zero) == 1, "All hooks installed");
            IntPtr bridge = IntPtr.Add(game, signatures[0] + 5 + Marshal.ReadInt32(game, signatures[0] + 1));
            Require(Marshal.ReadInt16(bridge) == 0x25ff && Marshal.ReadIntPtr(bridge, 6) == IntPtr.Add(module, built.Functions["convert"]), "Near bridge target");
            foreach (int slot in slots) Require(Marshal.ReadIntPtr(game, slot) == IntPtr.Add(module, built.Functions[slot == 0xa81628 ? "memo" : "set"]), "Vtable hook " + slot.ToString("X"));
            Require(entry(module, 2, IntPtr.Zero) == 1, "Non-attach notifications ignored");
            Marshal.WriteIntPtr(IntPtr.Add(module, built.BaseSlot), game); Marshal.WriteIntPtr(game, 0x1059008, new IntPtr(1)); Marshal.WriteIntPtr(game, 0x105e850, new IntPtr(1));
            for (int i = 0; i < 9; i++) { saves[i] = Alloc(32); Marshal.Copy(System.Text.Encoding.Unicode.GetBytes(i % 2 == 0 ? "@@\0" : "A\0"), 0, saves[i], i % 2 == 0 ? 6 : 4); }
            Stub(0x754de0, new Language(() => language)); Stub(0x76d510, new Character(id => saves[id - 1])); Stub(0x75f5f0, new Stored(value => value));
            Stub(0x77c000, new SetWide((self, name) => { chosen = name; return 1; }));
            Stub(0x779660, new ConvertWide((font, name, dst) => { int n = Marshal.PtrToStringUni(name).Length; Marshal.Copy(new byte[n * 40], 0, dst, n * 40); return new UIntPtr((uint)n); }));
            Stub(0x754bf0, new Rename(() => 32));
            Stub(0x4bede90, new Memo((self, x, y) => { memoCalls++; if ((Marshal.ReadInt32(self, 0x1d0) & 8) != 0) { Marshal.WriteInt32(x, BitConverter.ToInt32(BitConverter.GetBytes(569f), 0)); Marshal.WriteInt32(y, BitConverter.ToInt32(BitConverter.GetBytes(494f), 0)); } }));
            bool guideGood = false;
            Stub(0x67fba0, new Guide((self, row, flag) => { guideCalls++; var table = Marshal.ReadIntPtr(self, 0xf0); guideGood = row == 0 && Marshal.ReadInt16(table, 20) == 0 && Marshal.ReadInt16(table, 32) == 2; }));
            Func<string, Type, Delegate> function = (name, type) => Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module, built.Functions[name]), type);
            var set = (SetWide)function("set", typeof(SetWide)); var convert = (ConvertWide)function("convert", typeof(ConvertWide)); var rename = (Rename)function("rename", typeof(Rename)); var memo = (Memo)function("memo", typeof(Memo)); var guide = (Guide)function("guide", typeof(Guide));
            IntPtr metrics = Alloc(3072 * 24), fnt = Alloc(128), dstGlyphs = Alloc(40 * 20); Marshal.WriteIntPtr(fnt, 0x38, metrics); for (int i = 0; i < 3072; i++) Marshal.WriteInt32(metrics, i * 24, 67);
            for (int i = 0; i < 9; i++)
            {
                string original = Marshal.PtrToStringUni(saves[i]); Require(set(IntPtr.Zero, saves[i]) == 1, "Set return"); Require(Marshal.PtrToStringUni(chosen) == RuntimeModule.Names[i], "Name " + i);
                int count = (int)convert(fnt, chosen, dstGlyphs).ToUInt32(); Require(count == RuntimeModule.Names[i].Length, "Name count");
                for (int j = 0; j < count; j++) { int expected = mapping[RuntimeModule.Names[i][j].ToString()] + 1; Require(Marshal.ReadInt32(dstGlyphs, j * 40 + 28) == expected, "Glyph " + i + ":" + j); Require(Marshal.ReadIntPtr(dstGlyphs, j * 40 + 16) == IntPtr.Add(metrics, (expected - 1) * 24), "Metrics pointer"); }
                Require(Marshal.PtrToStringUni(saves[i]) == original, "Save unchanged");
            }
            Require(rename() == 0, "Japanese rename disabled"); language = 1; Require(rename() == 32, "Other language input preserved"); set(IntPtr.Zero, saves[0]); Require(chosen == saves[0], "Other language name preserved"); language = 0;
            IntPtr unrelated = Alloc(16); Marshal.Copy(System.Text.Encoding.Unicode.GetBytes("Other\0"), 0, unrelated, 12); set(IntPtr.Zero, unrelated); Require(chosen == unrelated, "Unrelated text preserved");
            IntPtr owner = Alloc(0x200), mesh = Alloc(0x240), xptr = Alloc(4), yptr = Alloc(4); Marshal.WriteIntPtr(owner, IntPtr.Add(game, 0xa64f88)); Marshal.WriteIntPtr(mesh, 8, owner); Marshal.WriteInt32(mesh, 0x1a4, 1);
            for (int i = 0; i < 6; i++) { Marshal.WriteIntPtr(owner, 0x48 + i * 8, mesh); Marshal.WriteInt32(mesh, 0x1d0, 0); memo(mesh, xptr, yptr); Require((Marshal.ReadInt32(mesh, 0x1d0) & 8) != 0, "All memo slots " + i); Require(Marshal.ReadInt32(xptr) == BitConverter.ToInt32(BitConverter.GetBytes(569f), 0), "Original layout used"); Marshal.WriteIntPtr(owner, 0x48 + i * 8, IntPtr.Zero); }
            Marshal.WriteInt32(mesh, 0x1d0, 0); memo(mesh, xptr, yptr); Require(Marshal.ReadInt32(mesh, 0x1d0) == 0, "Unrelated owner child untouched");
            IntPtr tableData = Alloc(20 + 21 * 112), obj = Alloc(0x130); Marshal.WriteIntPtr(obj, 0xf0, tableData); Marshal.WriteInt16(tableData, 20 + 20 * 112, 1); Marshal.WriteInt32(tableData, 20 + 20 * 112 + 8, 160003); Marshal.WriteInt16(tableData, 20 + 20 * 112 + 12, 2);
            guide(obj, 20, 1); Require(guideGood && guideCalls == 1, "Rename guide removed and row repacked"); Require(Marshal.ReadIntPtr(obj, 0xf0) == tableData && Marshal.ReadInt16(tableData, 20 + 20 * 112) == 1, "Guide table preserved");
            language = 1; guide(obj, 20, 0); Require(!guideGood && guideCalls == 2, "Other language guide preserved");
            VirtualFree(new IntPtr(bridge.ToInt64() & ~65535L), UIntPtr.Zero, 0x8000);
            Console.WriteLine("PASS runtime assertions=" + assertions + ", generated DLL bytes=" + built.Bytes.Length);
        }
        finally { if (module != IntPtr.Zero) FreeLibrary(module); if (game != IntPtr.Zero) VirtualFree(game, UIntPtr.Zero, 0x8000); foreach (IntPtr p in allocations) Marshal.FreeHGlobal(p); GC.KeepAlive(callbacks); }
    }
}
