param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $projectDir)
$projectPath = Join-Path $projectDir "CommunismMode.csproj"
$buildDir = Join-Path $projectDir "bin\$Configuration\netcoreapp9.0"
$manifestPath = Join-Path $projectDir "mod_manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Encoding utf8 | ConvertFrom-Json
$modId = [string]$manifest.id
$version = [string]$manifest.version
$modsDir = Join-Path $repoRoot "mods\$modId"
$stageRoot = Join-Path $repoRoot "_publish\${modId}_build_ready"
$payloadDir = Join-Path $stageRoot $modId
$zipPath = Join-Path $repoRoot "_publish\CommunismMode-$version.zip"

$running = Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue
if ($running) {
    throw "SlayTheSpire2.exe is running. Close the game before deploying the mod."
}

dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

foreach ($targetDir in @($modsDir, $payloadDir)) {
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $targetDir "mod_manifest.json") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "communismmode.dll") -Destination (Join-Path $targetDir "communismmode.dll") -Force
    Copy-Item -LiteralPath (Join-Path $projectDir "README.md") -Destination (Join-Path $targetDir "README.md") -Force
    Copy-Item -LiteralPath (Join-Path $projectDir "LICENSE") -Destination (Join-Path $targetDir "LICENSE") -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipPath) | Out-Null
Compress-Archive -LiteralPath $payloadDir -DestinationPath $zipPath

Write-Host "Communism Mode deployed to $modsDir"
Write-Host "Release ZIP created at $zipPath"
