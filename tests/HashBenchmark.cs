using System;
using System.Diagnostics;
using System.Security.Cryptography;
class HashBenchmark {
    static void Main() {
        byte[] data=new byte[1024*1024];new Random(37).NextBytes(data);
        foreach(var hash in new SHA256[]{SHA256.Create(),new SHA256Cng(),new SHA256CryptoServiceProvider()})
        using(hash){var watch=Stopwatch.StartNew();for(int i=0;i<256;i++)hash.TransformBlock(data,0,data.Length,data,0);hash.TransformFinalBlock(new byte[0],0,0);watch.Stop();Console.WriteLine(hash.GetType().Name+" "+(256.0/watch.Elapsed.TotalSeconds).ToString("F1")+" MiB/s "+BitConverter.ToString(hash.Hash));}
    }
}
