using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using SO4KoreanPatcher;

internal static class SteamCheatTests
{
    static string project, work, data, dataHash, baseFile;
    static int checks;
    static Manifest plan;
    static byte[] zero, one;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Reject(Action a, string why)
    { bool rejected = false; try { a(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, why); }
    static void Add(ZipArchive zip, string name, byte[] bytes)
    { using (var s = zip.CreateEntry(name).Open()) s.Write(bytes, 0, bytes.Length); }
    static void Main(string[] args)
    {
        try
        {
            project = Path.GetFullPath(args[0]); work = Path.GetFullPath(args[1]); Directory.CreateDirectory(work);
            baseFile = Path.Combine(Path.GetDirectoryName(project), "work/steam_movement_2x_20261004/StarOceanTheLastHope.original.exe");
            bool matrix = args.Length < 3 || args[2] != "transactions";
            MakePackage(); if (matrix) Matrix(); Transactions();
            Storage.AtomicJson(Path.Combine(work, "steam-cheat-tests.json"), new { passed = true, assertions = checks, decodedTransitions = matrix ? 64 : 0, installedCombinations = 8, failureRollback = true, interruptedExeRecovery = true, oldJournalUpgrade = true, defaultClearsCheats = true, restorationMetadataCleanup = true, gameExecuted = false });
            Console.WriteLine("PASS " + checks + " Steam cheat assertions");
        }
        catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
    }
    static void MakePackage()
    {
        zero = Enumerable.Range(0, 8192).Select(i => (byte)(i * 37)).ToArray();
        one = Enumerable.Range(0, 4096).Select(i => (byte)(i * 61)).ToArray();
        var ops = new List<Operation>(); var deltas = new List<byte[]>();
        string encoder = Path.Combine(Path.GetDirectoryName(project), "tools/xdelta3-3.2.1/xdelta3-3.2.1-windows-x86_64/xdelta3.exe");
        for (int i = 0; i < 3; i++)
        {
            string file = i == 2 ? "0001.bin" : "0000.bin"; int at = i == 2 ? 64 : 4096;
            byte[] source = (i == 2 ? one : zero).Skip(at).Take(512).ToArray(), target = (byte[])source.Clone();
            for (int n = 20; n < 70; n++) target[n] ^= 0x35;
            string a = Path.Combine(work, "source"), b = Path.Combine(work, "target"), d = Path.Combine(work, "delta");
            File.WriteAllBytes(a, source); File.WriteAllBytes(b, target);
            DeltaTool.Run(encoder, "-e -f -D -a -A -S none -s " + DeltaTool.Q(a) + " " + DeltaTool.Q(b) + " " + DeltaTool.Q(d));
            var delta = File.ReadAllBytes(d); deltas.Add(delta);
            ops.Add(new Operation { label = "fixture-" + i, sourceFile = file, sourceOffset = at, sourceLength = source.Length, sourceHash = Storage.Hash(source), targetFile = file, targetOffset = i == 1 ? 8192 : at, targetLength = target.Length, targetHash = Storage.Hash(target), stageFile = "deltas/" + i.ToString("D4") + ".xdelta", deltaHash = Storage.Hash(delta) });
        }
        var catalog = Storage.Json<SteamCheatCatalog>(File.ReadAllText(Path.Combine(project, "Assets/SteamCheats.json")));
        var native = File.ReadAllBytes(Path.Combine(project, "Assets/wininet.dll"));
        plan = new Manifest { schema = 3, format = "xdelta-ranges-v1", buildId = new string('c', 24), exeHash = catalog.baseHash, nativeHash = Storage.Hash(native), steamCheats = catalog,
            files = new[] { new FilePlan { name = "0000.bin", sourceLength = zero.Length, targetLength = zero.Length + 512 }, new FilePlan { name = "0001.bin", sourceLength = one.Length, targetLength = one.Length } }, operations = ops.ToArray() };
        data = Path.Combine(work, "fixture.data");
        using (var zip = ZipFile.Open(data, ZipArchiveMode.Create))
        {
            Add(zip, "manifest.json", Storage.Utf8.GetBytes(Storage.Json(plan))); Add(zip, "wininet.dll", native);
            for (int i = 0; i < 3; i++) Add(zip, ops[i].stageFile, deltas[i]);
            foreach (var v in catalog.variants.Where(v => v.mask != 0))
                foreach (string name in new[] { v.forwardFile, v.reverseFile }) Add(zip, name, File.ReadAllBytes(Path.Combine(project, "Assets", name)));
        }
        dataHash = Storage.HashFile(data);
    }
    static void Matrix()
    {
        var rows = new List<object>();
        using (var package = new DeltaPackage(data, dataHash))
        {
            for (int from = 0; from < 8; from++)
            {
                var input = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(project), "work/steam_cheat_options_20261004/variant-" + from + ".exe"));
                for (int to = 0; to < 8; to++)
                {
                    var result = SteamCheats.Prepare(package, input, (SteamCheatOptions)to);
                    string targetHash = plan.steamCheats.variants.Single(v => v.mask == to).hash;
                    Check(Storage.Hash(result.Target) == targetHash, "Native encoder / C# decoder transition " + from + " -> " + to);
                    Check(Storage.Hash(result.Original) == plan.exeHash, "Canonical original reconstructed");
                    rows.Add(new { from = from, to = to, targetHash = targetHash, passed = true });
                }
                Console.WriteLine("Verified all transitions from mask " + from);
                GC.Collect();
            }
        }
        Storage.AtomicJson(Path.Combine(work, "all-64-transitions.json"), rows);
    }
    static string Root(string name, int initialMask = 0)
    {
        string root = Path.Combine(work, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "0000.bin"), zero); File.WriteAllBytes(Path.Combine(root, "0001.bin"), one);
        File.Copy(Path.Combine(Path.GetDirectoryName(project), "work/steam_cheat_options_20261004/variant-" + initialMask + ".exe"), Path.Combine(root, SteamCheats.ExeName));
        return root;
    }
    static string JournalPath(string root) { return Path.Combine(root, PatchEngine.RecordName); }
    static Journal Record(string root) { return Storage.Json<Journal>(File.ReadAllText(JournalPath(root))); }
    static void Install(string root, int mask, Action fail = null)
    { new PatchEngine(null) { AfterExeWrite = fail }.Run(root, data, dataHash, false, null, (SteamCheatOptions)mask); }
    static void Expected(string root, int mask)
    {
        Check(Storage.HashFile(Path.Combine(root, SteamCheats.ExeName)) == plan.steamCheats.variants.Single(v => v.mask == mask).hash, "Installed EXE option mask " + mask);
        foreach (var o in plan.operations) using (var s = File.OpenRead(Path.Combine(root, o.targetFile))) Check(Storage.HashRange(s, o.targetOffset, o.targetLength, null) == o.targetHash, "Korean resource patch preserved");
        var j = Record(root); Check(j.schema == 4 && j.state == "installed" && j.cheatMask == mask, "Option journal committed");
        Check(Storage.HashFile(Path.Combine(root, j.backupFolder, j.exeBackup.entry)) == plan.exeHash, "Backup is stock Steam EXE");
        new PatchEngine(null).Run(root, data, dataHash, true, null, (SteamCheatOptions)mask);
    }
    static void Original(string root)
    {
        Check(File.ReadAllBytes(Path.Combine(root, "0000.bin")).SequenceEqual(zero) && File.ReadAllBytes(Path.Combine(root, "0001.bin")).SequenceEqual(one), "Original resource files restored");
        Check(Storage.HashFile(Path.Combine(root, SteamCheats.ExeName)) == plan.exeHash, "Original Steam EXE restored");
        Check(!File.Exists(Path.Combine(root, "wininet.dll")) && !File.Exists(Path.Combine(root, "packed.txt")), "Patch modules removed");
    }
    static void Transactions()
    {
        string root = Root("all-options");
        for (int mask = 0; mask < 8; mask++) { Install(root, mask); Expected(root, mask); Console.WriteLine("Installed and verified mask " + mask); }
        Install(root, 0); Expected(root, 0); Check(PatchEngine.CanRestore(root), "Restore includes no-cheat install");
        string originalBackup = Path.Combine(root, Record(root).backupFolder);
        new PatchEngine(null).RestoreOriginal(root); Original(root); Check(!PatchEngine.CanRestore(root), "Restored record disables button");
        Check(!File.Exists(JournalPath(root)) && !Directory.Exists(originalBackup), "Manual Steam restore removes EXE backup and journal");

        string cleanup = Root("old-restored-record"); Install(cleanup, 7); var cj = Record(cleanup); cj.state = "rolled-back"; Storage.AtomicJson(JournalPath(cleanup), cj);
        File.WriteAllBytes(Path.Combine(cleanup, "0000.bin"), zero); File.WriteAllBytes(Path.Combine(cleanup, "0001.bin"), one);
        File.Delete(Path.Combine(cleanup, "wininet.dll")); File.Delete(Path.Combine(cleanup, "packed.txt"));
        string originalExe = Path.Combine(Path.GetDirectoryName(project), "work/steam_cheat_options_20261004/variant-0.exe");
        string cleanupBackup = Path.Combine(cleanup, cj.backupFolder);
        Reject(() => new PatchEngine(null).RestoreOriginal(cleanup), "Remaining cheats prevent restored-artifact cleanup");
        Check(File.Exists(JournalPath(cleanup)) && Directory.Exists(cleanupBackup), "EXE verification failure preserves original backup and journal");
        File.Copy(originalExe, Path.Combine(cleanup, SteamCheats.ExeName), true); File.Delete(Path.Combine(cleanupBackup, cj.backups[0].entry));
        File.SetAttributes(Path.Combine(cleanup, SteamCheats.ExeName), FileAttributes.ReadOnly);
        Check(PatchEngine.CanRestore(cleanup), "Steam cleanup retry accepts partially removed backups");
        new PatchEngine(null).RestoreOriginal(cleanup); Original(cleanup);
        Check(!File.Exists(JournalPath(cleanup)) && !Directory.Exists(cleanupBackup), "Old restored Steam record and remaining backups are cleaned after verification");

        string existing = Root("existing-speed-recovery", 3); Install(existing, 4); Expected(existing, 4); Install(existing, 0); Expected(existing, 0);
        string legacy = Root("old-journal"); Install(legacy, 0); var old = Record(legacy); old.schema = 3; old.exeBackup = null; old.exeHash = null; old.cheatMask = 0; Storage.AtomicJson(JournalPath(legacy), old);
        File.Copy(Path.Combine(Path.GetDirectoryName(project), "work/steam_cheat_options_20261004/variant-3.exe"), Path.Combine(legacy, SteamCheats.ExeName), true);
        Install(legacy, 7); Expected(legacy, 7);

        string failure = Root("failure", 3); Reject(() => Install(failure, 7, () => { throw new IOException("injected after EXE write"); }), "EXE transaction fault propagated"); Original(failure); Install(failure, 2); Expected(failure, 2);
        string interrupted = Root("interrupted"); Install(interrupted, 7); var interruptedRecord = Record(interrupted); interruptedRecord.state = "installing"; Storage.AtomicJson(JournalPath(interrupted), interruptedRecord);
        File.WriteAllBytes(Path.Combine(interrupted, SteamCheats.ExeName), new byte[] { 9, 8, 7 }); Install(interrupted, 1); Expected(interrupted, 1);

        string broken = Root("broken-backup"); Install(broken, 2); var bj = Record(broken); string backup = Path.Combine(broken, bj.backupFolder, bj.exeBackup.entry);
        using (var s = File.OpenWrite(backup)) { s.WriteByte(0); }
        string exeBefore = Storage.HashFile(Path.Combine(broken, SteamCheats.ExeName)), journalBefore = Storage.HashFile(JournalPath(broken));
        Reject(() => Install(broken, 4), "Corrupt EXE backup blocks repatch"); Check(Storage.HashFile(Path.Combine(broken, SteamCheats.ExeName)) == exeBefore && Storage.HashFile(JournalPath(broken)) == journalBefore, "Corrupt backup rejected before mutations");
        Reject(() => new PatchEngine(null).RestoreOriginal(broken), "Corrupt EXE backup blocks restore");

        string foreign = Root("foreign"); using (var s = File.OpenWrite(Path.Combine(foreign, SteamCheats.ExeName))) { s.Position = 1000; s.WriteByte(0x73); }
        string foreignBefore = Storage.HashFile(Path.Combine(foreign, SteamCheats.ExeName)); Reject(() => Install(foreign, 7), "Unknown modified EXE rejected");
        Check(Storage.HashFile(Path.Combine(foreign, SteamCheats.ExeName)) == foreignBefore && !File.Exists(JournalPath(foreign)), "Unknown EXE remains untouched");
        Reject(() => Install(foreign, 8), "Unsupported option bits rejected");

        string verify = Root("read-only-verify", 3); new PatchEngine(null).Run(verify, data, dataHash, true, work, (SteamCheatOptions)7);
        Check(Storage.HashFile(Path.Combine(verify, SteamCheats.ExeName)) == plan.steamCheats.variants.Single(v => v.mask == 3).hash && !File.Exists(JournalPath(verify)), "Verify prepares choices without writing the EXE");
    }
}
