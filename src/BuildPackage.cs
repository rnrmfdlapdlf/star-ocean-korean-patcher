using System;using System.IO;using System.IO.Compression;using System.Linq;using System.Collections.Generic;using SO4KoreanPatcher;
internal static class BuildPackage
{
 public sealed class CurrentReference { public string reference {get;set;} }
 public sealed class MessageCheck { public bool passed {get;set;} public string referenceManifestHash {get;set;} public int validatedMessages {get;set;} }
 static void Main(string[] args){try{if(args[0]=="prepare")Prepare(args);else if(args[0]=="data")Data(args[1]);else if(args[0]=="zip")Release(args[1]);else throw new ArgumentException();}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}}
 static void Prepare(string[] args)
 {
  string project=Path.GetFullPath(args[1]),dev=Path.GetFullPath(args[2]),encoder=Path.GetFullPath(args[3]),work=Path.GetFullPath(args[4]);Directory.CreateDirectory(work);
  string original=Path.Combine(dev,"backup_before_korean_patch/original_2026-07-11"),reference=Path.Combine(dev,"work/SO4KoreanPatcher_menu_crash_20261001/current-before-reference/reference");
  string refreshed=Path.Combine(dev,"work/xdelta_project_20261001/refresh/latest-reference"),manifestPath=Path.Combine(dev,"tools/SO4KoreanPatcher.Build/local-work/manifest.json");
  if(File.Exists(Path.Combine(refreshed,"manifest.json"))){reference=refreshed;manifestPath=Path.Combine(refreshed,"manifest.json");}
  string repaired=Path.Combine(dev,"work/fixes_20261002/latest-reference");
  if(File.Exists(Path.Combine(repaired,"manifest.json"))){reference=repaired;manifestPath=Path.Combine(repaired,"manifest.json");}
  string latest=Path.Combine(dev,"work/release_20261003/latest-reference");
  if(File.Exists(Path.Combine(latest,"manifest.json"))){reference=latest;manifestPath=Path.Combine(latest,"manifest.json");}
  string current=Path.Combine(dev,"work/current_pc_reference.json");
  if(File.Exists(current)){reference=Path.GetFullPath(Path.Combine(dev,Storage.Json<CurrentReference>(File.ReadAllText(current)).reference));manifestPath=Path.Combine(reference,"manifest.json");}
  if(args.Length>5){reference=Path.GetFullPath(args[5]);manifestPath=Path.Combine(reference,"manifest.json");}
  string validation=Path.Combine(reference,"message-validation.json");
  Storage.Require(File.Exists(validation),"원본 메시지·제어 인수 검증 기록이 필요합니다: "+reference);
  var checkedMessages=Storage.Json<MessageCheck>(File.ReadAllText(validation));
  Storage.Require(checkedMessages.passed&&checkedMessages.validatedMessages>0&&checkedMessages.referenceManifestHash==Storage.HashFile(manifestPath),"메시지 검증 기록과 기준본 목록이 다릅니다.");
  var plan=Storage.Json<Manifest>(File.ReadAllText(manifestPath));
  Storage.Require((plan.format=="source-copy-xor-v1"||plan.format=="xdelta-ranges-v1")&&plan.operations.Length>=1202,"정상 기준본 목록이 다릅니다.");
  string native=Path.Combine(project,"Assets/wininet.dll"),decoder=Path.Combine(Path.GetDirectoryName(encoder),"xdelta3decode.exe");
  Storage.Require(Storage.HashFile(native)=="bf13768f254c83d6b79a44bac75086953904a9f30500f4ba6eb66b11d00f98ac","정상 기준 DLL이 다릅니다.");
  var sources=plan.files.ToDictionary(f=>f.name,f=>File.OpenRead(Path.Combine(original,f.name)));
  try
  {
   for(int i=0;i<plan.operations.Length;i++)
   {
    var o=plan.operations[i];byte[] from=DeltaTool.Read(sources[o.sourceFile],o.sourceOffset,o.sourceLength);Storage.Require(Storage.Hash(from)==o.sourceHash,"기준 원본 불일치: "+o.label);
    string target=Path.Combine(reference,i.ToString("D4")+".bin");Storage.Require(new FileInfo(target).Length==o.targetLength&&Storage.HashFile(target)==o.targetHash,"정상 기준본 불일치: "+o.label);
    o.stageFile="deltas/"+i.ToString("D4")+".xdelta";string patch=Path.Combine(project,"Assets",o.stageFile);string source=Path.Combine(work,"source.bin"),decoded=Path.Combine(work,"decoded.bin");File.WriteAllBytes(source,from);
    if(!File.Exists(patch)||string.IsNullOrEmpty(o.deltaHash)||Storage.HashFile(patch)!=o.deltaHash)DeltaTool.Run(encoder,"-e -f -D -a -A -S none -s "+DeltaTool.Q(source)+" "+DeltaTool.Q(target)+" "+DeltaTool.Q(patch));
    DeltaTool.Run(decoder,"-d -f -D -R -s "+DeltaTool.Q(source)+" "+DeltaTool.Q(patch)+" "+DeltaTool.Q(decoded));
    Storage.Require(new FileInfo(decoded).Length==o.targetLength&&Storage.HashFile(decoded)==o.targetHash,"xdelta 역변환이 정상본과 다릅니다.");
    o.deltaHash=Storage.HashFile(patch);if(i%100==0)Console.WriteLine("Verified xdelta "+i+"/"+plan.operations.Length);
   }
  }
  finally{foreach(var f in sources.Values)f.Dispose();}
  plan.schema=3;plan.format="xdelta-ranges-v1";if(string.IsNullOrEmpty(plan.baseline))plan.baseline="user-verified legacy reference; bf13768f DLL";
  plan.nativeHash=Storage.HashFile(native);plan.decoderHash=null;
  foreach(var f in plan.files){f.sourceHash=null;f.targetHash=null;}
  File.WriteAllText(Path.Combine(project,"Assets/DeltaManifest.json"),Storage.Json(plan),Storage.Utf8);
  Storage.AtomicJson(Path.Combine(work,"delta-generation.json"),new{passed=true,operations=plan.operations.Length,sourceBytes=plan.operations.Sum(o=>o.sourceLength),targetBytes=plan.operations.Sum(o=>o.targetLength),deltaBytes=plan.operations.Sum(o=>new FileInfo(Path.Combine(project,"Assets",o.stageFile)).Length),nativeHash=plan.nativeHash,reference=plan.baseline,gameInstalled=false});
  Console.WriteLine("All "+plan.operations.Length+" xdelta results equal the reference.");
 }
 static DateTimeOffset Now(){return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"));}
 static void CopyUnchangedAware(string source,string target){if(File.Exists(target)&&Storage.HashFile(source)==Storage.HashFile(target))return;if(File.Exists(target))File.SetAttributes(target,File.GetAttributes(target)&~FileAttributes.ReadOnly);File.Copy(source,target,true);}
 static void Add(ZipArchive zip,string name,string file){zip.CreateEntryFromFile(file,name,CompressionLevel.Optimal);}
 static void Data(string project)
 {
  var plan=Storage.Json<Manifest>(File.ReadAllText(Path.Combine(project,"Assets/DeltaManifest.json")));var now=Now();plan.version="v"+now.ToString("yyMMdd");plan.builtAt=now.ToString("o");plan.author="by Gideon";
  string cheatsFile=Path.Combine(project,"Assets/SteamCheats.json");Storage.Require(File.Exists(cheatsFile),"Steam 치트 차분을 먼저 생성해 주세요: tools/build_steam_cheats.py");
  plan.steamCheats=Storage.Json<SteamCheatCatalog>(File.ReadAllText(cheatsFile));
  Storage.Require(plan.steamCheats.schema==1&&plan.steamCheats.baseHash==plan.exeHash&&plan.steamCheats.length>0&&plan.steamCheats.length<=128*1024*1024&&plan.steamCheats.variants.Length==8&&plan.steamCheats.variants.Select(v=>v.mask).OrderBy(x=>x).SequenceEqual(Enumerable.Range(0,8)),"Steam 치트 기준본이 다릅니다.");
  plan.buildId=Storage.Hash(Storage.Utf8.GetBytes(plan.version+plan.nativeHash+string.Concat(plan.operations.Select(o=>o.deltaHash))+Storage.Json(plan.steamCheats))).Substring(0,24);
  string release=Path.Combine(project,"bin/Release");Directory.CreateDirectory(release);string file=Path.Combine(release,"SO4KoreanPatch.data"),temp=file+".tmp";if(File.Exists(temp))File.Delete(temp);
  using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create))
  {
   using(var s=zip.CreateEntry("manifest.json",CompressionLevel.Optimal).Open()){var b=Storage.Utf8.GetBytes(Storage.Json(plan));s.Write(b,0,b.Length);}
   foreach(var o in plan.operations){string path=Path.Combine(project,"Assets",o.stageFile);Storage.Require(Storage.HashFile(path)==o.deltaHash,"차분이 변경되었습니다.");Add(zip,o.stageFile,path);}
   foreach(var v in plan.steamCheats.variants.Where(v=>v.mask!=0))
   {
    Storage.Require(v.forwardFile=="cheats/"+v.mask+".xdelta"&&v.reverseFile=="cheats/"+v.mask+"-original.xdelta","Steam 치트 차분 경로가 잘못되었습니다.");
    foreach(var pair in new[]{new[]{v.forwardFile,v.forwardHash},new[]{v.reverseFile,v.reverseHash}})
    {string path=Path.Combine(project,"Assets",pair[0]);Storage.Require(Storage.HashFile(path)==pair[1],"Steam 치트 차분이 변경되었습니다.");Add(zip,pair[0],path);}
   }
   foreach(string name in new[]{"wininet.dll"})Add(zip,name,Path.Combine(project,"Assets",name));
  }
  if(File.Exists(file))File.Delete(file);File.Move(temp,file);
  File.WriteAllText(Path.Combine(project,"src/Generated/BuildInfo.cs"),"namespace SO4KoreanPatcher { internal static class BuildInfo { internal const string DataHash = \""+Storage.HashFile(file)+"\", Id = \""+plan.buildId+"\", Version = \""+plan.version+"\"; } }",Storage.Utf8);
  foreach(string name in new[]{"README.md","LICENSE"}){string target=Path.Combine(release,name);CopyUnchangedAware(Path.Combine(project,name),target);}
  foreach(string folder in new[]{"docs","samples"})if(Directory.Exists(Path.Combine(project,folder)))foreach(string path in Directory.GetFiles(Path.Combine(project,folder),"*",SearchOption.AllDirectories)){string dest=Path.Combine(release,path.Substring(project.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(dest));CopyUnchangedAware(path,dest);}
  File.WriteAllText(Path.Combine(release,"SO4KoreanPatcher.exe.config"),"<?xml version=\"1.0\"?><configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.8\"/></startup><runtime><AppContextSwitchOverrides value=\"Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false\"/></runtime></configuration>",Storage.Utf8);
  Console.WriteLine("Built patch data "+plan.version+" "+new FileInfo(file).Length+" bytes");
 }
 static void Release(string project)
 {
  string version="v"+Now().ToString("yyMMdd"),file=Path.Combine(project,"bin/SO4KoreanPatcher.Xdelta-"+version+".zip"),release=Path.Combine(project,"bin/Release");
  if(File.Exists(file))File.Delete(file);using(var zip=ZipFile.Open(file,ZipArchiveMode.Create))foreach(string path in Directory.GetFiles(release,"*",SearchOption.AllDirectories)){if(Path.GetExtension(path)==".pdb")continue;Add(zip,path.Substring(release.Length+1).Replace('\\','/'),path);}
  Console.WriteLine(file+" ("+new FileInfo(file).Length+" bytes)");
 }
}
