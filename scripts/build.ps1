# -Quick leaves out the slowest checks (the Odyssey AI's strength tests) while iterating; releases use the full run.
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$Quick)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$pluginProject = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\Haven\Haven.csproj') -Raw)
$version = [version]$pluginProject.Project.PropertyGroup.Version
$versionFolder = "v$version"
$pluginOutput = Join-Path $projectRoot "artifacts\$versionFolder\Haven"
if ($Configuration -ne 'Release') { $pluginOutput = Join-Path $projectRoot "artifacts\$versionFolder\$Configuration\Haven" }
Push-Location $projectRoot
try {
    # Keep builds away from the original developer-plugin location used by the running client.
    & $dotnetCommand build 'src\Haven\Haven.csproj' -c $Configuration --nologo -p:RestoreLockedMode=true "-p:OutputPath=$pluginOutput\"
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    & $dotnetCommand build 'tests\Haven.Tests\Haven.Tests.csproj' -c $Configuration --nologo -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Relay/test build failed.' }
    # The UI preview compiles the panels' source files on its own, so a panel reaching into plugin-only code breaks it
    # unnoticed (0.58.9 to 0.60.0). It reads Dalamud from the local dev folder, which CI doesn't have.
    if (-not $env:GITHUB_ACTIONS -and (Test-Path 'tools\Haven.UiPreview\preview.csproj')) {
        & $dotnetCommand build 'tools\Haven.UiPreview\preview.csproj' -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'UI preview build failed: a panel uses code the preview does not compile.' }
    }
    # One argument array: Windows PowerShell mangles "--" next to a splatted array.
    $runArguments = @('run', '--project', 'tests\Haven.Tests\Haven.Tests.csproj', '-c', $Configuration, '--no-build')
    if ($Quick) { $runArguments += @('--', '--quick') }
    & $dotnetCommand @runArguments
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    Write-Output "Plugin assembly: $pluginOutput\Haven.dll"
} finally { Pop-Location }
