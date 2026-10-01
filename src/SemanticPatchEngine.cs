using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace SO4KoreanPatcher
{
    internal sealed class SemanticPackage : IDisposable
    {
        private readonly FileStream file;
        private readonly ZipArchive zip;
        internal readonly SemanticManifest Plan;
        internal readonly Dictionary<string, ResourceRecipe> Recipes;
        internal readonly byte[] Font;
        internal SemanticPackage(string path, string expectedHash)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Storage.Require(file.Length <= 32 * 1024 * 1024 && Storage.HashRange(file, 0, file.Length, null) == expectedHash, "패치 데이터가 손상되었거나 실행 파일과 버전이 다릅니다.");
                file.Position = 0; zip = new ZipArchive(file, ZipArchiveMode.Read, true);
                Storage.Require(zip.Entries.Count == 3 && zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "NotoSansKR-Medium.ttf", "manifest.json", "translations.jsonl" }), "허용하지 않은 배포 데이터가 있습니다.");
                Plan = Storage.Json<SemanticManifest>(Storage.Utf8.GetString(Read("manifest.json", 2 * 1024 * 1024)));
                Recipes = Storage.Utf8.GetString(Read("translations.jsonl", 16 * 1024 * 1024)).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(Storage.Json<ResourceRecipe>).ToDictionary(r => r.key);
                Font = Read("NotoSansKR-Medium.ttf", 8 * 1024 * 1024);
                Storage.Require(Plan.schema == 2 && Plan.format == "translation-resources-v2" && Regex.IsMatch(Plan.version ?? "", "^v[0-9]{6}$") && Regex.IsMatch(Plan.buildId ?? "", "^[a-f0-9]{24}$") && Storage.Hash(Font) == Plan.fontHash, "패치 메타데이터가 올바르지 않습니다.");
                Storage.Require(Plan.sourceLengths != null && Plan.sourceLengths.Keys.OrderBy(n => n).SequenceEqual(new[] { "0000.bin", "0001.bin" }) && Plan.sourceLengths.Values.All(n => n >= OuterTable.Size), "원본 파일 목록이 잘못되었습니다.");
                Storage.Require(Recipes.Count > 0 && Recipes.Count < 10000 && Plan.locations != null && Plan.locations.Length > 0 && Plan.locations.Length < 10000, "리소스 수가 잘못되었습니다.");
                var locations = new HashSet<string>();
                foreach (var l in Plan.locations)
                {
                    Storage.Require(l.outerId >= 0 && l.outerId < OuterTable.Size / 12 && l.member >= 0 && Regex.IsMatch(l.package ?? "", "^[0-9]{4}\\.(pkg|pack)$") && Recipes.ContainsKey(l.recipe) && locations.Add(l.outerId + ":" + l.member), "리소스 위치가 잘못되었습니다.");
                }
                Storage.Require(Plan.locations.GroupBy(l => l.outerId).All(g => g.Select(l => l.package).Distinct().Count() == 1), "리소스 이름이 중복되었습니다.");
                foreach (var r in Recipes.Values)
                {
                    Storage.Require(Regex.IsMatch(r.key ?? "", "^[a-f0-9]{20}$") && Regex.IsMatch(r.sourceHash ?? "", "^[a-f0-9]{64}$") && r.sourceSize > 0 && r.sourceSize <= 128 * 1024 * 1024 && Plan.locations.Any(l => l.recipe == r.key), "리소스 규칙이 잘못되었습니다.");
                    Storage.Require(new[] { "text", "companion", "title", "warning" }.Contains(r.kind), "지원하지 않는 리소스 종류입니다.");
                    if (r.kind == "companion") Storage.Require(Recipes.ContainsKey(r.companionOf) && Recipes[r.companionOf].kind == "text", "이벤트 폰트 참조가 잘못되었습니다.");
                    if (r.kind == "text") Storage.Require((r.mode == 1 || r.mode == 2) && r.records != null && r.mapping != null && r.records.Select(t => t.id).Distinct().Count() == r.records.Length, "텍스트 규칙이 잘못되었습니다.");
                    if (r.font != null) Storage.Require(r.font.count > 0 && r.font.count < 16383 && r.font.glyphs != null && r.font.glyphs.Select(g => g.index).Distinct().Count() == r.font.glyphs.Length && r.font.fontSize >= 8 && r.font.fontSize < 256, "폰트 규칙이 잘못되었습니다.");
                }
            }
            catch { if (zip != null) zip.Dispose(); file.Dispose(); throw; }
        }
        private byte[] Read(string name, int limit)
        {
            var e = zip.GetEntry(name); Storage.Require(e != null && e.Length > 0 && e.Length <= limit, "배포 항목 크기가 잘못되었습니다.");
            using (var s = e.Open()) { var bytes = new byte[(int)e.Length]; Storage.ReadExactly(s, bytes, bytes.Length); Storage.Require(s.ReadByte() == -1, "배포 항목이 예상보다 깁니다."); return bytes; }
        }
        public void Dispose() { zip.Dispose(); file.Dispose(); }
    }
    internal sealed class ResourceStager
    {
        private readonly SemanticPackage package;
        private readonly Dictionary<string, FileStream> files;
        private readonly string stage;
        private readonly OuterTable outer;
        private readonly Dictionary<int, Kcap> archives = new Dictionary<int, Kcap>();
        private readonly Dictionary<string, string> compiled = new Dictionary<string, string>();
        private readonly List<Operation> operations = new List<Operation>();
        private readonly ResourceCompiler compiler;
        private long append;
        internal ResourceStager(SemanticPackage package, Dictionary<string, FileStream> files, string stage, ResourceCompiler compiler)
        { this.package = package; this.files = files; this.stage = stage; this.compiler = compiler; outer = new OuterTable(BinaryData.Read(files["0000.bin"], 0, OuterTable.Size)); append = files["0000.bin"].Length; }
        private Kcap Archive(ResourceLocation l)
        {
            Kcap archive;
            if (!archives.TryGetValue(l.outerId, out archive)) archives[l.outerId] = archive = new Kcap(files[outer.FileName(l.outerId)], outer.Offset(l.outerId), outer.Length(l.outerId));
            Storage.Require(l.member < archive.Members.Length, "패키지 멤버 번호가 다릅니다: " + l.package); return archive;
        }
        private byte[] Packed(ResourceLocation l) { return Archive(l).ReadPacked(files[outer.FileName(l.outerId)], outer.Offset(l.outerId), l.member); }
        private byte[] Resource(string key)
        {
            string path;
            if (!compiled.TryGetValue(key, out path))
            {
                var r = package.Recipes[key]; var l = package.Plan.locations.First(x => x.recipe == key); var result = compiler.Compile(r, Slz.Decode(Packed(l)), Resource);
                path = Path.Combine(stage, "r_" + key + ".raw"); File.WriteAllBytes(path, result); compiled.Add(key, path); return result;
            }
            return File.ReadAllBytes(path);
        }
        private void Add(string label, string file, long offset, byte[] result)
        {
            long oldLength = Math.Min(result.Length, Math.Max(0, files[file].Length - offset)); string oldHash = oldLength == 0 ? null : Storage.HashRange(files[file], offset, oldLength, null);
            if (oldLength == result.Length && oldHash == Storage.Hash(result)) return;
            string entry = operations.Count.ToString("D4") + ".new"; File.WriteAllBytes(Path.Combine(stage, entry), result);
            operations.Add(new Operation { label = label, sourceFile = file, targetFile = file, sourceOffset = offset, targetOffset = offset, sourceLength = oldLength, targetLength = result.Length, sourceHash = oldHash, targetHash = Storage.Hash(result), stageFile = entry });
        }
        internal Manifest Build(byte[] executable, Action<int> progress)
        {
            var updatedOuter = new OuterTable(outer.Encoded);
            var groups = package.Plan.locations.GroupBy(l => l.outerId).ToArray(); int done = 0;
            foreach (var group in groups)
            {
                var first = group.First(); var archive = Archive(first); string file = outer.FileName(first.outerId); long start = outer.Offset(first.outerId);
                bool movable = new[] { "0003.pkg", "0008.pkg", "0015.pkg", "0047.pkg" }.Contains(first.package);
                var replacements = new Dictionary<int, Tuple<byte[], int>>();
                foreach (var location in group)
                {
                    var original = Packed(location); var raw = Slz.Decode(original); var recipe = package.Recipes[location.recipe];
                    Storage.Require(raw.Length == recipe.sourceSize && Storage.Hash(raw) == recipe.sourceHash, "원본 멤버가 다릅니다: " + location.package + ":" + location.member);
                    var target = Resource(location.recipe);
                    string compressionKey = location.recipe + "_" + Storage.Hash(original).Substring(0, 20) + "_" + Storage.Hash(target).Substring(0, 20), compressedPath = Path.Combine(stage, "z_" + compressionKey + ".slz");
                    byte[] packed;
                    if (File.Exists(compressedPath)) packed = File.ReadAllBytes(compressedPath);
                    else
                    {
                        packed = recipe.font != null && recipe.font.common && movable ? Slz.Stored(target, original) : Slz.Encode(target, original);
                        if (!movable && packed.Length > archive.Members[location.member].Extent) packed = Slz.Encode(target, original, true);
                        if (!movable && packed.Length > archive.Members[location.member].Extent)
                        {
                            var bytePacked = Slz.Encode(target, original, target.Length < 32768, 2);
                            if (bytePacked.Length < packed.Length) packed = bytePacked;
                        }
                        File.WriteAllBytes(compressedPath, packed);
                    }
                    int size = BinaryData.Magic(original, 0, "SLZ") ? BinaryData.Align(target.Length, 16) : target.Length;
                    Storage.Require(packed.Length <= archive.Members[location.member].Extent || movable, "음성 위치를 유지할 수 없어 적용을 중단했습니다: " + location.package + ":" + location.member + " (" + packed.Length + "/" + archive.Members[location.member].Extent + ")");
                    replacements.Add(location.member, Tuple.Create(packed, size));
                }
                bool relocate = replacements.Any(kv => kv.Value.Item1.Length > archive.Members[kv.Key].Extent);
                if (relocate)
                {
                    Storage.Require(movable && file == "0000.bin", "이동할 수 없는 패키지입니다.");
                    using (var rebuilt = new MemoryStream())
                    {
                        rebuilt.Write(archive.Header, 0, archive.Header.Length);
                        // Match the legacy KCAP rebuild: members remain in table order.
                        foreach (var member in archive.Members)
                        {
                            ResourceCompiler.Pad(rebuilt, 16); int at = (int)rebuilt.Length;
                            Tuple<byte[], int> replacement;
                            byte[] payload; int size;
                            if (replacements.TryGetValue(member.Index, out replacement)) { payload = replacement.Item1; size = replacement.Item2; }
                            else { payload = archive.ReadPacked(files[file], start, member.Index); size = member.Size; }
                            rebuilt.Position = at; rebuilt.Write(payload, 0, payload.Length);
                            rebuilt.Position = 16 + member.Index * 16 + 8; rebuilt.Write(BitConverter.GetBytes(size), 0, 4); rebuilt.Write(BitConverter.GetBytes(at), 0, 4);
                        }
                        rebuilt.Position = rebuilt.Length; ResourceCompiler.Pad(rebuilt, 16); int logical = (int)rebuilt.Length; rebuilt.Position = 12; rebuilt.Write(BitConverter.GetBytes(logical), 0, 4); ResourceCompiler.Pad(rebuilt, 2048); byte[] result = rebuilt.ToArray();
                        using (var check = new MemoryStream(result)) { var cap = new Kcap(check, 0, result.Length); foreach (var kv in replacements) Storage.Require(cap.ReadPacked(check, 0, kv.Key).SequenceEqual(kv.Value.Item1), "재생성 패키지 검증 실패"); }
                        Add(first.package, file, append, result); updatedOuter.Relocate(first.outerId, append, result.Length); append += result.Length;
                    }
                }
                else foreach (var kv in replacements)
                {
                    var member = archive.Members[kv.Key]; byte[] prior = archive.ReadPacked(files[file], start, kv.Key); var data = new byte[Math.Max(prior.Length, kv.Value.Item1.Length)]; Buffer.BlockCopy(kv.Value.Item1, 0, data, 0, kv.Value.Item1.Length);
                    Add(first.package + ":" + kv.Key, file, start + member.Offset, data); Add(first.package + ":" + kv.Key + ":size", file, start + 16 + kv.Key * 16 + 8, BitConverter.GetBytes(kv.Value.Item2));
                }
                done++; if (progress != null) progress(done * 7900 / groups.Length);
            }
            var originalTable = BinaryData.Read(files["0000.bin"], 0, OuterTable.Size);
            for (int p = 0; p < OuterTable.Size; p += 12) if (!BinaryData.Slice(originalTable, p, 12).SequenceEqual(BinaryData.Slice(updatedOuter.Encoded, p, 12))) Add("outer-table", "0000.bin", p, BinaryData.Slice(updatedOuter.Encoded, p, 12));
            var commonRecipe = package.Recipes.Values.First(r => r.font != null && r.font.common);
            var global = commonRecipe.mapping;
            var commonData = Resource(commonRecipe.key);
            if (BinaryData.I32(commonData, 48) > 0)
            {
                int count = checked(BinaryData.I32(commonData, 144) + BinaryData.I32(commonData, 160));
                int metrics = BinaryData.I32(commonData, 40), end = BinaryData.I32(commonData, 44);
                foreach (char ch in string.Concat(RuntimeModule.Names).Distinct())
                {
                    int glyph;
                    Storage.Require(global.TryGetValue(ch.ToString(), out glyph) && glyph >= 0 && glyph < count && metrics + (long)(glyph + 1) * 24 <= end, "이름 글리프 범위 오류: " + ch);
                    Storage.Require(BinaryData.I32(commonData, metrics + glyph * 24) > 0, "이름 글리프가 비어 있습니다: " + ch);
                }
            }
            var module = RuntimeModule.Build(executable, global); File.WriteAllBytes(Path.Combine(stage, "wininet.dll"), module.Bytes);
            var plan = new Manifest { buildId = package.Plan.buildId, nativeHash = Storage.Hash(module.Bytes), operations = operations.ToArray(), files = package.Plan.sourceLengths.Select(kv => new FilePlan { name = kv.Key, sourceLength = kv.Value, targetLength = kv.Key == "0000.bin" ? append : kv.Value }).ToArray() };
            PatchEngine.ValidateOperations(plan.files, plan.operations); return plan;
        }
    }
    public sealed class PatchEngine
    {
        public const string RecordName = "SO4KoreanPatch.install.json";
        private readonly MonotonicProgress progress;
        public Action<int> AfterWrite;
        public PatchEngine(Action<int> report) { progress = new MonotonicProgress(report); }
        internal static void Idle() { foreach (var p in Process.GetProcessesByName("StarOceanTheLastHope")) { p.Dispose(); throw new IOException("게임을 종료한 뒤 다시 실행해 주세요."); } }
        public static bool IsGameFolder(string root) { return Directory.Exists(root) && new[] { "0000.bin", "0001.bin", "StarOceanTheLastHope.exe" }.All(n => File.Exists(Path.Combine(root, n))); }
        internal static void ValidateOperations(FilePlan[] files, Operation[] ops)
        {
            Storage.Require(files != null && files.Length == 2 && files.Select(f => f.name).OrderBy(n => n).SequenceEqual(new[] { "0000.bin", "0001.bin" }) && ops != null && ops.Length > 0 && ops.Length < 10000, "설치 범위 목록이 잘못되었습니다.");
            foreach (var f in files)
            {
                Storage.Require(f.sourceLength >= OuterTable.Size && f.targetLength >= f.sourceLength && f.targetLength < 256L * 1024 * 1024 * 1024, "설치 파일 크기가 잘못되었습니다."); long end = 0;
                foreach (var op in ops.Where(o => o.targetFile == f.name).OrderBy(o => o.targetOffset))
                {
                    Storage.Require(op.targetOffset >= end && op.targetLength > 0 && op.targetLength <= f.targetLength && op.targetOffset <= f.targetLength - op.targetLength && Regex.IsMatch(op.targetHash ?? "", "^[a-f0-9]{64}$"), "설치 구간이 겹치거나 범위를 벗어납니다."); end = op.targetOffset + op.targetLength;
                }
                long append = f.sourceLength; foreach (var op in ops.Where(o => o.targetFile == f.name && o.targetOffset >= f.sourceLength).OrderBy(o => o.targetOffset)) { Storage.Require(op.targetOffset == append, "추가 영역이 연속적이지 않습니다."); append += op.targetLength; } Storage.Require(append == f.targetLength, "추가 영역 길이가 다릅니다.");
            }
            Storage.Require(ops.All(o => files.Any(f => f.name == o.targetFile)), "허용하지 않은 설치 파일입니다.");
        }
        private static string BackupRoot(string root, Journal j) { Storage.Require(Regex.IsMatch(j.backupFolder ?? "", "^SO4KoreanPatch\\.backup\\.[a-f0-9]{32}$"), "복구 폴더가 잘못되었습니다."); return Path.Combine(root, j.backupFolder); }
        private static void Rollback(string root, Journal j, Dictionary<string, FileStream> files)
        {
            Storage.Require(j.schema == 2, "지원하지 않는 설치 기록입니다."); ValidateOperations(j.files, j.operations); string backup = BackupRoot(root, j);
            Storage.Require(j.backups != null && j.backups.Count == j.operations.Count(o => o.sourceLength > 0) &&
                j.operations.Where(o => o.sourceLength > 0).All(o => j.backups.Count(b => b.file == o.targetFile && b.offset == o.targetOffset && b.length == o.sourceLength && b.hash == o.sourceHash) == 1), "복구 백업 목록이 설치 기록과 다릅니다.");
            foreach (var b in j.backups)
            {
                Storage.Require(files.ContainsKey(b.file) && Regex.IsMatch(b.entry ?? "", "^[0-9]{4}\\.bak$") && b.offset >= 0 && b.length > 0 && b.offset <= j.files.Single(f => f.name == b.file).sourceLength - b.length, "복구 구간이 잘못되었습니다.");
                string path = Path.Combine(backup, b.entry); Storage.Require(File.Exists(path) && new FileInfo(path).Length == b.length && Storage.HashFile(path) == b.hash, "복구 백업이 손상되었습니다.");
            }
            string dll = Path.Combine(root, "wininet.dll"), packed = Path.Combine(root, "packed.txt");
            if (File.Exists(dll)) Storage.Require(Storage.HashFile(dll) == j.nativeHash, "다른 DLL이 있어 복구를 중단했습니다.");
            if (File.Exists(packed)) Storage.Require(Storage.HashFile(packed) == j.packedHash, "다른 변경 목록이 있어 복구를 중단했습니다.");
            foreach (var b in j.backups) using (var input = File.OpenRead(Path.Combine(backup, b.entry))) { files[b.file].Position = b.offset; Storage.Copy(input, 0, files[b.file], b.length); }
            foreach (var f in j.files) { files[f.name].SetLength(f.sourceLength); files[f.name].Flush(true); }
            foreach (var b in j.backups) Storage.Require(Storage.HashRange(files[b.file], b.offset, b.length, null) == b.hash, "복구 구간 검증 실패");
            if (File.Exists(dll)) File.Delete(dll); if (File.Exists(packed)) File.Delete(packed); j.state = "rolled-back"; Storage.AtomicJson(Path.Combine(root, RecordName), j);
        }
        public void Run(string root, string data, string dataHash, bool verifyOnly, string verificationWorkspace)
        {
            root = Path.GetFullPath(root); Storage.Require(IsGameFolder(root), "게임 설치 폴더를 확인해 주세요."); Idle();
            using (var package = new SemanticPackage(data, dataHash))
            {
                var files = new Dictionary<string, FileStream>(); string stage = null; Journal journal = null; bool durable = false;
                try
                {
                    foreach (string name in package.Plan.sourceLengths.Keys) files.Add(name, new FileStream(Path.Combine(root, name), FileMode.Open, verifyOnly ? FileAccess.Read : FileAccess.ReadWrite, verifyOnly ? FileShare.Read : FileShare.None));
                    string record = Path.Combine(root, RecordName);
                    if (File.Exists(record))
                    {
                        var existing = Storage.Json<Journal>(File.ReadAllText(record, Storage.Utf8));
                        Storage.Require(existing.schema == 2, "지원하지 않는 설치 기록입니다."); ValidateOperations(existing.files, existing.operations);
                        Storage.Require(existing.files.All(f => package.Plan.sourceLengths[f.name] == f.sourceLength), "설치 기록의 원본 크기가 다릅니다.");
                        if (existing.state == "installed")
                        {
                            Verify(files, existing.files, existing.operations, 0, verifyOnly ? 9800 : 500); VerifyExtra(root, existing);
                            if (verifyOnly) { Storage.Require(existing.buildId == package.Plan.buildId, "다른 빌드가 설치되어 있습니다. 한글 패치 버튼으로 업데이트해 주세요."); progress.Set(10000); return; }
                            // A durable recovery state also covers interruption while restoring the previous build.
                            existing.state = "installing"; Storage.AtomicJson(record, existing);
                            Rollback(root, existing, files);
                        }
                        if (existing.state == "installing") { Storage.Require(!verifyOnly, "미완료 설치 기록이 있습니다. 패치 프로그램으로 복구해 주세요."); Rollback(root, existing, files); }
                        else Storage.Require(existing.state == "rolled-back", "알 수 없는 설치 상태입니다.");
                    }
                    Storage.Require(!File.Exists(Path.Combine(root, "wininet.dll")) && !File.Exists(Path.Combine(root, "packed.txt")), "기존 패치 또는 모드가 있습니다. 원본 게임 폴더에서 실행해 주세요.");
                    foreach (var kv in package.Plan.sourceLengths) Storage.Require(files[kv.Key].Length == kv.Value, "원본 파일 크기가 다릅니다: " + kv.Key);
                    string workspace = verifyOnly ? Path.GetFullPath(verificationWorkspace) : root; Directory.CreateDirectory(workspace);
                    Storage.Require(new DriveInfo(Path.GetPathRoot(workspace)).AvailableFreeSpace > 1024L * 1024 * 1024, "작업 공간이 부족합니다.");
                    stage = Path.Combine(workspace, "SO4KoreanPatch.stage." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
                    Manifest plan;
                    using (var compiler = new ResourceCompiler(package.Font, package.Plan.version)) plan = new ResourceStager(package, files, stage, compiler).Build(File.ReadAllBytes(Path.Combine(root, "StarOceanTheLastHope.exe")), progress.Set);
                    progress.Set(8000);
                    if (verifyOnly) { Storage.AtomicJson(Path.Combine(workspace, "resource-verification.json"), new { passed = true, resourceRecipes = package.Recipes.Count, memberLocations = package.Plan.locations.Length, changedBytes = plan.operations.Sum(o => o.targetLength), operations = plan.operations.Length, nativeBytes = new FileInfo(Path.Combine(stage, "wininet.dll")).Length, buildId = package.Plan.buildId }); progress.Set(10000); return; }
                    Idle();
                    byte[] packedText = Storage.Utf8.GetBytes("0000.bin\r\n0001.bin\r\nwininet.dll\r\n");
                    journal = new Journal { state = "installing", buildId = plan.buildId, files = plan.files, operations = plan.operations, nativeHash = plan.nativeHash, packedHash = Storage.Hash(packedText), backupFolder = "SO4KoreanPatch.backup." + Guid.NewGuid().ToString("N") };
                    string backup = BackupRoot(root, journal); Directory.CreateDirectory(backup);
                    for (int i = 0; i < plan.operations.Length; i++)
                    {
                        var op = plan.operations[i]; Storage.Require(Storage.HashFile(Path.Combine(stage, op.stageFile)) == op.targetHash, "적용 직전 생성물 검증 실패");
                        if (op.sourceLength == 0) continue;
                        Storage.Require(Storage.HashRange(files[op.targetFile], op.targetOffset, op.sourceLength, null) == op.sourceHash, "적용 직전 원본이 변경되었습니다.");
                        string entry = i.ToString("D4") + ".bak", path = Path.Combine(backup, entry);
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { Storage.Copy(files[op.targetFile], op.targetOffset, output, op.sourceLength); output.Flush(true); }
                        Storage.Require(Storage.HashFile(path) == op.sourceHash, "복구 백업 검증 실패"); journal.backups.Add(new Backup { file = op.targetFile, entry = entry, offset = op.targetOffset, length = op.sourceLength, hash = op.sourceHash });
                    }
                    Storage.AtomicJson(record, journal); durable = true; progress.Set(8500); Idle(); int writes = 0;
                    foreach (var op in plan.operations.OrderBy(o => o.label == "outer-table" ? 1 : 0))
                    {
                        files[op.targetFile].Position = op.targetOffset; using (var input = File.OpenRead(Path.Combine(stage, op.stageFile))) Storage.Copy(input, 0, files[op.targetFile], op.targetLength); files[op.targetFile].Flush(true);
                        writes++; if (AfterWrite != null) AfterWrite(writes); progress.Set(8500 + writes * 900 / plan.operations.Length);
                    }
                    File.Move(Path.Combine(stage, "wininet.dll"), Path.Combine(root, "wininet.dll"));
                    using (var output = new FileStream(Path.Combine(root, "packed.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { output.Write(packedText, 0, packedText.Length); output.Flush(true); }
                    Verify(files, plan.files, plan.operations, 9400, 590); VerifyExtra(root, journal); journal.state = "installed"; Storage.AtomicJson(record, journal); progress.Set(10000);
                }
                catch (Exception error)
                {
                    if (durable && journal != null && journal.state == "installing") try { Rollback(root, journal, files); } catch (Exception rollback) { throw new IOException("패치와 자동 복구가 완료되지 않았습니다. 설치 기록과 백업 폴더를 보존해 주세요.\n" + rollback.Message, error); }
                    throw;
                }
                finally
                {
                    foreach (var f in files.Values) f.Dispose();
                    if (stage != null && Directory.Exists(stage)) try { foreach (string path in Directory.GetFiles(stage)) File.Delete(path); Directory.Delete(stage, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        private static void VerifyExtra(string root, Journal j)
        {
            Storage.Require(File.Exists(Path.Combine(root, "wininet.dll")) && Storage.HashFile(Path.Combine(root, "wininet.dll")) == j.nativeHash, "보정 모듈 검증 실패");
            Storage.Require(File.Exists(Path.Combine(root, "packed.txt")) && Storage.HashFile(Path.Combine(root, "packed.txt")) == j.packedHash, "변경 목록 검증 실패");
        }
        private void Verify(Dictionary<string, FileStream> streams, FilePlan[] files, Operation[] operations, int start, int span)
        {
            foreach (var f in files) Storage.Require(streams[f.name].Length == f.targetLength, "적용 파일 길이가 다릅니다."); long total = operations.Sum(o => o.targetLength), done = 0;
            foreach (var op in operations) { long previous = done; Storage.Require(Storage.HashRange(streams[op.targetFile], op.targetOffset, op.targetLength, n => progress.Set(start + (int)((previous + n) * span / total))) == op.targetHash, "적용 구간 검증 실패: " + op.label); done += op.targetLength; }
        }
    }
}
