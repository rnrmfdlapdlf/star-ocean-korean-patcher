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
                var options = new[] { Field<CheckBox>(form, "movement"), Field<CheckBox>(form, "recovery"), Field<CheckBox>(form, "saveAnywhere") };
                var selected = typeof(MainForm).GetMethod("SelectedCheats", BindingFlags.NonPublic | BindingFlags.Instance);
                Check(options.All(c => c.Enabled && !c.Checked) && (SteamCheatOptions)selected.Invoke(form, null) == SteamCheatOptions.None, "PC defaults to no cheats");
                Check(!Field<Button>(form, "restore").Enabled, "Restore disabled without PC folder");
                Check(pc.Checked && !xbox.Checked && !button.Enabled, "PC is initial mode"); xbox.Checked = true;
                Check(!Field<Button>(form, "restore").Enabled && !Field<Button>(form, "restore").Visible, "Xbox has no restore action");
                Check(!pc.Checked && drop.MultipleFiles && drop.Prompt.Contains("ISO 3개"), "Xbox mode changes prompt");
                Check(options.All(c => c.Enabled && !c.Checked), "Xbox enables all three options with no default selection");
                for (int mask = 0; mask < 8; mask++)
                {
                    for (int bit = 0; bit < 3; bit++) options[bit].Checked = (mask & (1 << bit)) != 0;
                    Check((int)(SteamCheatOptions)selected.Invoke(form, null) == mask, "Xbox UI captures combination " + mask);
                }
                typeof(MainForm).GetField("working", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, true);
                typeof(MainForm).GetMethod("RefreshCheats", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
                Check(options.All(c => !c.Enabled), "Options disabled during patch operation");
                typeof(MainForm).GetField("working", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, false);
                typeof(MainForm).GetMethod("RefreshCheats", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
                Check(options.All(c => c.Enabled), "Options reenabled after patch operation");
                typeof(Control).GetMethod("OnDragDrop", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(drop, new object[] { new DragEventArgs(new DataObject(DataFormats.FileDrop, paths.Reverse().ToArray()), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy) });
                Check(button.Enabled && Field<string[]>(form, "isoPaths").Length == 3, "Drop selects all three ISOs");
                pc.Checked = true; Check(Field<string[]>(form, "isoPaths") == null && drop.FolderPath == "" && !button.Enabled && !drop.MultipleFiles, "Mode switch clears incompatible input");
                Check(options.All(c => c.Enabled && c.Checked) && (int)(SteamCheatOptions)selected.Invoke(form, null) == 7, "Platform switch retains chosen options");
                form.PerformLayout(); var layout = (TableLayoutPanel)form.Controls[0];
                Check(layout.GetColumn(pc.Parent) == 0 && layout.GetColumn(drop) == 2, "Platform and options are left of drop box");
            }
            Reject(() => XboxPatchEngine.SafePath(work, "../escape"), "Reject traversal");
            Reject(() => XboxPatchEngine.SafePath(work, "C:\\escape"), "Reject absolute path");
            var plan = Storage.Json<XboxManifest>(File.ReadAllText(Path.Combine(project, "Assets/Xbox360/manifest.json")));
            XboxPatchEngine.ValidateManifest(plan); checks++;
            foreach (var disc in plan.discs)
            {
                var original = disc.files.Single(f => f.path == "default.xex");
                string baseline = original.target_sha256;
                for (int mask = 0; mask < 8; mask++)
                {
                    var files = XboxPatchEngine.SelectFiles(disc, (SteamCheatOptions)mask);
                    var executable = files.Single(f => f.path == "default.xex");
                    var variant = disc.cheats.Single(v => v.mask == mask);
                    Check(executable.target_sha256 == variant.target_sha256 && executable.target_size == variant.target_size && executable.delta == original.delta && executable.delta_sha256 == original.delta_sha256, "Disc " + disc.disc + " retains common base delta and selects result " + mask);
                    Check(variant.source_sha256 == baseline && variant.source_size == original.target_size, "Every additional delta uses the cheat-free Korean base");
                    Check(executable.sha256 == original.sha256 && executable.offset == original.offset && executable.size == original.size && files.Where(f => f.path != "default.xex").SequenceEqual(disc.files.Where(f => f.path != "default.xex")), "Variant preserves source identity and resource files");
                    Check(original.target_sha256 == baseline && disc.files.Single(f => f.path == "default.xex") == original, "Selection leaves baseline manifest intact");
                }
            }
            Reject(() => XboxPatchEngine.SelectFiles(plan.discs[0], (SteamCheatOptions)8), "Reject unknown cheat bit");
            Reject(() => new XboxPatchEngine(null, null).Run(paths, "missing-package", (SteamCheatOptions)(-1)), "Reject invalid options before opening ISO or writing output");
            var variants = plan.discs[0].cheats;
            variants[7].mask = 6; Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject duplicate or missing combination"); variants[7].mask = 7;
            string deltaPath = variants[1].delta; variants[1].delta = "../escape.xdelta";
            Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject cheat delta traversal"); variants[1].delta = deltaPath;
            string deltaHash = variants[1].delta_sha256; variants[1].delta_sha256 = "invalid";
            Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject malformed cheat delta hash"); variants[1].delta_sha256 = deltaHash;
            long targetSize = variants[1].target_size; variants[1].target_size = 0;
            Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject invalid variant size"); variants[1].target_size = targetSize;
            string baselineHash = variants[0].target_sha256; variants[0].target_sha256 = new string('f', 64);
            Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject mismatched no-cheat baseline"); variants[0].target_sha256 = baselineHash;
            string sourceHash = variants[1].source_sha256; variants[1].source_sha256 = new string('f', 64);
            Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject option delta based on a different executable"); variants[1].source_sha256 = sourceHash;
            long sourceSize = variants[1].source_size; variants[1].source_size++;
            Reject(() => XboxPatchEngine.ValidateManifest(plan), "Reject option delta source size mismatch"); variants[1].source_size = sourceSize;
            variants[0].delta = plan.discs[0].files.Single(f => f.path == "default.xex").delta;
            Reject(() => XboxPatchEngine.ValidateManifest(plan), "No-cheat mode has no additional delta"); variants[0].delta = null;
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
                    string basis = Path.Combine(args[1], "disc" + disc.disc, "variant-0.xex"), targetPath = Path.Combine(work, "option-test.xex");
                    foreach (var variant in disc.cheats.OrderBy(v => v.mask))
                    {
                        File.Copy(basis, targetPath, true);
                        XboxPatchEngine.ApplyCheat(targetPath, variant, Path.Combine(project, "Assets/Xbox360"), work);
                        Check(new FileInfo(targetPath).Length == variant.target_size && Storage.HashFile(targetPath) == variant.target_sha256, "Actual additional delta roundtrip Disc " + disc.disc + " mask " + variant.mask);
                        Check(!File.Exists(Path.Combine(work, "selected_default.xex")), "Verified option replaces the temporary base atomically");
                    }
                    File.Copy(basis, targetPath, true);
                    byte[] corrupt = File.ReadAllBytes(targetPath); corrupt[0] ^= 1; File.WriteAllBytes(targetPath, corrupt);
                    string corruptHash = Storage.Hash(corrupt);
                    Reject(() => XboxPatchEngine.ApplyCheat(targetPath, disc.cheats[1], Path.Combine(project, "Assets/Xbox360"), work), "Reject corrupt baseline before option decode");
                    Check(Storage.HashFile(targetPath) == corruptHash && !File.Exists(Path.Combine(work, "selected_default.xex")), "Corrupt baseline is not overwritten");
                    File.Copy(basis, targetPath, true);
                    var option = disc.cheats.Single(v => v.mask == 1);
                    string expectedDeltaHash = option.delta_sha256; option.delta_sha256 = new string('f', 64);
                    Reject(() => XboxPatchEngine.ApplyCheat(targetPath, option, Path.Combine(project, "Assets/Xbox360"), work), "Reject corrupt option delta before replacement"); option.delta_sha256 = expectedDeltaHash;
                    Check(Storage.HashFile(targetPath) == option.source_sha256, "Failed option preserves verified base");
                    Console.WriteLine("Disc " + disc.disc + " all 8 additional option combinations verified");
                }
            string workPrefix = Path.GetFullPath(Path.Combine(project, "obj")).TrimEnd('\\') + "\\";
            Check(Path.GetFullPath(work).StartsWith(workPrefix, StringComparison.OrdinalIgnoreCase), "Test cleanup stays inside project obj folder");
            Directory.Delete(work, true);
            Storage.AtomicJson(Path.Combine(project, "obj/xbox-checks.json"), new { passed = true, assertions = checks, gameLaunched = false });
            Console.WriteLine("Passed " + checks + " checks; no game execution"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
