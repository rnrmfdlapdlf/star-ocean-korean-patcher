using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
namespace SO4KoreanPatcher
{
    internal sealed class DeltaPackage : IDisposable
    {
        private readonly ZipArchive zip;
        internal readonly Manifest Plan;
        internal DeltaPackage(string path, string hash)
        {
            Storage.Require(Storage.HashFile(path) == hash, "패치 데이터가 실행 파일과 일치하지 않습니다.");
            zip = ZipFile.OpenRead(path);
            try
            {
                Plan = Storage.Json<Manifest>(Storage.Utf8.GetString(Read("manifest.json", 4 * 1024 * 1024)));
                Storage.Require(Plan.schema == 3 && Plan.format == "xdelta-ranges-v1", "패치 형식이 올바르지 않습니다.");
                PatchEngine.Validate(Plan.files, Plan.operations);
                Storage.Require(Regex.IsMatch(Plan.buildId ?? "", "^[a-f0-9]{24}$") && Regex.IsMatch(Plan.exeHash ?? "", "^[a-f0-9]{64}$"), "빌드 정보가 잘못되었습니다.");
                var names = new HashSet<string>(new[] { "manifest.json", "wininet.dll" });
                if (Plan.steamCheats != null)
                {
                    SteamCheats.Validate(Plan.steamCheats, Plan.exeHash);
                    foreach (var v in Plan.steamCheats.variants.Where(v => v.mask != 0)) { names.Add(v.forwardFile); names.Add(v.reverseFile); }
                }
                for (int i = 0; i < Plan.operations.Length; i++)
                {
                    var op = Plan.operations[i];
                    Storage.Require(op.stageFile == "deltas/" + i.ToString("D4") + ".xdelta" && Regex.IsMatch(op.deltaHash ?? "", "^[a-f0-9]{64}$"), "차분 경로가 잘못되었습니다.");
                    names.Add(op.stageFile);
                }
                Storage.Require(zip.Entries.Count == names.Count && zip.Entries.All(e => names.Remove(e.FullName)) && names.Count == 0, "패치 데이터 항목이 잘못되었습니다.");
                Storage.Require(Storage.Hash(Read("wininet.dll", 2 * 1024 * 1024)) == Plan.nativeHash, "실행 모듈이 손상되었습니다.");
            }
            catch { zip.Dispose(); throw; }
        }
        internal byte[] Read(string name, int limit)
        {
            var entry = zip.GetEntry(name); Storage.Require(entry != null && entry.Length > 0 && entry.Length <= limit, "패치 데이터 크기가 잘못되었습니다: " + name);
            using (var stream = entry.Open()) { var bytes = new byte[(int)entry.Length]; Storage.ReadExactly(stream, bytes, bytes.Length); Storage.Require(stream.ReadByte() == -1, "패치 데이터 길이가 다릅니다."); return bytes; }
        }
        public void Dispose() { zip.Dispose(); }
    }
    public sealed class PatchEngine
    {
        public const string RecordName = "SO4KoreanPatch.install.json";
        private readonly MonotonicProgress progress;
        public Action<int> AfterWrite;
        public Action AfterExeWrite;
        private static readonly byte[] Packed = Storage.Utf8.GetBytes("0000.bin\r\n0001.bin\r\nwininet.dll\r\n");
        public PatchEngine(Action<int> report) { progress = new MonotonicProgress(report); }
        public static bool IsGameFolder(string root) { return Directory.Exists(root) && new[] { "0000.bin", "0001.bin", "StarOceanTheLastHope.exe" }.All(n => File.Exists(Path.Combine(root, n))); }
        private static void Idle() { foreach (var p in Process.GetProcessesByName("StarOceanTheLastHope")) { p.Dispose(); throw new IOException("게임을 종료한 뒤 다시 실행해 주세요."); } }
        internal static void Validate(FilePlan[] files, Operation[] operations)
        {
            Storage.Require(files != null && files.Length == 2 && files.All(f => f != null) && files.Select(f => f.name).OrderBy(x => x).SequenceEqual(new[] { "0000.bin", "0001.bin" }) && operations != null && operations.Length > 0 && operations.Length <= 20000 && operations.All(o => o != null), "적용 구간 목록이 잘못되었습니다.");
            foreach (var f in files)
            {
                Storage.Require(f.sourceLength >= 0 && f.targetLength >= f.sourceLength && f.targetLength < 256L * 1024 * 1024 * 1024, "게임 파일 크기가 잘못되었습니다.");
                long end = 0, append = f.sourceLength;
                foreach (var o in operations.Where(o => o.targetFile == f.name).OrderBy(o => o.targetOffset))
                {
                    Storage.Require(o.targetOffset >= end && o.targetLength > 0 && o.targetLength <= 64L * 1024 * 1024 && o.targetOffset <= f.targetLength - o.targetLength && Regex.IsMatch(o.targetHash ?? "", "^[a-f0-9]{64}$"), "적용 구간이 겹치거나 범위를 벗어났습니다.");
                    end = o.targetOffset + o.targetLength;
                    Storage.Require(o.targetOffset >= f.sourceLength || end <= f.sourceLength, "원본과 추가 구간의 경계가 잘못되었습니다.");
                    if (o.targetOffset >= f.sourceLength) { Storage.Require(o.targetOffset == append, "추가 구간이 연속적이지 않습니다."); append = end; }
                    var source = files.SingleOrDefault(x => x.name == o.sourceFile);
                    Storage.Require(source != null && o.sourceOffset >= 0 && o.sourceLength >= 0 && o.sourceLength <= 64L * 1024 * 1024 && o.sourceOffset <= (o.sourceLength == 0 ? source.targetLength : source.sourceLength - o.sourceLength), "원본 참조 범위가 잘못되었습니다.");
                    if (o.sourceLength > 0) Storage.Require(Regex.IsMatch(o.sourceHash ?? "", "^[a-f0-9]{64}$"), "원본 구간 해시가 잘못되었습니다.");
                }
                Storage.Require(append == f.targetLength, "추가 구간 크기가 다릅니다.");
            }
            Storage.Require(operations.All(o => files.Any(f => f.name == o.targetFile)), "허용하지 않은 대상 파일입니다.");
        }
        private static string BackupRoot(string root, Journal journal)
        {
            Storage.Require(Regex.IsMatch(journal.backupFolder ?? "", "^SO4KoreanPatch\\.backup\\.[a-f0-9]{32}$"), "복구 폴더 이름이 잘못되었습니다.");
            return Path.Combine(root, journal.backupFolder);
        }
        private static void CleanupObsoleteBackups(string root, Journal current)
        {
            // Only the latest verified backup is needed for restoration. Older releases
            // did not write ownership markers, so require their exact folder/file format.
            try
            {
                ValidateBackups(root, current, true);
                string parent = Path.GetFullPath(root);
                if (parent.Length > Path.GetPathRoot(parent).Length) parent = parent.TrimEnd('\\', '/');
                string keep = Path.GetFullPath(BackupRoot(root, current));
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return;
                foreach (string candidate in Directory.GetDirectories(parent, "SO4KoreanPatch.backup.*", SearchOption.TopDirectoryOnly))
                {
                    string folder = Path.GetFullPath(candidate);
                    if (string.Equals(folder, keep, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetDirectoryName(folder), parent, StringComparison.OrdinalIgnoreCase) ||
                        !Regex.IsMatch(Path.GetFileName(folder), @"\ASO4KoreanPatch\.backup\.[a-f0-9]{32}\z")) continue;
                    try
                    {
                        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                        string[] entries = Directory.GetFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly);
                        if (entries.Any(entry => !IsBackupFile(folder, entry))) continue;
                        foreach (string entry in entries)
                        {
                            if (!IsBackupFile(folder, entry)) throw new IOException("복구 폴더 내용이 변경되었습니다.");
                            File.Delete(entry);
                        }
                        // Never recursively delete an old folder or follow a junction.
                        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) Directory.Delete(folder);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private static bool IsBackupFile(string folder, string entry)
        {
            string full = Path.GetFullPath(entry), name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase) ||
                (name != SteamCheats.BackupName && !Regex.IsMatch(name, @"\A[0-9]{4,5}\.(bin|bak)\z"))) return false;
            return (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        private static bool MatchesOriginal(Manifest plan, Dictionary<string, FileStream> files)
        {
            if (plan.files.Any(f => files[f.name].Length != f.sourceLength)) return false;
            foreach (var o in plan.operations)
                if (Storage.HashRange(files[o.sourceFile], o.sourceOffset, o.sourceLength, null) != o.sourceHash) return false;
            return true;
        }
        private static void VerifyRecordedLeftovers(string root, Journal previous)
        {
            foreach (string name in new[] { "wininet.dll", "packed.txt" })
            {
                string path = Path.Combine(root, name);
                if (!File.Exists(path)) continue;
                string expected = name == "wininet.dll" ? previous.nativeHash : previous.packedHash;
                Storage.Require(Storage.HashFile(path) == expected, "원본 데이터는 확인했지만 기존 기록과 다른 파일이 남아 있습니다: " + name);
            }
        }
        private static void VerifyInstalled(string root, Journal j, Dictionary<string, FileStream> files)
        {
            if (j.schema == 4) SteamCheats.VerifyInstalled(j, files[SteamCheats.ExeName]);
            foreach (var f in j.files) Storage.Require(files[f.name].Length == f.targetLength, "설치 후 게임 파일 크기가 변경되었습니다.");
            foreach (var o in j.operations) Storage.Require(Storage.HashRange(files[o.targetFile], o.targetOffset, o.targetLength, null) == o.targetHash, "설치된 패치 구간이 변경되었습니다: " + o.label);
            Storage.Require(File.Exists(Path.Combine(root, "wininet.dll")) && Storage.HashFile(Path.Combine(root, "wininet.dll")) == j.nativeHash && File.Exists(Path.Combine(root, "packed.txt")) && Storage.HashFile(Path.Combine(root, "packed.txt")) == j.packedHash, "설치된 패치 모듈이 변경되었습니다.");
        }
        private static void ValidateBackups(string root, Journal j, bool hashes, bool filesRequired = true)
        {
            string backup = BackupRoot(root, j);
            if (j.schema == 4) SteamCheats.ValidateBackup(root, j, hashes, filesRequired);
            else Storage.Require(j.exeBackup == null && j.exeHash == null && j.cheatMask == 0, "기존 형식의 실행 파일 복구 기록이 잘못되었습니다.");
            var needed = j.operations.Where(o => o.targetOffset < j.files.Single(f => f.name == o.targetFile).sourceLength).ToArray();
            Storage.Require(j.backups != null && j.backups.Count == needed.Length, "원본 복구 기록이 불완전합니다.");
            var names = new HashSet<string>(); var restored = new HashSet<Operation>();
            foreach (var b in j.backups)
            {
                Storage.Require(b != null && Regex.IsMatch(b.entry ?? "", "^[0-9]{4,5}\\.(bin|bak)$") && names.Add(b.entry) && needed.Count(o => o.targetFile == b.file && o.targetOffset == b.offset && o.targetLength == b.length) == 1, "원본 복구 구간이 잘못되었습니다.");
                Storage.Require(restored.Add(needed.Single(o => o.targetFile == b.file && o.targetOffset == b.offset && o.targetLength == b.length)), "원본 복구 구간이 중복되었습니다.");
                Storage.Require(Regex.IsMatch(b.hash ?? "", "^[a-f0-9]{64}$") && (!filesRequired || (File.Exists(Path.Combine(backup, b.entry)) && new FileInfo(Path.Combine(backup, b.entry)).Length == b.length && (!hashes || Storage.HashFile(Path.Combine(backup, b.entry)) == b.hash))), "원본 복구 데이터가 손상되었습니다.");
            }
        }
        private static Journal RecoveryJournal(string root, bool allowRestored = false)
        {
            var j = Storage.Json<Journal>(File.ReadAllText(Path.Combine(root, RecordName)));
            Storage.Require(j != null && (j.schema == 2 || j.schema == 3 || j.schema == 4) && (j.state == "installed" || j.state == "installing" || (allowRestored && j.state == "rolled-back")), "복구할 패치 기록이 없습니다.");
            Validate(j.files, j.operations);
            return j;
        }
        public static bool CanRestore(string root)
        {
            if (!IsGameFolder(root)) return false;
            try { var j = RecoveryJournal(root, true); ValidateBackups(root, j, false, j.state != "rolled-back"); return true; }
            catch (InvalidDataException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        public void RestoreOriginal(string root)
        {
            root = Path.GetFullPath(root);
            Storage.Require(IsGameFolder(root), "게임 설치 폴더를 확인해 주세요."); Idle();
            var j = RecoveryJournal(root, true);
            var files = new Dictionary<string, FileStream>();
            try
            {
                var access = j.state == "rolled-back" ? FileAccess.Read : FileAccess.ReadWrite;
                foreach (var f in j.files) files.Add(f.name, new FileStream(Path.Combine(root, f.name), FileMode.Open, access, FileShare.None));
                if (j.schema == 4) files.Add(SteamCheats.ExeName, new FileStream(Path.Combine(root, SteamCheats.ExeName), FileMode.Open, access, FileShare.None));
                if (j.state == "rolled-back")
                {
                    VerifyRestored(root, j, files); progress.Set(5000); Idle();
                    CleanupRestored(root, j); progress.Set(10000); return;
                }
                ValidateBackups(root, j, true); progress.Set(2500);
                if (j.state == "installed") VerifyInstalled(root, j, files);
                else
                {
                    VerifyRecordedLeftovers(root, j);
                    foreach (var f in j.files) Storage.Require(files[f.name].Length >= f.sourceLength && files[f.name].Length <= f.targetLength, "복구할 게임 파일 크기가 기록과 다릅니다.");
                }
                progress.Set(5000); Idle();
                j.state = "installing"; Storage.AtomicJson(Path.Combine(root, RecordName), j);
                Restore(root, j, files); VerifyRestored(root, j, files);
                CleanupRestored(root, j); progress.Set(10000);
            }
            finally { foreach (var f in files.Values) f.Dispose(); }
        }
        private static void VerifyRestored(string root, Journal j, Dictionary<string, FileStream> files)
        {
            // Cleanup may have been interrupted after some backup files were deleted.
            // Verify the restored game against the recorded original hashes directly.
            ValidateBackups(root, j, false, false);
            foreach (var f in j.files) Storage.Require(files[f.name].Length == f.sourceLength, "원본 복구 후 게임 파일 크기가 다릅니다.");
            foreach (var b in j.backups) Storage.Require(Storage.HashRange(files[b.file], b.offset, b.length, null) == b.hash, "원본 복구 후 데이터 검증에 실패했습니다.");
            if (j.schema == 4)
            {
                var exe = files[SteamCheats.ExeName]; var b = j.exeBackup;
                Storage.Require(exe.Length == b.length && Storage.HashRange(exe, 0, exe.Length, null) == b.hash, "Steam 실행 파일 원본 복구 검증에 실패했습니다.");
            }
            Storage.Require(!File.Exists(Path.Combine(root, "wininet.dll")) && !File.Exists(Path.Combine(root, "packed.txt")), "원본 복구 후 패치 모듈이 남아 있습니다.");
        }
        private static void CleanupRestored(string root, Journal j)
        {
            Storage.Require(j.state == "rolled-back", "검증된 원본 복구 후에만 복구 자료를 정리할 수 있습니다.");
            string parent = Path.GetFullPath(root);
            if (parent.Length > Path.GetPathRoot(parent).Length) parent = parent.TrimEnd('\\', '/');
            string folder = Path.GetFullPath(BackupRoot(parent, j));
            Storage.Require(string.Equals(Path.GetDirectoryName(folder), parent, StringComparison.OrdinalIgnoreCase), "복구 자료 경로가 게임 폴더를 벗어났습니다.");
            try
            {
                if (Directory.Exists(folder) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0 && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                {
                    var names = new HashSet<string>(j.backups.Select(b => b.entry), StringComparer.Ordinal);
                    if (j.exeBackup != null) names.Add(j.exeBackup.entry);
                    string[] entries = Directory.GetFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                    // Preserve an entire folder if the user added files or subdirectories.
                    if (entries.All(entry => names.Contains(Path.GetFileName(entry)) && IsBackupFile(folder, entry)))
                    {
                        foreach (string entry in entries)
                        {
                            Storage.Require(names.Contains(Path.GetFileName(entry)) && IsBackupFile(folder, entry), "복구 자료 내용이 변경되었습니다.");
                            File.Delete(entry);
                        }
                        Storage.Require((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0, "복구 폴더가 연결 대상으로 변경되었습니다.");
                        Directory.Delete(folder);
                    }
                }
                // Delete the journal last so interrupted cleanup can be retried.
                File.Delete(Path.Combine(parent, RecordName));
            }
            catch (IOException e) { throw new IOException("원본 복구는 완료했지만 복구 자료를 정리하지 못했습니다. 파일 사용을 해제한 뒤 원본 복구를 다시 실행해 주세요.", e); }
            catch (UnauthorizedAccessException e) { throw new IOException("원본 복구는 완료했지만 복구 자료를 정리하지 못했습니다. 파일 사용을 해제한 뒤 원본 복구를 다시 실행해 주세요.", e); }
        }
        private static void Restore(string root, Journal j, Dictionary<string, FileStream> files)
        {
            ValidateBackups(root, j, true);
            string backup = BackupRoot(root, j);
            foreach (var b in j.backups) using (var input = File.OpenRead(Path.Combine(backup, b.entry))) { files[b.file].Position = b.offset; Storage.Copy(input, 0, files[b.file], b.length); }
            foreach (var f in j.files) { files[f.name].SetLength(f.sourceLength); files[f.name].Flush(true); }
            if (j.schema == 4)
            {
                var exe = files[SteamCheats.ExeName]; var b = j.exeBackup;
                using (var input = File.OpenRead(Path.Combine(backup, b.entry))) { exe.Position = 0; Storage.Copy(input, 0, exe, b.length); }
                exe.SetLength(b.length); exe.Flush(true);
                Storage.Require(Storage.HashRange(exe, 0, exe.Length, null) == b.hash, "Steam 실행 파일 원본 복구 검증에 실패했습니다.");
            }
            foreach (var b in j.backups) Storage.Require(Storage.HashRange(files[b.file], b.offset, b.length, null) == b.hash, "원본 복구 검증에 실패했습니다.");
            foreach (string n in new[] { "wininet.dll", "packed.txt" }) { string path = Path.Combine(root, n); if (File.Exists(path)) File.Delete(path); }
            j.state = "rolled-back"; Storage.AtomicJson(Path.Combine(root, RecordName), j);
        }
        public void Run(string root, string data, string hash, bool verifyOnly, string verificationDirectory, SteamCheatOptions options = SteamCheatOptions.None)
        {
            SteamCheats.ValidateOptions(options);
            root = Path.GetFullPath(root); Storage.Require(IsGameFolder(root), "게임 설치 폴더를 확인해 주세요."); Idle();
            string stage = Path.Combine(Path.GetTempPath(), "SO4KoreanPatcher-Xdelta-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            var files = new Dictionary<string, FileStream>(); Journal current = null; bool writing = false;
            try
            {
                using (var package = new DeltaPackage(data, hash))
                {
                    var plan = package.Plan;
                    foreach (var f in plan.files) files.Add(f.name, new FileStream(Path.Combine(root, f.name), FileMode.Open, verifyOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None));
                    string record = Path.Combine(root, RecordName); bool externallyRestored = false;
                    Journal previous = null;
                    if (File.Exists(record))
                    {
                        previous = Storage.Json<Journal>(File.ReadAllText(record));
                        Storage.Require(previous != null && (previous.schema == 2 || previous.schema == 3 || previous.schema == 4), "기존 패치 기록 형식을 지원하지 않습니다."); Validate(previous.files, previous.operations);
                        Storage.Require(new[] { "installed", "installing", "rolled-back" }.Contains(previous.state), "기존 패치 상태가 올바르지 않습니다.");
                    }
                    PreparedSteamExe prepared = null;
                    if (plan.steamCheats != null)
                    {
                        var exe = new FileStream(Path.Combine(root, SteamCheats.ExeName), FileMode.Open, verifyOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);
                        files.Add(SteamCheats.ExeName, exe);
                        if (previous != null && previous.schema == 4 && previous.state == "installing")
                        {
                            ValidateBackups(root, previous, true);
                            prepared = SteamCheats.FromOriginal(package, File.ReadAllBytes(Path.Combine(BackupRoot(root, previous), previous.exeBackup.entry)), options);
                        }
                        else
                        {
                            Storage.Require(exe.Length == plan.steamCheats.length, "지원하는 Steam 실행 파일과 크기가 다릅니다.");
                            prepared = SteamCheats.Prepare(package, DeltaTool.Read(exe, 0, exe.Length, SteamCheats.MaximumExeLength), options);
                        }
                    }
                    else
                    {
                        Storage.Require(options == SteamCheatOptions.None, "이 패치 데이터에는 Steam 치트 옵션이 없습니다.");
                        Storage.Require(Storage.HashFile(Path.Combine(root, SteamCheats.ExeName)) == plan.exeHash, "지원하는 게임 실행 파일과 다릅니다.");
                        if (previous != null && previous.schema == 4)
                            files.Add(SteamCheats.ExeName, new FileStream(Path.Combine(root, SteamCheats.ExeName), FileMode.Open, verifyOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None));
                    }
                    if (previous != null)
                    {
                        if (MatchesOriginal(plan, files))
                        {
                            VerifyRecordedLeftovers(root, previous); externallyRestored = true;
                        }
                        if (!externallyRestored && previous.state == "installed")
                        {
                            VerifyInstalled(root, previous, files);
                            if (verifyOnly) { Storage.Require(previous.buildId == plan.buildId && previous.cheatMask == (int)options, "설치된 패치와 확인할 빌드 또는 치트 옵션이 다릅니다."); progress.Set(10000); return; }
                        }
                        if (!externallyRestored && previous.state != "rolled-back")
                        {
                            Storage.Require(!verifyOnly, "중단된 설치의 원본 복구가 필요합니다.");
                            ValidateBackups(root, previous, true);
                            previous.state = "installing"; Storage.AtomicJson(record, previous); Restore(root, previous, files);
                        }
                    }
                    foreach (var f in plan.files) Storage.Require(files[f.name].Length == f.sourceLength, "원본 파일 크기가 다릅니다. Steam 무결성 검사로 복구한 뒤 다시 시도해 주세요.");
                    Storage.Require(externallyRestored || (!File.Exists(Path.Combine(root, "wininet.dll")) && !File.Exists(Path.Combine(root, "packed.txt"))), "복구 기록이 없는 패치 또는 모드 파일이 있습니다. README의 원본 복구 방법을 확인해 주세요.");
                    progress.Set(500);
                    for (int i = 0; i < plan.operations.Length; i++)
                    {
                        var o = plan.operations[i]; var bytes = DeltaTool.Read(files[o.sourceFile], o.sourceOffset, o.sourceLength);
                        Storage.Require(Storage.Hash(bytes) == o.sourceHash, "원본 데이터가 다릅니다: " + o.label);
                        var diff = package.Read(o.stageFile, 64 * 1024 * 1024); Storage.Require(Storage.Hash(diff) == o.deltaHash, "xdelta 데이터가 손상되었습니다.");
                        string output = Path.Combine(stage, i.ToString("D4") + ".new");
                        File.WriteAllBytes(output, Vcdiff.Decode(bytes, diff, checked((int)o.targetLength)));
                        Storage.Require(new FileInfo(output).Length == o.targetLength && Storage.HashFile(output) == o.targetHash, "정상 패치본과 차분 복원 결과가 다릅니다: " + o.label);
                        progress.Set(500 + (i + 1) * 6000 / plan.operations.Length);
                    }
                    if (verifyOnly)
                    {
                        if (verificationDirectory != null) Storage.AtomicJson(Path.Combine(verificationDirectory, "resource-verification.json"), new { passed = true, format = plan.format, reference = plan.baseline, operations = plan.operations.Length, changedBytes = plan.operations.Sum(o => o.targetLength), fullBinHashed = false, nativeHash = plan.nativeHash, cheatMask = (int)options, exeHash = prepared == null ? plan.exeHash : prepared.TargetHash });
                        progress.Set(10000); return;
                    }
                    Idle();
                    current = new Journal { schema = prepared == null ? 3 : 4, state = "installing", buildId = plan.buildId, files = plan.files, operations = plan.operations, nativeHash = plan.nativeHash, packedHash = Storage.Hash(Packed), backupFolder = "SO4KoreanPatch.backup." + Guid.NewGuid().ToString("N") };
                    string backup = BackupRoot(root, current); Directory.CreateDirectory(backup);
                    for (int i = 0; i < plan.operations.Length; i++)
                    {
                        var o = plan.operations[i]; long originalLength = plan.files.Single(f => f.name == o.targetFile).sourceLength;
                        if (o.targetOffset >= originalLength) continue;
                        byte[] bytes = DeltaTool.Read(files[o.targetFile], o.targetOffset, o.targetLength); string name = i.ToString("D4") + ".bin";
                        DeltaTool.WriteDurable(Path.Combine(backup, name), bytes); current.backups.Add(new Backup { file = o.targetFile, entry = name, hash = Storage.Hash(bytes), offset = o.targetOffset, length = o.targetLength });
                    }
                    if (prepared != null)
                    {
                        DeltaTool.WriteDurable(Path.Combine(backup, SteamCheats.BackupName), prepared.Original);
                        current.exeBackup = new Backup { file = SteamCheats.ExeName, entry = SteamCheats.BackupName, offset = 0, length = prepared.Original.Length, hash = plan.exeHash };
                        current.exeHash = prepared.TargetHash; current.cheatMask = prepared.Mask;
                    }
                    Storage.AtomicJson(record, current); writing = true; progress.Set(7300);
                    for (int i = 0; i < plan.operations.Length; i++)
                    {
                        var o = plan.operations[i]; byte[] bytes = File.ReadAllBytes(Path.Combine(stage, i.ToString("D4") + ".new")); Storage.Require(Storage.Hash(bytes) == o.targetHash, "적용 전 임시 데이터가 변경되었습니다.");
                        files[o.targetFile].Position = o.targetOffset; files[o.targetFile].Write(bytes, 0, bytes.Length); if (AfterWrite != null) AfterWrite(i); progress.Set(7300 + (i + 1) * 1900 / plan.operations.Length);
                    }
                    foreach (var f in plan.files) { files[f.name].SetLength(f.targetLength); files[f.name].Flush(true); }
                    if (prepared != null)
                    {
                        var exe = files[SteamCheats.ExeName]; exe.Position = 0; exe.Write(prepared.Target, 0, prepared.Target.Length); exe.SetLength(prepared.Target.Length); exe.Flush(true);
                        if (AfterExeWrite != null) AfterExeWrite(); progress.Set(9400);
                    }
                    DeltaTool.WriteDurable(Path.Combine(root, "wininet.dll"), package.Read("wininet.dll", 2 * 1024 * 1024)); DeltaTool.WriteDurable(Path.Combine(root, "packed.txt"), Packed);
                    VerifyInstalled(root, current, files); progress.Set(9800); current.state = "installed"; Storage.AtomicJson(record, current); writing = false;
                    CleanupObsoleteBackups(root, current); progress.Set(10000);
                }
            }
            catch
            {
                if (writing && current != null) Restore(root, current, files);
                throw;
            }
            finally
            {
                foreach (var f in files.Values) f.Dispose();
                // This directory is generated above, never a path supplied by the user or package.
                try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
