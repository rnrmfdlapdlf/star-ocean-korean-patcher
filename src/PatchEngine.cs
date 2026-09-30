using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SO4KoreanPatcher
{
    public sealed class FilePlan
    {
        public string name, sourceHash, targetHash;
        public long sourceLength, targetLength;
    }
    public sealed class Operation
    {
        public string label, sourceFile, sourceHash, targetFile, targetHash, stageFile;
        public long sourceOffset, sourceLength, targetOffset, targetLength;
    }
    public sealed class Manifest
    {
        public int schema;
        public string format, version, builtAt, author, exeHash, nativeHash, buildId;
        public FilePlan[] files;
        public Operation[] operations;
    }
    public sealed class Backup
    {
        public string file, entry, hash;
        public long offset, length;
    }
    public sealed class Journal
    {
        public int schema = 2;
        public string state, buildId, backupFolder, nativeHash, packedHash;
        public Operation[] operations;
        public FilePlan[] files;
        public List<Backup> backups = new List<Backup>();
    }
    public sealed class MonotonicProgress
    {
        private int value;
        private readonly Action<int> report;
        public MonotonicProgress(Action<int> report) { this.report = report; }
        public int Value { get { return value; } }
        public void Set(int next)
        {
            if (next < 0 || next > 10000) throw new ArgumentOutOfRangeException("next");
            lock (this) { if (next > value) { value = next; if (report != null) report(value); } }
        }
    }
    public static class Storage
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        public static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
        public static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(bytes)); }
        public static string HashFile(string path) { using (var f = File.OpenRead(path)) return HashRange(f, 0, f.Length, null); }
        public static string HashRange(Stream stream, long offset, long length, Action<long> progress)
        {
            stream.Position = offset;
            using (var hash = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024]; long done = 0;
                while (done < length)
                {
                    int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - done));
                    if (n == 0) throw new EndOfStreamException();
                    hash.TransformBlock(buffer, 0, n, buffer, 0); done += n;
                    if (progress != null) progress(done);
                }
                hash.TransformFinalBlock(new byte[0], 0, 0); return Hex(hash.Hash);
            }
        }
        public static T Json<T>(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 40 }.Deserialize<T>(text);
        }
        public static string Json(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 40 }.Serialize(value);
        }
        public static void AtomicJson(string path, object value)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { byte[] b = Utf8.GetBytes(Json(value)); f.Write(b, 0, b.Length); f.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static void Copy(Stream source, long offset, Stream target, long length)
        {
            source.Position = offset; byte[] b = new byte[1024 * 1024];
            while (length > 0)
            {
                int n = source.Read(b, 0, (int)Math.Min(b.Length, length));
                if (n == 0) throw new EndOfStreamException(); target.Write(b, 0, n); length -= n;
            }
        }
        public static void ReadExactly(Stream source, byte[] b, int count)
        {
            int at = 0;
            while (at < count) { int n = source.Read(b, at, count - at); if (n == 0) throw new EndOfStreamException(); at += n; }
        }
        public static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    }
}
