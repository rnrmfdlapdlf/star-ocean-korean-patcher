param([switch]$Prepare,[switch]$PrepareSteamCheats,[switch]$PrepareXboxCheats,[string]$Python='python',[string]$DevelopmentRoot=(Split-Path -Parent $PSScriptRoot),[string]$Encoder=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\xdelta3-3.2.1\xdelta3-3.2.1-windows-x86_64\xdelta3.exe'),[string]$Reference,[string]$ValidationWork)
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$project=$PSScriptRoot
$obj=Join-Path $project 'obj'
$pendingPath=$null
$currentPath=Join-Path $DevelopmentRoot 'work\current_pc_reference.json'
if(Test-Path -LiteralPath $currentPath){
  $current=Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json
  if($current.pendingTranslations){
    $pendingPath=Join-Path $DevelopmentRoot $current.pendingTranslations
    & $Python -B (Join-Path $DevelopmentRoot 'tools\refresh_pending_translations.py') prepare $DevelopmentRoot $pendingPath
    if($LASTEXITCODE -ne 0){throw 'Pending translation reference preparation failed'}
    $pending=Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json
    $pendingReference=Join-Path $DevelopmentRoot $pending.target_reference
    if($Reference -and [IO.Path]::GetFullPath($Reference) -ne [IO.Path]::GetFullPath($pendingReference)){throw 'The selected reference does not include the pending translation corrections.'}
    $Reference=$pendingReference
    if($pending.state -ne 'delta_ready'){$Prepare=$true}
  }
}
New-Item -ItemType Directory -Path $obj -Force | Out-Null
& $compiler /nologo /target:exe /platform:x64 ('/out:'+(Join-Path $obj 'BuildPackage.exe')) /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll (Join-Path $project 'src\Models.cs') (Join-Path $project 'src\DeltaTool.cs') (Join-Path $project 'src\BuildPackage.cs')
if($LASTEXITCODE -ne 0){throw 'BuildPackage compilation failed'}
if($Prepare){
  if(!$ValidationWork){$ValidationWork=Join-Path (Split-Path -Parent $project) 'work\xdelta_project_20261001\generation'}
  $prepareArgs=@('prepare',$project,$DevelopmentRoot,$Encoder,$ValidationWork)
  if($Reference){$prepareArgs+=$Reference}
  & (Join-Path $obj 'BuildPackage.exe') $prepareArgs
  if($LASTEXITCODE -ne 0){throw 'xdelta generation failed'}
  if($pendingPath){
    & $Python -B (Join-Path $DevelopmentRoot 'tools\refresh_pending_translations.py') mark-ready $DevelopmentRoot $pendingPath
    if($LASTEXITCODE -ne 0){throw 'Pending translation delta verification failed'}
  }
}
if($PrepareSteamCheats){
  & $Python (Join-Path $project 'tools\build_steam_cheats.py')
  if($LASTEXITCODE -ne 0){throw 'Steam cheat delta generation failed'}
}
if($PrepareXboxCheats){
  & $Python -B (Join-Path $project 'tools\build_xbox_cheats.py')
  if($LASTEXITCODE -ne 0){throw 'Xbox cheat delta generation failed'}
}
& (Join-Path $obj 'BuildPackage.exe') data $project
if($LASTEXITCODE -ne 0){throw 'Data build failed'}
$xboxAssets=Join-Path $project 'Assets\Xbox360'
$xboxManifest=Join-Path $xboxAssets 'manifest.json'
if(!(Test-Path -LiteralPath $xboxManifest)){throw 'Xbox360 assets are missing. Run build.ps1 -PrepareXboxCheats with the retained development inputs.'}
$xboxHash=(Get-FileHash -LiteralPath $xboxManifest -Algorithm SHA256).Hash.ToLowerInvariant()
$utf8=New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $project 'src\Generated\XboxBuildInfo.cs'), ('namespace SO4KoreanPatcher { internal static class XboxBuildInfo { internal const string ManifestHash = "'+$xboxHash+'"; } }'), $utf8)
$xboxRelease=Join-Path $project 'bin\Release\Xbox360'
foreach($asset in Get-ChildItem -LiteralPath $xboxAssets -Recurse -File){
  $relative=$asset.FullName.Substring($xboxAssets.Length+1)
  $target=Join-Path $xboxRelease $relative
  New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
  Copy-Item -LiteralPath $asset.FullName -Destination $target -Force
}
$files=@(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' -File | Where-Object Name -ne 'BuildPackage.cs' | ForEach-Object FullName)+@(Get-ChildItem -LiteralPath (Join-Path $project 'src\Generated') -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler /nologo /target:winexe /platform:x64 /optimize+ ('/out:'+(Join-Path $project 'bin\Release\SO4KoreanPatcher.exe')) ('/win32manifest:'+(Join-Path $project 'src\app.manifest')) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $files
if($LASTEXITCODE -ne 0){throw 'Patcher compilation failed'}
& (Join-Path $obj 'BuildPackage.exe') zip $project
if($LASTEXITCODE -ne 0){throw 'Release ZIP failed'}
