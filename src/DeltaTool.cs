using System;
using System.Diagnostics;
using System.IO;
using System.Text;
namespace SO4KoreanPatcher
{
    internal static class DeltaTool
    {
        internal static void Run(string executable, string arguments)
        {
            var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(executable) };
            info.EnvironmentVariables.Remove("XDELTA");
            using (var process = Process.Start(info))
            {
                var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(120000)) { process.Kill(); throw new IOException("xdelta 처리 제한 시간을 초과했습니다."); }
                string details = error.GetAwaiter().GetResult(); output.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new InvalidDataException("xdelta 처리에 실패했습니다. " + details.Trim());
            }
        }
        internal static string Q(string path)
        {
            Storage.Require(!path.Contains("\"") && !path.EndsWith("\\", StringComparison.Ordinal), "파일 경로가 잘못되었습니다.");
            return "\"" + path + "\"";
        }
        internal static byte[] Read(Stream file, long offset, long size)
        {
            Storage.Require(offset >= 0 && size >= 0 && size <= 64L * 1024 * 1024 && offset <= file.Length - size, "파일 구간이 범위를 벗어났습니다.");
            file.Position = offset; var result = new byte[(int)size]; Storage.ReadExactly(file, result, result.Length); return result;
        }
        internal static void WriteDurable(string path, byte[] bytes)
        { using (var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) { f.Write(bytes, 0, bytes.Length); f.Flush(true); } }
    }
}
