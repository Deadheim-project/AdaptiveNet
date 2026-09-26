[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$DllPath,

    [string]$ConfigPath,

    [string]$ReadmePath,

    [string]$OutputDirectory,

    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-A-Za-z0-9.]+)?$')]
    [string]$Version = '0.3.2',

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-AdaptiveNetAssembly {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$SemanticVersion
    )

    $identity = [Reflection.AssemblyName]::GetAssemblyName($Path)
    $coreVersion = ($SemanticVersion -split '-', 2)[0]
    $expected = [Version]($coreVersion + '.0')
    if ($identity.Name -ne 'AdaptiveNet' -or $identity.Version -ne $expected) {
        throw "DLL nao corresponde ao pacote: nome=$($identity.Name), versao=$($identity.Version), esperado=AdaptiveNet $expected"
    }
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DllPath) {
    $DllPath = Join-Path $projectRoot 'bin\Release\net48\AdaptiveNet.dll'
}
if (-not $ConfigPath) {
    $ConfigPath = Join-Path $projectRoot 'config\Detalhes.AdaptiveNet.cfg'
}
if (-not $ReadmePath) {
    $ReadmePath = Join-Path $projectRoot 'README.md'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot 'dist'
}

$comparePath = Join-Path $PSScriptRoot 'Compare-Telemetry.ps1'
foreach ($inputFile in @($DllPath, $ConfigPath, $ReadmePath, (Join-Path $PSScriptRoot 'Install-PrivateServer.ps1'), $comparePath)) {
    if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) {
        throw "Arquivo obrigatorio nao encontrado: $inputFile"
    }
}

$sourceDll = (Resolve-Path -LiteralPath $DllPath).ProviderPath
$sourceConfig = (Resolve-Path -LiteralPath $ConfigPath).ProviderPath
$sourceReadme = (Resolve-Path -LiteralPath $ReadmePath).ProviderPath
$sourceInstaller = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Install-PrivateServer.ps1')).ProviderPath
$sourceCompare = (Resolve-Path -LiteralPath $comparePath).ProviderPath
$sourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -File -Filter '*.cs'
    Get-Item -LiteralPath (Join-Path $projectRoot 'AdaptiveNet.csproj')
)
$newestSource = $sourceFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ((Get-Item -LiteralPath $sourceDll).LastWriteTimeUtc -lt $newestSource.LastWriteTimeUtc) {
    throw "AdaptiveNet.dll esta mais antigo que $($newestSource.FullName). Compile antes de empacotar."
}
Assert-AdaptiveNetAssembly -Path $sourceDll -SemanticVersion $Version
if (-not (Select-String -LiteralPath $sourceConfig -SimpleMatch "## AdaptiveNet $Version" -Quiet)) {
    throw "Preset de configuracao nao declara AdaptiveNet ${Version}: $sourceConfig"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "AdaptiveNet-deadheim-$Version"
$destinationZip = Join-Path $outputRoot ($packageName + '.zip')
$destinationChecksum = $destinationZip + '.sha256'
$launcherPackageName = "AdaptiveNet-launcher-$Version"
$launcherZip = Join-Path $outputRoot ($launcherPackageName + '.zip')
$launcherChecksum = $launcherZip + '.sha256'

foreach ($outputFile in @($destinationZip, $launcherZip)) {
    if ((Test-Path -LiteralPath $outputFile -PathType Leaf) -and -not $Force) {
        throw "O pacote ja existe: $outputFile. Use -Force para substituir somente os pacotes AdaptiveNet."
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$stagingRoot = Join-Path $tempRoot ('AdaptiveNetPackage-' + [Guid]::NewGuid().ToString('N'))
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
if (-not $stagingRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($stagingRoot)).StartsWith('AdaptiveNetPackage-', [StringComparison]::Ordinal)) {
    throw "Diretorio temporario recusado por seguranca: $stagingRoot"
}

if (-not $PSCmdlet.ShouldProcess($outputRoot, 'Criar pacotes privados do servidor e do launcher')) {
    return
}

try {
    $packageRoot = Join-Path $stagingRoot $packageName
    $packagePluginDirectory = Join-Path $packageRoot 'BepInEx\plugins\AdaptiveNet'
    $packageConfigDirectory = Join-Path $packageRoot 'BepInEx\config'
    New-Item -ItemType Directory -Path $packagePluginDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $packageConfigDirectory -Force | Out-Null

    Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $packagePluginDirectory 'AdaptiveNet.dll')
    Copy-Item -LiteralPath $sourceConfig -Destination (Join-Path $packageConfigDirectory 'Detalhes.AdaptiveNet.cfg')
    Copy-Item -LiteralPath $sourceReadme -Destination (Join-Path $packageRoot 'README.md')
    Copy-Item -LiteralPath $sourceInstaller -Destination (Join-Path $packageRoot 'Install-PrivateServer.ps1')
    Copy-Item -LiteralPath $sourceCompare -Destination (Join-Path $packageRoot 'Compare-Telemetry.ps1')

    $launcherRoot = Join-Path $stagingRoot $launcherPackageName
    $launcherConfigDirectory = Join-Path $launcherRoot 'config'
    New-Item -ItemType Directory -Path $launcherConfigDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $launcherRoot 'AdaptiveNet.dll')
    Copy-Item -LiteralPath $sourceConfig -Destination (Join-Path $launcherConfigDirectory 'Detalhes.AdaptiveNet.cfg')

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $temporaryZip = Join-Path $stagingRoot ($packageName + '.zip')
    $temporaryLauncherZip = Join-Path $stagingRoot ($launcherPackageName + '.zip')
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $temporaryZip -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $launcherRoot '*') -DestinationPath $temporaryLauncherZip -CompressionLevel Optimal
    Move-Item -LiteralPath $temporaryZip -Destination $destinationZip -Force:$Force
    Move-Item -LiteralPath $temporaryLauncherZip -Destination $launcherZip -Force:$Force

    $hash = Get-FileHash -LiteralPath $destinationZip -Algorithm SHA256
    ($hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($destinationZip)) |
        Set-Content -LiteralPath $destinationChecksum -Encoding Ascii
    $launcherHash = Get-FileHash -LiteralPath $launcherZip -Algorithm SHA256
    ($launcherHash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($launcherZip)) |
        Set-Content -LiteralPath $launcherChecksum -Encoding Ascii

    Write-Host "Pacote servidor: $destinationZip"
    Write-Host "SHA-256 servidor: $($hash.Hash.ToLowerInvariant())"
    Write-Host "Pacote launcher: $launcherZip"
    Write-Host "SHA-256 launcher: $($launcherHash.Hash.ToLowerInvariant())"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        $resolvedStaging = (Resolve-Path -LiteralPath $stagingRoot).ProviderPath
        if ($resolvedStaging.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            ([IO.Path]::GetFileName($resolvedStaging)).StartsWith('AdaptiveNetPackage-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
        else {
            Write-Warning "Limpeza automatica recusada para caminho inesperado: $resolvedStaging"
        }
    }
}
