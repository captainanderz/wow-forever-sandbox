# Builds every project in ForeverSandbox.sln (Release, x64) into artifacts\. -UpdateLock refreshes the NuGet lock files.
param([switch]$UpdateLock)
. (Join-Path $PSScriptRoot 'Common.ps1')
$solution = Join-Path $RepoRoot 'ForeverSandbox.sln'
Restore-Project $solution -UpdateLock:$UpdateLock
Invoke-Dotnet build $solution --configuration Release --no-restore --property:Platform=x64 --verbosity minimal
