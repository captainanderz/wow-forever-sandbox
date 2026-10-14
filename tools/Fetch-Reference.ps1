# Downloads the pinned TrinityCore reference files the generators read (listed in reference.json) into tools\reference\ and
# checks each against its Git blob hash. Only needed to regenerate ForeverUpdateFields.g.cs or data\hotfixes.json.
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'reference.json') -Raw | ConvertFrom-Json
$destinationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'reference'))
$repository = $manifest.repository -replace '^https://github.com/', 'https://raw.githubusercontent.com/'
foreach ($file in $manifest.files) {
    $destination = [IO.Path]::GetFullPath((Join-Path $destinationRoot $file.path))
    if (-not $destination.StartsWith($destinationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes tools\reference: $($file.path)" }
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Invoke-WebRequest -Uri "$repository/$($manifest.commit)/$($file.path)" -OutFile $destination
    }
    $data = [IO.File]::ReadAllBytes($destination)
    $prefix = [Text.Encoding]::UTF8.GetBytes("blob $($data.Length)`0")
    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        $null = $sha1.TransformBlock($prefix, 0, $prefix.Length, $prefix, 0)
        $null = $sha1.TransformFinalBlock($data, 0, $data.Length)
        $actual = [BitConverter]::ToString($sha1.Hash).Replace('-', '').ToLowerInvariant()
    } finally { $sha1.Dispose() }
    if ($actual -ne $file.gitBlobSha1) { Remove-Item -LiteralPath $destination; throw "Hash mismatch: $($file.path)" }
    Write-Host "OK  $($file.path)"
}
