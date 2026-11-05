param([string]$Url = 'http://localhost:9420')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
Push-Location $projectRoot
try {
    & $dotnetCommand run --project 'src\Haven.Relay\Haven.Relay.csproj' -c Release -- --urls $Url
    if ($LASTEXITCODE -ne 0) { throw 'Relay stopped with an error.' }
} finally { Pop-Location }
