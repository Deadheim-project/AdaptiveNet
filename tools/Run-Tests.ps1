<#
.SYNOPSIS
Builds AdaptiveNet and runs every test suite.

.DESCRIPTION
The suites are console Exe projects that print "<name> tests passed: <n>" and throw on the
first failure. AssemblyGuardTests inspects bin\Release\net48\AdaptiveNet.dll, so the mod is
built first; it refuses to run against an assembly older than src\ rather than reporting on
stale IL.

WireTests targets net48 and needs the Valheim and BepInEx assemblies. Pass -SkipWireTests on
a machine without a Valheim install.
#>
[CmdletBinding()]
param(
    [string]$ValheimInstall = 'D:\valheim-ref',
    [switch]$SkipWireTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host 'Building AdaptiveNet (Release)...'
& dotnet build (Join-Path $repoRoot 'AdaptiveNet.csproj') -c Release --no-incremental `
    -p:ValheimInstall=$ValheimInstall -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "AdaptiveNet build failed ($LASTEXITCODE)." }

$suites = @('ControllerTests', 'SamplingTests', 'AssemblyGuardTests')
if (-not $SkipWireTests) { $suites += 'WireTests' }

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($suite in $suites) {
    $projectDir = Join-Path $repoRoot "tests\$suite"
    if (-not (Test-Path -LiteralPath $projectDir -PathType Container)) {
        $failures.Add("$suite : project directory not found")
        continue
    }

    Push-Location $projectDir
    try {
        & dotnet run -c Release -p:ValheimInstall=$ValheimInstall -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { $failures.Add("$suite : exit code $LASTEXITCODE") }
    } finally {
        Pop-Location
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host ''
Write-Host "All $($suites.Count) AdaptiveNet test suites passed."
