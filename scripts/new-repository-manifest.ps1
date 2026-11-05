param(
    [string]$Repository = 'queenofardour/Haven',
    [string]$OutputDirectory = 'artifacts/repository'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Expected owner/repository.' }
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/Haven/Haven.csproj') -Raw)
$version = [version]$project.Project.PropertyGroup.Version
$versionFolder = "v$version"
$pluginDirectory = Join-Path $projectRoot "artifacts/$versionFolder/Haven"
$manifest = Get-Content -LiteralPath (Join-Path $pluginDirectory 'Haven.json') -Raw | ConvertFrom-Json -AsHashtable
if ([version]$manifest.AssemblyVersion -ne [version]"$version.0" -or $manifest.InternalName -ne 'Haven') { throw 'Build/version mismatch.' }
$zipPath = Join-Path $projectRoot "artifacts/Haven-alpha-$version.zip"
if (-not (Test-Path -LiteralPath $zipPath)) { throw 'Package the plugin first.' }
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entry = $archive.GetEntry('Haven.json')
    if ($null -eq $entry -or $null -eq $archive.GetEntry('Haven.dll')) { throw 'ZIP must contain Haven.dll and Haven.json at its root.' }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { $packaged = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($packaged.AssemblyVersion -ne $manifest.AssemblyVersion) { throw 'Packaged manifest does not match build.' }
} finally { $archive.Dispose() }
$download = "https://github.com/$Repository/releases/download/v$version/Haven-alpha-$version.zip"
$manifest.RepoUrl = "https://github.com/$Repository"
$manifest.DownloadLinkInstall = $download
$manifest.DownloadLinkUpdate = $download
$manifest.IsHide = $false
$manifest.IsTestingExclusive = $false
$manifest.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$destination = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
ConvertTo-Json -InputObject @($manifest) -Depth 12 | Set-Content -LiteralPath (Join-Path $destination 'pluginmaster.json') -Encoding utf8NoBOM
Write-Output "Repository entry: Haven $($manifest.AssemblyVersion) / Dalamud API $($manifest.DalamudApiLevel)"
