using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using SO4KoreanPatcher;

internal static class XboxTests
{
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    static void Reject(Action action, string name) { bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; } Check(failed, name); }
    static T Field<T>(MainForm form, string name) { return (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }
    static object Isos(params string[] paths) { return typeof(MainForm).GetMethod("GetIsos", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { new DataObject(DataFormats.FileDrop, paths) }); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string project = args[0], work = Path.Combine(project, "obj", "xbox-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            string[] paths = Enumerable.Range(1, 3).Select(n => Path.Combine(work, "disc" + n + ".iso")).ToArray();
            foreach (string p in paths) File.WriteAllBytes(p, new byte[] { 0 });
            Check(XboxPatchEngine.OutputPath(paths[0]) == Path.Combine(work, "disc1_repacked.iso"), "Output is beside original with repacked suffix");
            Check(Isos(paths) != null, "Accept three ISOs"); Check(Isos(paths[0]) == null, "Reject one ISO");
            Check(Isos(paths[0], paths[0], paths[2]) == null, "Reject duplicate paths");
            Check(Isos(paths[0], paths[1], work) == null, "Reject folder in ISO input");
            using (var form = new MainForm())
            {
                var pc = Field<RadioButton>(form, "pc"); var xbox = Field<RadioButton>(form, "xbox"); var drop = Field<FolderDropBox>(form, "drop"); var button = Field<Button>(form, "patch");
                Check(!Field<Button>(form, "restore").Enabled, "Restore disabled without PC folder");
                Check(pc.Checked && !xbox.Checked && !button.Enabled, "PC is initial mode"); xbox.Checked = true;
                Check(!Field<Button>(form, "restore").Enabled && !Field<Button>(form, "restore").Visible, "Xbox has no restore action");
                Check(!pc.Checked && drop.MultipleFiles && drop.Prompt.Contains("ISO 3개"), "Xbox mode changes prompt");
                typeof(Control).GetMethod("OnDragDrop", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(drop, new object[] { new DragEventArgs(new DataObject(DataFormats.FileDrop, paths.Reverse().ToArray()), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy) });
                Check(button.Enabled && Field<string[]>(form, "isoPaths").Length == 3, "Drop selects all three ISOs");
                pc.Checked = true; Check(Field<string[]>(form, "isoPaths") == null && drop.FolderPath == "" && !button.Enabled && !drop.MultipleFiles, "Mode switch clears incompatible input");
                form.PerformLayout(); var layout = (TableLayoutPanel)form.Controls[0];
                Check(layout.GetColumn(pc.Parent) == 0 && layout.GetColumn(drop) == 1, "Platform radios are left of drop box");
            }
            Reject(() => XboxPatchEngine.SafePath(work, "../escape"), "Reject traversal");
            Reject(() => XboxPatchEngine.SafePath(work, "C:\\escape"), "Reject absolute path");
            var plan = Storage.Json<XboxManifest>(File.ReadAllText(Path.Combine(project, "Assets/Xbox360/manifest.json")));
            XboxPatchEngine.ValidateManifest(plan); checks++;
            plan.discs[2].disc = 1; Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject repeated disc in manifest"); plan.discs[2].disc = 3;
            var target = new XboxFile { path = "sample.bin", target_size = 3, target_sha256 = Storage.Hash(new byte[] { 1, 2, 3 }) };
            byte[] fixture = new byte[0x12000]; Array.Copy(System.Text.Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA"), 0, fixture, 0x10000, 20);
            Array.Copy(BitConverter.GetBytes((uint)33), 0, fixture, 0x10014, 4); Array.Copy(BitConverter.GetBytes((uint)2048), 0, fixture, 0x10018, 4);
            Array.Copy(BitConverter.GetBytes((uint)34), 0, fixture, 33 * 2048 + 4, 4); Array.Copy(BitConverter.GetBytes((uint)3), 0, fixture, 33 * 2048 + 8, 4);
            fixture[33 * 2048 + 13] = 10; Array.Copy(System.Text.Encoding.ASCII.GetBytes("sample.bin"), 0, fixture, 33 * 2048 + 14, 10); Array.Copy(new byte[] { 1, 2, 3 }, 0, fixture, 34 * 2048, 3);
            string iso = Path.Combine(work, "fixture.iso"); File.WriteAllBytes(iso, fixture); XboxPatchEngine.VerifyIso(iso, new[] { target }); checks++;
            fixture[34 * 2048] ^= 1; File.WriteAllBytes(iso, fixture); Reject(() => XboxPatchEngine.VerifyIso(iso, new[] { target }), "Detect ISO content corruption");
            fixture[33 * 2048 + 2] = 1; File.WriteAllBytes(iso, fixture); Reject(() => XboxPatchEngine.VerifyIso(iso, new[] { target }), "Reject broken directory tree");
            if (args.Length > 1)
                foreach (var disc in plan.discs)
                {
                    XboxPatchEngine.VerifyIso(Path.Combine(args[1], "Star Ocean - The Last Hope (Japan) (Disc " + disc.disc.ToString("00") + ")_repacked.iso"), disc.files); checks++;
                    Console.WriteLine("Disc " + disc.disc + " ISO matches patcher target hashes");
                }
            Storage.AtomicJson(Path.Combine(project, "obj/xbox-checks.json"), new { passed = true, assertions = checks, gameLaunched = false });
            Directory.Delete(work, true);
            Console.WriteLine("Passed " + checks + " checks; no game execution"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
