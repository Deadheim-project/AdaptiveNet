[CmdletBinding()]
param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet falhou ($LASTEXITCODE): $($Arguments -join ' ')"
    }
}

Push-Location $projectRoot
try {
    Invoke-DotNet @('clean', '.\AdaptiveNet.csproj', '-c', 'Release')
    # Every suite, not a hand-picked pair. This gate ran ControllerTests and WireTests only,
    # which left AssemblyGuardTests out of the release path -- the one suite that catches a
    # wrong Steamworks interface, the bug that shipped twice. Run-Tests.ps1 owns the list so
    # the gate cannot drift from it again; it builds Release itself.
    & (Join-Path $PSScriptRoot 'Run-Tests.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "Suites de teste falharam ($LASTEXITCODE)."
    }

    & (Join-Path $PSScriptRoot 'Verify-GameAssemblies.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "Verificacao das assemblies do jogo falhou ($LASTEXITCODE)."
    }

    $packageArguments = @{}
    if ($Force) { $packageArguments.Force = $true }
    & (Join-Path $PSScriptRoot 'New-PrivateServerPackage.ps1') @packageArguments
}
finally {
    Pop-Location
}
