using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SO4KoreanPatcher
{
    public sealed class XboxFile
    {
        public string path, sha256, target_sha256, delta, delta_sha256;
        public long offset, size, target_size;
    }
    public sealed class XboxDisc
    {
        public int disc;
        public string iso_sha256;
        public long iso_size;
        public XboxFile[] files;
    }
    public sealed class XboxTool { public string path, sha256; }
    public sealed class XboxManifest
    {
        public int schema;
        public XboxDisc[] discs;
        public XboxTool[] tools;
    }
    public sealed class XboxPatchEngine
    {
        private readonly MonotonicProgress progress;
        private readonly Action<string> message;
        public XboxPatchEngine(Action<int> report, Action<string> message)
        { progress = new MonotonicProgress(report); this.message = message ?? delegate { }; }

        internal static string SafePath(string root, string name)
        {
            Storage.Require(!string.IsNullOrWhiteSpace(name) && !Path.IsPathRooted(name) && !name.Contains(":"), "패치 목록의 경로가 잘못되었습니다.");
            string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(prefix, name));
            Storage.Require(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "패치 목록의 경로가 폴더를 벗어났습니다.");
            return path;
        }
        internal static void ValidateManifest(XboxManifest plan)
        {
            Storage.Require(plan != null && plan.schema == 1 && plan.discs != null && plan.discs.Length == 3 && plan.discs.Select(d => d.disc).OrderBy(d => d).SequenceEqual(new[] { 1, 2, 3 }), "Xbox 360 패치 목록이 잘못되었습니다.");
            foreach (var disc in plan.discs)
            {
                Storage.Require(disc.iso_size > 0 && disc.files != null && disc.files.Length == 4, "디스크 파일 목록이 잘못되었습니다.");
                Storage.Require(disc.files.Select(f => f.path).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "$SystemUpdate/su20076000_00000000", "default.xex", "soz0.bin", "soz1.bin" }), "지원하지 않는 디스크 파일 구성입니다.");
                foreach (var f in disc.files)
                    Storage.Require(f.offset >= 0 && f.size > 0 && f.offset <= disc.iso_size - f.size && f.target_size > 0 && f.target_size < 0x100000000L, "디스크 파일 범위가 잘못되었습니다.");
            }
            Storage.Require(plan.tools != null && plan.tools.Length == 2 && plan.tools.Any(t => t.path == "tools/exiso.exe") && plan.tools.Any(t => t.path == "tools/xdelta3.exe"), "Xbox 360 도구 목록이 잘못되었습니다.");
        }

        public string[] Run(string[] isoPaths, string package)
        {
            Storage.Require(isoPaths != null && isoPaths.Length == 3 && isoPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 && isoPaths.All(p => File.Exists(p) && string.Equals(Path.GetExtension(p), ".iso", StringComparison.OrdinalIgnoreCase)), "서로 다른 원본 ISO 3개를 선택해 주세요.");
            string manifest = Path.Combine(package, "manifest.json");
            Storage.Require(File.Exists(manifest), "Xbox360 패치 자료가 없습니다. 배포 압축 파일 전체를 해제해 주세요.");
            byte[] bytes = File.ReadAllBytes(manifest);
            Storage.Require(Storage.Hash(bytes) == XboxBuildInfo.ManifestHash, "Xbox 360 패치 목록이 손상되었거나 실행 파일과 버전이 다릅니다.");
            var plan = Storage.Json<XboxManifest>(Storage.Utf8.GetString(bytes)); ValidateManifest(plan);
            foreach (var tool in plan.tools) RequireHash(SafePath(package, tool.path), tool.sha256);
            foreach (var file in plan.discs.SelectMany(d => d.files).Where(f => f.delta != null)) RequireHash(SafePath(package, file.delta), file.delta_sha256);
            var sources = new Dictionary<int, FileStream>();
            var sourcePaths = new Dictionary<int, string>();
            var results = new List<string>();
            try
            {
                // Keep each input open without write sharing through completion.
                for (int i = 0; i < isoPaths.Length; i++)
                {
                    message("원본 ISO 확인 중 (" + (i + 1) + "/3): " + Path.GetFileName(isoPaths[i]));
                    var stream = new FileStream(isoPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        Storage.Require(plan.discs.Any(d => d.iso_size == stream.Length), "지원하는 일본판 원본 ISO 크기와 다릅니다: " + Path.GetFileName(isoPaths[i]));
                        int start = i * 800;
                        string hash = Storage.HashRange(stream, 0, stream.Length, n => progress.Set(start + (int)(800L * n / stream.Length)));
                        var disc = plan.discs.SingleOrDefault(d => d.iso_sha256 == hash && d.iso_size == stream.Length);
                        Storage.Require(disc != null, "지원하는 일본판 원본 ISO가 아닙니다: " + Path.GetFileName(isoPaths[i]) + "\n변경되지 않은 Disc 1·2·3 원본을 사용해 주세요.");
                        Storage.Require(!sources.ContainsKey(disc.disc), "Disc " + disc.disc + "이 중복되었습니다. 서로 다른 디스크 3개를 선택해 주세요.");
                        sources.Add(disc.disc, stream); sourcePaths.Add(disc.disc, Path.GetFullPath(isoPaths[i])); stream = null;
                    }
                    finally { if (stream != null) stream.Dispose(); }
                }
                foreach (var disc in plan.discs.OrderBy(d => d.disc))
                {
                    int start = 2400 + (disc.disc - 1) * 2500;
                    string parent = Path.GetDirectoryName(sourcePaths[disc.disc]);
                    var drive = new DriveInfo(Path.GetPathRoot(parent));
                    long required = disc.files.Sum(f => f.size + 2 * f.target_size) + 1024L * 1024 * 1024;
                    Storage.Require(!string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase), "결과 ISO는 4 GB를 초과합니다. NTFS 또는 exFAT 위치에서 실행해 주세요.");
                    Storage.Require(drive.AvailableFreeSpace >= required, "원본 ISO 위치에 약 " + (required / (1024 * 1024 * 1024) + 1) + " GB의 여유 공간이 필요합니다.");
                    string iso = OutputPath(sourcePaths[disc.disc]);
                    string work = Path.Combine(parent, ".so4-work-" + Guid.NewGuid().ToString("N")), extracted = Path.Combine(work, "extracted");
                    Directory.CreateDirectory(extracted);
                    string partial = Path.Combine(work, "result.iso.partial");
                    try
                    {
                    for (int i = 0; i < disc.files.Length; i++)
                    {
                        var f = disc.files[i]; string target = SafePath(extracted, f.path);
                        string source = f.delta == null ? target : SafePath(work, "source_" + f.path);
                        Directory.CreateDirectory(Path.GetDirectoryName(source)); Directory.CreateDirectory(Path.GetDirectoryName(target));
                        message("Disc " + disc.disc + "/3 · 파일 추출: " + f.path);
                        using (var dst = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Storage.Copy(sources[disc.disc], f.offset, dst, f.size);
                        RequireHash(source, f.sha256);
                        if (f.delta != null)
                        {
                            message("Disc " + disc.disc + "/3 · 한글 패치 적용: " + f.path);
                            RunTool(SafePath(package, "tools/xdelta3.exe"), "-d -D -R -s " + DeltaTool.Q(source) + " " + DeltaTool.Q(SafePath(package, f.delta)) + " " + DeltaTool.Q(target), Path.Combine(work, "disc" + disc.disc + "_" + f.path + ".log"));
                        }
                        Storage.Require(new FileInfo(target).Length == f.target_size, "패치된 파일 크기가 다릅니다: " + f.path);
                        RequireHash(target, f.target_sha256); progress.Set(start + (i + 1) * 400);
                    }
                    message("Disc " + disc.disc + "/3 · 결과 ISO 생성 중");
                    RunTool(SafePath(package, "tools/exiso.exe"), "-q -m -c " + DeltaTool.Q(extracted) + " " + DeltaTool.Q(partial), Path.Combine(work, "disc" + disc.disc + "_exiso.log"));
                    progress.Set(start + 2100); message("Disc " + disc.disc + "/3 · 결과 ISO 파일 확인 중");
                    VerifyIso(partial, disc.files);
                    if (File.Exists(iso)) File.Replace(partial, iso, null); else File.Move(partial, iso);
                    results.Add(iso); progress.Set(start + 2500);
                    }
                    finally
                    {
                        // All paths are confined to the unique directory created by this operation.
                        string prefix = Path.GetFullPath(work).TrimEnd('\\') + "\\";
                        foreach (string temp in Directory.GetFiles(work, "*", SearchOption.AllDirectories))
                        {
                            Storage.Require(Path.GetFullPath(temp).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "임시 경로 확인 실패");
                            File.Delete(temp);
                        }
                        foreach (string dir in Directory.GetDirectories(work, "*", SearchOption.AllDirectories).OrderByDescending(n => n.Length)) Directory.Delete(dir);
                        Directory.Delete(work);
                    }
                }
                progress.Set(10000); return results.ToArray();
            }
            finally { foreach (var source in sources.Values) source.Dispose(); }
        }
        internal static string OutputPath(string source)
        {
            string full = Path.GetFullPath(source);
            return Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + "_repacked.iso");
        }
        private static void RequireHash(string path, string hash)
        { Storage.Require(File.Exists(path) && Storage.HashFile(path) == hash, "파일이 없거나 해시가 다릅니다: " + path); }
        private static void RunTool(string path, string arguments, string log)
        {
            var info = new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(log) };
            info.EnvironmentVariables.Remove("XDELTA");
            using (var process = Process.Start(info))
            {
                var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync(); process.WaitForExit();
                string details = error.GetAwaiter().GetResult(); File.WriteAllText(log, output.GetAwaiter().GetResult() + details, Storage.Utf8);
                Storage.Require(process.ExitCode == 0, Path.GetFileName(path) + " 처리에 실패했습니다. " + details.Trim());
            }
        }
        internal static void VerifyIso(string path, XboxFile[] files)
        {
            using (var stream = File.OpenRead(path))
            {
                stream.Position = 0x10000; byte[] descriptor = new byte[28]; Storage.ReadExactly(stream, descriptor, descriptor.Length);
                Storage.Require(System.Text.Encoding.ASCII.GetString(descriptor, 0, 20) == "MICROSOFT*XBOX*MEDIA", "결과 ISO의 파일 시스템이 잘못되었습니다.");
                var found = new Dictionary<string, Tuple<long, long>>(StringComparer.OrdinalIgnoreCase);
                ReadDirectory(stream, BitConverter.ToUInt32(descriptor, 20), BitConverter.ToUInt32(descriptor, 24), "", found, new HashSet<uint>(), 0);
                Storage.Require(found.Count == files.Length, "결과 ISO 파일 수가 다릅니다.");
                foreach (var f in files)
                {
                    Tuple<long, long> entry;
                    Storage.Require(found.TryGetValue(f.path, out entry) && entry.Item2 == f.target_size, "결과 ISO에 파일이 없거나 크기가 다릅니다: " + f.path);
                    Storage.Require(Storage.HashRange(stream, entry.Item1, entry.Item2, null) == f.target_sha256, "결과 ISO 파일 해시가 다릅니다: " + f.path);
                }
            }
        }
        private static void ReadDirectory(Stream stream, uint sector, uint size, string prefix, Dictionary<string, Tuple<long, long>> found, HashSet<uint> directories, int depth)
        {
            Storage.Require(depth < 16 && size <= 16 * 1024 * 1024 && directories.Add(sector) && (long)sector * 2048 <= stream.Length - size, "결과 ISO 디렉터리가 잘못되었습니다.");
            byte[] data = new byte[(int)size]; stream.Position = (long)sector * 2048; Storage.ReadExactly(stream, data, data.Length);
            var pending = new Stack<int>(); var visited = new HashSet<int>(); if (size > 0) pending.Push(0);
            while (pending.Count > 0)
            {
                int at = pending.Pop(); Storage.Require(at >= 0 && at <= data.Length - 14 && visited.Add(at), "결과 ISO 디렉터리 트리가 잘못되었습니다.");
                int left = BitConverter.ToUInt16(data, at) * 4, right = BitConverter.ToUInt16(data, at + 2) * 4;
                uint sec = BitConverter.ToUInt32(data, at + 4), length = BitConverter.ToUInt32(data, at + 8); int count = data[at + 13];
                Storage.Require(at + 14 + count <= data.Length && count > 0, "결과 ISO 파일 이름이 잘렸습니다.");
                string name = System.Text.Encoding.ASCII.GetString(data, at + 14, count);
                Storage.Require(name != "." && name != ".." && name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0, "결과 ISO 파일 이름이 잘못되었습니다.");
                if ((data[at + 12] & 16) != 0) ReadDirectory(stream, sec, length, prefix + name + "/", found, directories, depth + 1);
                else { Storage.Require((long)sec * 2048 <= stream.Length - length && !found.ContainsKey(prefix + name), "결과 ISO 파일 범위가 잘못되었습니다."); found.Add(prefix + name, Tuple.Create((long)sec * 2048, (long)length)); }
                if (right != 0) pending.Push(right); if (left != 0) pending.Push(left);
            }
        }
    }
}
