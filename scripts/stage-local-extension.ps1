<#
.SYNOPSIS
    Builds the EMP Prep extension and copies it into Cove's local extensions directory.

.DESCRIPTION
    Builds the React/CSS bundles and the backend, then copies the build output into the directory
    Cove loads extensions from. Restart Cove afterwards to pick up the new build.
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$DestinationRoot = (Join-Path $env:LOCALAPPDATA "cove_personal_live\extensions")
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "src\EmpPrep\EmpPrep.csproj"

Write-Host "Building EMP Prep UI bundles..."
Push-Location $repoRoot
try {
    npm run build:ui
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build:ui failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host "Building EMP Prep ($Configuration)..."
dotnet build $projectPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$outputDir = Join-Path $repoRoot "src\EmpPrep\bin\$Configuration\net10.0"
$manifestPath = Join-Path $outputDir "extension.json"
if (-not (Test-Path $manifestPath)) {
    throw "Missing manifest at $manifestPath"
}

$manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($manifest.id)) {
    throw "Manifest at $manifestPath does not define an id"
}

New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
$targetDir = Join-Path $DestinationRoot $manifest.id

# Update generated files in place. Do not delete an existing extension directory: local
# development data and configuration may live alongside its binaries.
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item -Path (Join-Path $outputDir "*") -Destination $targetDir -Recurse -Force

Write-Host ("Staged {0} -> {1}" -f $manifest.id, $targetDir)
Write-Host "Restart Cove to reload the extension if it is already running."
