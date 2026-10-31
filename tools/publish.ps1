param(
    [ValidateSet('win-x64', 'win-x86', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$runtime = 'win-x64',
    [string]$output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (!$output) { $output = Join-Path $root "artifacts/$runtime" }
dotnet restore (Join-Path $root 'WallAligner.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet publish (Join-Path $root 'WallAligner.csproj') -c Release -r $runtime --no-restore --self-contained true -p:PublishSingleFile=false -o $output --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
