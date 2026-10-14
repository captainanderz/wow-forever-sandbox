# Builds and runs the protocol and data tests (tests\ForeverSandbox.Tests); results go to artifacts\test-results.json.
# -Client <folder> also checks a frozen client snapshot (terrain, client tables, impassable walls); the Battle.net install at
# C:\Blizzard Games\World of Warcraft is checked when present. Close the game first: a running client locks its storage.
param([string]$Client, [switch]$UpdateLock)
. (Join-Path $PSScriptRoot 'Common.ps1')
$project = Join-Path $RepoRoot 'tests/ForeverSandbox.Tests/ForeverSandbox.Tests.csproj'
Restore-Project $project -UpdateLock:$UpdateLock
Invoke-Dotnet build $project --configuration Release --no-restore --property:Platform=x64 --verbosity minimal
$env:FOREVER_SANDBOX_CLIENT = if ($Client) { [IO.Path]::GetFullPath($Client) } else { '' }
Push-Location -LiteralPath $RepoRoot
try { Invoke-Dotnet (Join-Path $RepoRoot 'artifacts/bin/ForeverSandbox.Tests/release/ForeverSandbox.Tests.dll') (Join-Path $RepoRoot 'artifacts/test-results.json') }
finally { Pop-Location }
