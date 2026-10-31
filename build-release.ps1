$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$storyOutput = Join-Path $PSScriptRoot 'dist/SekaiStorySkip'
dotnet publish SekaiStorySkip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $storyOutput
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $storyOutput
Write-Output (Join-Path $storyOutput 'SekaiStorySkip.exe')
