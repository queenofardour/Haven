param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$pluginProject = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\Haven\Haven.csproj') -Raw)
$version = [version]$pluginProject.Project.PropertyGroup.Version
$versionFolder = "v$version"
Push-Location $projectRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
    $artifactDirectory = Join-Path $projectRoot 'artifacts'
    $versionDirectory = Join-Path $artifactDirectory $versionFolder
    $pluginDirectory = Join-Path $versionDirectory 'Haven'
    $relayDirectory = Join-Path $versionDirectory 'relay'
    New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $pluginDirectory 'Haven.dll'))) { throw 'Build the Release plugin before packaging.' }
    $manifest = Get-Content -LiteralPath (Join-Path $pluginDirectory 'Haven.json') -Raw | ConvertFrom-Json
    if ([version]$manifest.AssemblyVersion -ne [version]"$version.0") { throw 'Plugin manifest does not match the source version. Rebuild before packaging.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $pluginDirectory 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $pluginDirectory 'THIRD_PARTY_NOTICES.md') -Force
    & $dotnetCommand publish 'src\Haven.Relay\Haven.Relay.csproj' -c Release --no-restore -o $relayDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Relay publication failed.' }
    $pluginFiles = Get-ChildItem -LiteralPath $pluginDirectory -File
    Compress-Archive -LiteralPath $pluginFiles.FullName -DestinationPath (Join-Path $artifactDirectory "Haven-alpha-$version.zip") -Force
    $sourceArchive = Join-Path $artifactDirectory "Haven-source-$version.zip"
    $archiveStream = [System.IO.File]::Open($sourceArchive, [System.IO.FileMode]::Create)
    $zip = [System.IO.Compression.ZipArchive]::new($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $sourceFiles = & git -c core.quotepath=false ls-files --cached --others --exclude-standard
        if ($LASTEXITCODE -ne 0) { throw 'Source file inventory failed.' }
        foreach ($relativePath in $sourceFiles) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $projectRoot $relativePath), $relativePath.Replace('\', '/')) | Out-Null
        }
    } finally { $zip.Dispose(); $archiveStream.Dispose() }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $relayDirectory 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $relayDirectory 'THIRD_PARTY_NOTICES.md') -Force
    Write-Output "Created artifacts/Haven-alpha-$version.zip, Haven-source-$version.zip, and $versionFolder/relay/."
} finally { Pop-Location }
