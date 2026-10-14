# Shared by the build scripts: a repository-local .NET CLI home and NuGet cache (.cache\), no telemetry.
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $RepoRoot '.cache/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $RepoRoot '.cache/nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $RepoRoot '.cache/nuget-http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:MSBuildEnableWorkloadResolver = 'false'
$NuGetConfig = Join-Path $RepoRoot 'NuGet.Config'

function Invoke-Dotnet {
    & dotnet @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed." }
}

# Every project lists win-x64 in RuntimeIdentifiers, so one locked restore covers builds and self-contained publishes.
function Restore-Project([string]$Project, [switch]$UpdateLock, [string]$ArtifactsPath) {
    $extra = @(if ($ArtifactsPath) { "--property:ArtifactsPath=$ArtifactsPath" })
    Invoke-Dotnet restore $Project --configfile $NuGetConfig "--property:RestoreLockedMode=$(-not $UpdateLock)" --property:Platform=x64 --verbosity minimal @extra
}
