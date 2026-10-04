using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SO4KoreanPatcher
{
    internal sealed class PreparedSteamExe
    {
        internal byte[] Original, Target;
        internal string TargetHash;
        internal int Mask;
    }
    internal static class SteamCheats
    {
        internal const string ExeName = "StarOceanTheLastHope.exe";
        internal const string BackupName = "StarOceanTheLastHope.original.exe";
        internal const int MaximumExeLength = 128 * 1024 * 1024;
        internal static void ValidateOptions(SteamCheatOptions options)
        { Storage.Require((int)options >= 0 && (int)options <= 7, "치트 옵션이 올바르지 않습니다."); }
        private static bool Hash(string value)
        { return Regex.IsMatch(value ?? "", "^[a-f0-9]{64}$"); }
        internal static void Validate(SteamCheatCatalog catalog, string baseHash)
        {
            Storage.Require(catalog != null && catalog.schema == 1 && catalog.length > 0 && catalog.length <= MaximumExeLength && Hash(catalog.baseHash) && catalog.baseHash == baseHash,
                "Steam 치트 기준본 정보가 잘못되었습니다.");
            Storage.Require(catalog.variants != null && catalog.variants.Length == 8 && catalog.variants.All(v => v != null) &&
                catalog.variants.Select(v => v.mask).OrderBy(v => v).SequenceEqual(Enumerable.Range(0, 8)) &&
                catalog.variants.Select(v => v.hash).Distinct().Count() == 8, "Steam 치트 조합 목록이 잘못되었습니다.");
            foreach (var v in catalog.variants)
            {
                Storage.Require(Hash(v.hash), "Steam 실행 파일 해시가 잘못되었습니다.");
                if (v.mask == 0)
                    Storage.Require(v.hash == baseHash && v.forwardFile == null && v.reverseFile == null && v.forwardHash == null && v.reverseHash == null, "치트 미선택 기준본이 잘못되었습니다.");
                else
                    Storage.Require(v.forwardFile == "cheats/" + v.mask + ".xdelta" && v.reverseFile == "cheats/" + v.mask + "-original.xdelta" && Hash(v.forwardHash) && Hash(v.reverseHash), "Steam 치트 차분 경로가 잘못되었습니다.");
            }
        }
        private static byte[] Decode(DeltaPackage package, byte[] source, string name, string hash)
        {
            var delta = package.Read(name, 4 * 1024 * 1024);
            Storage.Require(Storage.Hash(delta) == hash, "Steam 치트 차분이 손상되었습니다.");
            return Vcdiff.Decode(source, delta, package.Plan.steamCheats.length, MaximumExeLength);
        }
        internal static PreparedSteamExe Prepare(DeltaPackage package, byte[] current, SteamCheatOptions options)
        {
            ValidateOptions(options);
            var catalog = package.Plan.steamCheats;
            Storage.Require(current.Length == catalog.length, "지원하는 Steam 실행 파일과 크기가 다릅니다.");
            string currentHash = Storage.Hash(current);
            var variant = catalog.variants.SingleOrDefault(v => v.hash == currentHash);
            Storage.Require(variant != null, "지원하는 Steam 실행 파일과 다릅니다. 원본 또는 이 프로그램의 치트 조합을 사용해 주세요.");
            var original = variant.mask == 0 ? current : Decode(package, current, variant.reverseFile, variant.reverseHash);
            return FromOriginal(package, original, options);
        }
        internal static PreparedSteamExe FromOriginal(DeltaPackage package, byte[] original, SteamCheatOptions options)
        {
            ValidateOptions(options);
            var catalog = package.Plan.steamCheats;
            Storage.Require(original.Length == catalog.length && Storage.Hash(original) == catalog.baseHash, "Steam 치트 원본 복원 결과가 다릅니다.");
            var wanted = catalog.variants.Single(v => v.mask == (int)options);
            var target = wanted.mask == 0 ? original : Decode(package, original, wanted.forwardFile, wanted.forwardHash);
            Storage.Require(Storage.Hash(target) == wanted.hash, "선택한 Steam 치트 조합의 결과가 다릅니다.");
            return new PreparedSteamExe { Original = original, Target = target, TargetHash = wanted.hash, Mask = wanted.mask };
        }
        internal static void ValidateBackup(string root, Journal journal, bool hashes, bool filesRequired = true)
        {
            var b = journal.exeBackup;
            Storage.Require(b != null && b.file == ExeName && b.entry == BackupName && b.offset == 0 && b.length > 0 && b.length <= MaximumExeLength &&
                Hash(b.hash) && Hash(journal.exeHash) && journal.cheatMask >= 0 && journal.cheatMask <= 7, "Steam 실행 파일 복구 기록이 잘못되었습니다.");
            string path = Path.Combine(root, journal.backupFolder, b.entry);
            Storage.Require(!filesRequired || (File.Exists(path) && new FileInfo(path).Length == b.length && (!hashes || Storage.HashFile(path) == b.hash)), "Steam 실행 파일 원본 백업이 손상되었습니다.");
        }
        internal static void VerifyInstalled(Journal journal, Stream exe)
        {
            Storage.Require(journal.exeBackup != null, "Steam 실행 파일 복구 기록이 없습니다.");
            Storage.Require(exe.Length == journal.exeBackup.length && Storage.HashRange(exe, 0, exe.Length, null) == journal.exeHash, "설치된 Steam 실행 파일이 변경되었습니다.");
        }
    }
}
