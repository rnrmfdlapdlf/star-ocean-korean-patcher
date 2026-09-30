using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SO4KoreanPatcher;

internal static class Tests
{
    private static int assertions;
    private static string output, project;
    private static byte[] executable, font;
    private static Dictionary<string,int> mapping;
    private static void Check(bool ok, string why) { assertions++; if (!ok) throw new Exception(why); }
    private static void Reject(Action action, string why) { bool failed=false; try { action(); } catch (IOException) { failed=true; } catch (InvalidDataException) { failed=true; } Check(failed, why); }
    private static void Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false); AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        try
        {
            if(args.Length!=2) throw new ArgumentException("Tests game-root isolated-output-directory");
            string root=Path.GetFullPath(args[0]); project=Path.Combine(root,"SO4KoreanPatcher"); output=Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            executable=CompactExecutable(File.ReadAllBytes(Path.Combine(root,"StarOceanTheLastHope.exe")));
            font=File.ReadAllBytes(Path.Combine(project,"Assets/NotoSansKR-Medium.ttf"));
            mapping=File.ReadLines(Path.Combine(project,"Assets/Translations.jsonl")).Select(Storage.Json<ResourceRecipe>).First(r=>r.font!=null&&r.font.common).mapping;
            TextAndCompression(); InstallTransactions(); RuntimeTests.Run(new[]{root,Path.Combine(output,"runtime")});
            Console.WriteLine("PASS semantic assertions="+assertions);
        }
        catch(Exception e) { Console.Error.WriteLine(e); Environment.ExitCode=1; }
    }
    private static byte[] CompactExecutable(byte[] original)
    {
        var pe=new PeReader(original); int[] code={0x77c105,0x6900a8,0x67f00e,0x67fb26,0x77c000,0x779660,0x75f5f0,0x76d510,0x754de0,0x4bede90,0x783de0};
        int[] slots={0x9dc430,0x9dc6a0,0xa64ed0,0xa65140,0xa82fa0,0xa90b40,0xaad6f0,0xa81628};
        var pages=code.Concat(slots).Select(r=>r&~4095).Distinct().OrderBy(r=>r).ToArray(); var result=new byte[4096+pages.Length*4096];
        Buffer.BlockCopy(original,0,result,0,4096); Buffer.BlockCopy(BitConverter.GetBytes((ushort)pages.Length),0,result,pe.Pe+6,2);
        BinaryData.Put(result,pe.Optional+60,4096);
        for(int i=0;i<pages.Length;i++) { int h=pe.Sections+i*40; Array.Clear(result,h,40); BinaryData.Put(result,h+8,4096); BinaryData.Put(result,h+12,(uint)pages[i]); BinaryData.Put(result,h+16,4096); BinaryData.Put(result,h+20,(uint)(4096+i*4096)); }
        var target=new PeReader(result);
        foreach(int r in code) Buffer.BlockCopy(pe.At(r,16),0,result,target.Offset(r),16);
        foreach(int r in slots) Buffer.BlockCopy(pe.At(r,8),0,result,target.Offset(r),8);
        return result;
    }
    private static void TextAndCompression()
    {
        var m=new Dictionary<string,int>{{"가",0},{"나",126},{"다",127},{"~",2100}}; var inv=m.ToDictionary(k=>k.Value,k=>k.Key);
        string text="가나\n다〔G:16402〕〔ARG:0100〕가〔END〕~〔G:16423〕〔ARG:00010203〕";
        byte[] bytes=GameText.Encode(text,m); Check(GameText.Decode(bytes,inv)==text,"Nested names and zero-valued control arguments"); Check(GameText.End(bytes,0)==bytes.Length,"Full control span");
        Reject(()=>GameText.Encode("없는 글자",m),"Missing glyph rejected"); Reject(()=>GameText.Encode("가〔END〕나",m),"Early terminator rejected");
        var rng=new Random(271828);
        foreach(int mode in new[]{2,3}) foreach(int size in new[]{2,256,65536,65538,131074})
        {
            var raw=new byte[size]; rng.NextBytes(raw); for(int i=20;i<raw.Length;i++) if(i%500<300)raw[i]=raw[i%20];
            var original=new byte[32]; Encoding.ASCII.GetBytes("SLZ").CopyTo(original,0); original[3]=(byte)mode; BinaryData.Put(original,20,32);
            foreach(bool optimal in new[]{false,true}) { byte[] encoded=Slz.Encode(raw,original,optimal); Check(Slz.Decode(encoded).SequenceEqual(raw),"SLZ roundtrip "+mode+":"+size); Check(Slz.Encode(raw,encoded,optimal).SequenceEqual(encoded),"Unchanged chunk reuse"); }
            Check(Slz.Decode(Slz.Stored(raw,original)).SequenceEqual(raw),"Stored SLZ");
        }
        for(int n=0;n<50;n++) { var alpha=new byte[16]; rng.NextBytes(alpha); var decoded=Bc7Reader.Block(Bc7Alpha.Block(alpha),0); Check(alpha.Zip(decoded,(a,b)=>Math.Abs(a-b)).Max()<=10,"BC7 alpha quantization bound"); }
        var p=new List<int>();var progress=new MonotonicProgress(p.Add);foreach(int n in new[]{10,5,10,9999,2,10000})progress.Set(n);Check(p.SequenceEqual(new[]{10,9999,10000}),"Progress never decreases");
    }
    private sealed class Fixture { internal string Root,Data,Hash; internal byte[] Before0,Before1; }
    private static void Add(ZipArchive z,string name,byte[] bytes) { using(var s=z.CreateEntry(name,CompressionLevel.Optimal).Open())s.Write(bytes,0,bytes.Length); }
    private static Fixture Make(string label,bool large=false,bool movable=true)
    {
        string root=Path.Combine(output,label+"-"+Guid.NewGuid().ToString("N").Substring(0,8)); Directory.CreateDirectory(root);
        var text=new byte[256];Encoding.ASCII.GetBytes("pDCM").CopyTo(text,0);BinaryData.Put(text,20,256);BinaryData.Put(text,32,128);BinaryData.Put(text,36,144);BinaryData.Put(text,60,2);
        BinaryData.Put(text,128,123);BinaryData.Put(text,132,0);BinaryData.Put(text,136,456);BinaryData.Put(text,140,2);text[144]=20;text[146]=30;
        var pkg=new byte[2048];Encoding.ASCII.GetBytes("KCAP").CopyTo(pkg,0);BinaryData.Put(pkg,8,2);BinaryData.Put(pkg,12,2048);BinaryData.Put(pkg,24,256);BinaryData.Put(pkg,28,64);BinaryData.Put(pkg,40,128);BinaryData.Put(pkg,44,512);Buffer.BlockCopy(text,0,pkg,64,text.Length);for(int i=512;i<640;i++)pkg[i]=(byte)i;
        var outer=new OuterTable(new byte[OuterTable.Size]);outer.Relocate(0,OuterTable.Size,pkg.Length);
        var b0=new byte[OuterTable.Size+pkg.Length];Buffer.BlockCopy(outer.Encoded,0,b0,0,outer.Encoded.Length);Buffer.BlockCopy(pkg,0,b0,OuterTable.Size,pkg.Length);var b1=new byte[OuterTable.Size];b1[111]=99;
        File.WriteAllBytes(Path.Combine(root,"0000.bin"),b0);File.WriteAllBytes(Path.Combine(root,"0001.bin"),b1);File.WriteAllBytes(Path.Combine(root,"StarOceanTheLastHope.exe"),executable);
        var recipe=new ResourceRecipe{key=new string('a',20),kind="text",sourceHash=Storage.Hash(text),sourceSize=text.Length,mode=1,mapping=mapping,records=new[]{new TextRecord{id=123,text=large?string.Concat(Enumerable.Repeat("엣지",500)):"엣지"}},font=new FontRule{common=true,count=3072,fontSize=24,glyphs=new GlyphRule[0]}};
        var manifest=new SemanticManifest{version="v261001",builtAt="test",buildId=new string('b',24),fontHash=Storage.Hash(font),sourceLengths=new Dictionary<string,long>{{"0000.bin",b0.Length},{"0001.bin",b1.Length}},locations=new[]{new ResourceLocation{package=movable?"0003.pkg":"0627.pkg",outerId=0,member=0,recipe=recipe.key}}};
        string data=Path.Combine(root,"test.data");using(var z=ZipFile.Open(data,ZipArchiveMode.Create)){Add(z,"manifest.json",Storage.Utf8.GetBytes(Storage.Json(manifest)));Add(z,"translations.jsonl",Storage.Utf8.GetBytes(Storage.Json(recipe)+"\n"));Add(z,"NotoSansKR-Medium.ttf",font);}
        return new Fixture{Root=root,Data=data,Hash=Storage.HashFile(data),Before0=b0,Before1=b1};
    }
    private static void Run(Fixture f,Action<int> after=null,bool verify=false,List<int> progress=null)
    { new PatchEngine(v=>{if(progress!=null)progress.Add(v);}){AfterWrite=after}.Run(f.Root,f.Data,f.Hash,verify,Path.Combine(f.Root,"verify")); }
    private static void Unchanged(Fixture f)
    {Check(File.ReadAllBytes(Path.Combine(f.Root,"0000.bin")).SequenceEqual(f.Before0),"Original BIN0 restored");Check(File.ReadAllBytes(Path.Combine(f.Root,"0001.bin")).SequenceEqual(f.Before1),"Unrelated BIN1 preserved");Check(File.ReadAllBytes(Path.Combine(f.Root,"StarOceanTheLastHope.exe")).SequenceEqual(executable),"EXE untouched");}
    private static void InstallTransactions()
    {
        var normal=Make("normal");Run(normal,null,true);Unchanged(normal);Check(!File.Exists(Path.Combine(normal.Root,PatchEngine.RecordName)),"Verify-only does not install");
        var progress=new List<int>();Run(normal,null,false,progress);Check(progress.Last()==10000&&progress.Zip(progress.Skip(1),(a,b)=>a<=b).All(v=>v),"Install progress");
        byte[] installed=File.ReadAllBytes(Path.Combine(normal.Root,"0000.bin"));using(var s=new MemoryStream(installed)){var o=new OuterTable(BinaryData.Slice(installed,0,OuterTable.Size));var cap=new Kcap(s,o.Offset(0),o.Length(0));var actual=GameText.Records(Slz.Decode(cap.ReadPacked(s,o.Offset(0),0)));Check(actual[123].SequenceEqual(GameText.Encode("엣지",mapping)),"Translation installed");Check(actual[456].SequenceEqual(new byte[]{30,0}),"Untranslated record preserved");Check(cap.ReadPacked(s,o.Offset(0),1).SequenceEqual(normal.Before0.Skip(OuterTable.Size+512).Take(128)),"Unrelated member preserved");}
        Run(normal);Check(File.ReadAllBytes(Path.Combine(normal.Root,"0000.bin")).SequenceEqual(installed),"Second installation verifies without writing");
        string record=Path.Combine(normal.Root,PatchEngine.RecordName);var journal=Storage.Json<Journal>(File.ReadAllText(record));journal.state="installing";Storage.AtomicJson(record,journal);Run(normal);Check(File.ReadAllBytes(Path.Combine(normal.Root,"0000.bin")).SequenceEqual(installed),"Interrupted journal recovery then reinstall");
        using(var file=File.Open(Path.Combine(normal.Root,"0000.bin"),FileMode.Open,FileAccess.Write)){file.Position=OuterTable.Size+64+145;file.WriteByte(77);}Reject(()=>Run(normal),"Changed target detected on second run");
        var failing=Make("rollback",true);Reject(()=>Run(failing,n=>{throw new IOException("Injected write failure");}),"Write fault propagated");Unchanged(failing);Check(!File.Exists(Path.Combine(failing.Root,"wininet.dll")),"No DLL after rollback");Check(Storage.Json<Journal>(File.ReadAllText(Path.Combine(failing.Root,PatchEngine.RecordName))).state=="rolled-back","Rollback recorded");Run(failing);Check(new FileInfo(Path.Combine(failing.Root,"0000.bin")).Length>failing.Before0.Length,"Static package relocated on overflow");
        var tight=Make("fixed-slot",true,false);Reject(()=>Run(tight),"Audio package capacity failure is pre-write");Unchanged(tight);
        var wrong=Make("wrong-source");using(var file=File.Open(Path.Combine(wrong.Root,"0000.bin"),FileMode.Open,FileAccess.Write)){file.Position=OuterTable.Size+64+144;file.WriteByte(80);}wrong.Before0=File.ReadAllBytes(Path.Combine(wrong.Root,"0000.bin"));Reject(()=>Run(wrong),"Wrong target member rejected");Unchanged(wrong);
        var unrelated=Make("unrelated-byte");unrelated.Before1[222]=67;File.WriteAllBytes(Path.Combine(unrelated.Root,"0001.bin"),unrelated.Before1);Run(unrelated);Check(File.ReadAllBytes(Path.Combine(unrelated.Root,"0001.bin")).SequenceEqual(unrelated.Before1),"No whole-file hash requirement");
        var locked=Make("exclusive-lock");using(var file=File.Open(Path.Combine(locked.Root,"0000.bin"),FileMode.Open,FileAccess.Read,FileShare.Read))Reject(()=>Run(locked),"Exclusive write access required");Unchanged(locked);
        var corrupt=Make("data-hash");corrupt.Hash=new string('0',64);Reject(()=>Run(corrupt),"Package hash mismatch rejected");Unchanged(corrupt);
        var mod=Make("other-mod");File.WriteAllText(Path.Combine(mod.Root,"wininet.dll"),"user mod");Reject(()=>Run(mod),"Existing mod preserved");Unchanged(mod);Check(File.ReadAllText(Path.Combine(mod.Root,"wininet.dll"))=="user mod","Other DLL untouched");
    }
}
