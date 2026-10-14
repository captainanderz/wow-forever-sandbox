# Builds the standalone Forever Sandbox product folder: self-contained launcher, server and tools (no .NET install needed).
# It never ships Blizzard files: the client snapshot in <Destination>\client is made locally by ForeverSnapshot. Existing
# sandbox data (characters, account data, locations, settings) and sandbox.json in the product folder are kept.
param([string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'))
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = [IO.Path]::GetFullPath($Destination)
$staging = Join-Path $RepoRoot 'artifacts/publish-staging'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

function Publish([string]$Project, [string]$Output, [switch]$SingleFile) {
    $project = Join-Path $RepoRoot $Project
    $artifacts = Join-Path $staging ('obj-' + [IO.Path]::GetFileNameWithoutExtension($project))
    Restore-Project $project -ArtifactsPath $artifacts
    $extra = @(if ($SingleFile) { '--property:PublishSingleFile=true'; '--property:IncludeNativeLibrariesForSelfExtract=true' })
    Invoke-Dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --no-restore --property:Platform=x64 "--property:ArtifactsPath=$artifacts" --output (Join-Path $staging $Output) --verbosity minimal @extra
}

Publish 'src/ForeverSandbox.Server/ForeverSandbox.Server.csproj' 'server'
Publish 'src/ForeverSandbox/ForeverSandbox.csproj' 'launcher' -SingleFile
Publish 'src/ForeverSnapshot/ForeverSnapshot.csproj' 'tools' -SingleFile
Publish 'src/ForeverCertificates/ForeverCertificates.csproj' 'tools' -SingleFile

New-Item -ItemType Directory -Path $root -Force | Out-Null
foreach ($folder in 'server', 'tools') {
    $target = Join-Path $root $folder
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $staging $folder) -Destination $target -Recurse
    Get-ChildItem -LiteralPath $target -Filter *.pdb -Recurse | Remove-Item
}
Copy-Item -LiteralPath (Join-Path $staging 'launcher/ForeverSandbox.exe') -Destination (Join-Path $root 'ForeverSandbox.exe') -Force

# Shipped data (the TACT key hotfixes) is refreshed; the player's own files in sandbox-data\ are never touched.
$data = Join-Path $root 'sandbox-data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $RepoRoot 'data/hotfixes.json') -Destination $data -Force

# With a snapshot in place: the addons go into its AddOns folder and the sandbox's own client files (the .walls models) into
# its storage (reversible: ForeverSandbox snapshot walls remove --to client).
if (Test-Path -LiteralPath (Join-Path $root 'client\_classic_beta_\WowB.exe')) {
    $addonTarget = Join-Path $root 'client\_classic_beta_\Interface\AddOns'
    New-Item -ItemType Directory -Path $addonTarget -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'addons') -Directory | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $addonTarget -Recurse -Force }
    & (Join-Path $root 'tools\ForeverSnapshot.exe') walls install --to (Join-Path $root 'client')
    if ($LASTEXITCODE -ne 0) { throw 'Installing the wall models into the snapshot failed.' }
}
$settings = Join-Path $root 'sandbox.json'
if (-not (Test-Path -LiteralPath $settings)) { [ordered]@{ clientRoot = 'client' } | ConvertTo-Json | Set-Content -LiteralPath $settings }
@"
Forever Sandbox (WoW Forever beta 1.60.1.70338)

Play:      double-click ForeverSandbox.exe, log in with admin / admin. Close the game to stop the sandbox.
Snapshot:  client\ is a frozen copy of the 1.60.1.70338 client made from your own Battle.net install
           (ForeverSandbox snapshot create --from "C:\Blizzard Games\World of Warcraft" --to "<this folder>\client").
           Battle.net never updates it. Check it with: ForeverSandbox snapshot verify "<this folder>\client"
Data:      sandbox-data\ (characters, account data, saved locations, settings). Settings: sandbox.json.
Commands:  .help in the game chat lists the sandbox commands (.fly, .speed, .tele, .loc, .walls, ...).
Fog:       /fog off in the game removes all fog, /fog on brings it back. The map and scene fog part is a change in the
           running game's memory only (nothing on disk); "fullFogOff": false in sandbox.json turns that part off.
Walls:     /walls in the game shows impassable terrain (the map's impass flag) as in-world walls: the sandbox's own
           translucent yellow/red wall models (added to client\ by ForeverSnapshot walls install; walls remove undoes it),
           /walls glass faint|light|medium|strong changes their tint, /walls look <yellow> <red> uses game force fields
           instead, /walls overlay draws them over the game window, /walls off hides them.
Never share client\ - it holds Blizzard's game files.
"@ | Set-Content -LiteralPath (Join-Path $root 'README.txt')
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Forever Sandbox published to $root"
