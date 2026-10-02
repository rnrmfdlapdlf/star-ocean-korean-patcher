param([switch]$Prepare,[string]$DevelopmentRoot=(Split-Path -Parent $PSScriptRoot),[string]$Encoder=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\xdelta3-3.2.1\xdelta3-3.2.1-windows-x86_64\xdelta3.exe'))
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$project=$PSScriptRoot
$obj=Join-Path $project 'obj'
New-Item -ItemType Directory -Path $obj -Force | Out-Null
& $compiler /nologo /target:exe /platform:x64 ('/out:'+(Join-Path $obj 'BuildPackage.exe')) /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll (Join-Path $project 'src\Models.cs') (Join-Path $project 'src\DeltaTool.cs') (Join-Path $project 'src\BuildPackage.cs')
if($LASTEXITCODE -ne 0){throw 'BuildPackage compilation failed'}
if($Prepare){
  & (Join-Path $obj 'BuildPackage.exe') prepare $project $DevelopmentRoot $Encoder (Join-Path (Split-Path -Parent $project) 'work\xdelta_project_20261001\generation')
  if($LASTEXITCODE -ne 0){throw 'xdelta generation failed'}
}
& (Join-Path $obj 'BuildPackage.exe') data $project
if($LASTEXITCODE -ne 0){throw 'Data build failed'}
$files=@(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' -File | Where-Object Name -ne 'BuildPackage.cs' | ForEach-Object FullName)+@(Join-Path $project 'src\Generated\BuildInfo.cs')
& $compiler /nologo /target:winexe /platform:x64 /optimize+ ('/out:'+(Join-Path $project 'bin\Release\SO4KoreanPatcher.exe')) ('/win32manifest:'+(Join-Path $project 'src\app.manifest')) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $files
if($LASTEXITCODE -ne 0){throw 'Patcher compilation failed'}
& (Join-Path $obj 'BuildPackage.exe') zip $project
if($LASTEXITCODE -ne 0){throw 'Release ZIP failed'}
