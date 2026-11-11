# Builds dist\Burnaviour Chest Tracker.exe (one file, .NET included)
$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\src\ChestCounter\ChestCounter.csproj" -c Release -r win-x64 --self-contained true -m:1 `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=none `
  -o "$PSScriptRoot\dist"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "Built: $PSScriptRoot\dist\Burnaviour Chest Tracker.exe"
