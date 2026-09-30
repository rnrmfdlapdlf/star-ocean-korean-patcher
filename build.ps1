$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    # The C# build tool stamps the Korean date and packages only translation rules and the TTF.
    dotnet build SO4KoreanPatcher.csproj -c Release --ignore-failed-sources -p:CreateReleaseZip=true
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }
