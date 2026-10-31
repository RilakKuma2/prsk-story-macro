$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$storyOutput = Join-Path $PSScriptRoot 'dist/SekaiStorySkip'
if (Test-Path $storyOutput) { Remove-Item -LiteralPath $storyOutput -Recurse -Force }
dotnet publish SekaiStorySkip.csproj -p:SekaiPlatform=windows -p:EnableWindowsTargeting=true -p:ReleasePackage=true -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $storyOutput
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $storyOutput
$zip = Join-Path $PSScriptRoot 'dist/SekaiStorySkip-win-x64.zip'
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($storyOutput,$zip,[IO.Compression.CompressionLevel]::Optimal,$true)
Get-Item -LiteralPath $zip | Select-Object Name,Length
