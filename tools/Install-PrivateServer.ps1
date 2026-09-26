[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,

    [string]$DllPath,

    [string]$ConfigPath,

    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$ExpectedVersion = '0.3.2',

    [switch]$ReplaceConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label nao encontrado: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

function Find-FirstExistingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Candidates,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath
        }
    }

    throw "$Label nao encontrado. Locais verificados:`n$($Candidates -join "`n")"
}

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pathFull = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Destino fora da raiz validada do servidor: $pathFull"
    }
}

function Assert-AdaptiveNetAssembly {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Version
    )

    try {
        $identity = [Reflection.AssemblyName]::GetAssemblyName($Path)
    }
    catch {
        throw "DLL .NET invalido: $Path ($($_.Exception.Message))"
    }
    $expected = [Version]($Version + '.0')
    if ($identity.Name -ne 'AdaptiveNet' -or $identity.Version -ne $expected) {
        throw "DLL AdaptiveNet inesperado: nome=$($identity.Name), versao=$($identity.Version), esperado=AdaptiveNet $expected"
    }
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Container)) {
    throw "Diretorio do servidor nao encontrado: $ServerPath"
}

$serverRoot = (Resolve-Path -LiteralPath $ServerPath).ProviderPath.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$serverExecutable = Join-Path $serverRoot 'valheim_server.exe'
$bepInExAssembly = Join-Path $serverRoot 'BepInEx\core\BepInEx.dll'

Resolve-ExistingFile -Path $serverExecutable -Label 'valheim_server.exe' | Out-Null
Resolve-ExistingFile -Path $bepInExAssembly -Label 'BepInEx 5' | Out-Null

$runningServer = Get-Process -Name 'valheim_server' -ErrorAction SilentlyContinue
if ($runningServer) {
    throw 'Ha um processo valheim_server ativo. Pare o servidor antes de instalar ou atualizar AdaptiveNet.'
}

if ($DllPath) {
    $sourceDll = Resolve-ExistingFile -Path $DllPath -Label 'AdaptiveNet.dll'
}
else {
    $sourceDll = Find-FirstExistingFile -Label 'AdaptiveNet.dll' -Candidates @(
        (Join-Path $PSScriptRoot '..\bin\Release\net48\AdaptiveNet.dll'),
        (Join-Path $PSScriptRoot 'BepInEx\plugins\AdaptiveNet\AdaptiveNet.dll'),
        (Join-Path $PSScriptRoot '..\BepInEx\plugins\AdaptiveNet\AdaptiveNet.dll')
    )
}

Assert-AdaptiveNetAssembly -Path $sourceDll -Version $ExpectedVersion

if ($ConfigPath) {
    $sourceConfig = Resolve-ExistingFile -Path $ConfigPath -Label 'Detalhes.AdaptiveNet.cfg'
}
else {
    $sourceConfig = Find-FirstExistingFile -Label 'Detalhes.AdaptiveNet.cfg' -Candidates @(
        (Join-Path $PSScriptRoot '..\config\Detalhes.AdaptiveNet.cfg'),
        (Join-Path $PSScriptRoot 'BepInEx\config\Detalhes.AdaptiveNet.cfg'),
        (Join-Path $PSScriptRoot '..\BepInEx\config\Detalhes.AdaptiveNet.cfg')
    )
}

$pluginDirectory = Join-Path $serverRoot 'BepInEx\plugins\AdaptiveNet'
$destinationDll = Join-Path $pluginDirectory 'AdaptiveNet.dll'
$configDirectory = Join-Path $serverRoot 'BepInEx\config'
$destinationConfig = Join-Path $configDirectory 'Detalhes.AdaptiveNet.cfg'
$backupRoot = Join-Path $serverRoot ('BepInEx\AdaptiveNet-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))

Assert-PathInsideRoot -Root $serverRoot -Path $pluginDirectory
Assert-PathInsideRoot -Root $serverRoot -Path $destinationDll
Assert-PathInsideRoot -Root $serverRoot -Path $configDirectory
Assert-PathInsideRoot -Root $serverRoot -Path $destinationConfig
Assert-PathInsideRoot -Root $serverRoot -Path $backupRoot

$sameDllPath = [string]::Equals(
    [IO.Path]::GetFullPath($sourceDll),
    [IO.Path]::GetFullPath($destinationDll),
    [StringComparison]::OrdinalIgnoreCase)
$sameConfigPath = [string]::Equals(
    [IO.Path]::GetFullPath($sourceConfig),
    [IO.Path]::GetFullPath($destinationConfig),
    [StringComparison]::OrdinalIgnoreCase)
$needsDllBackup = (Test-Path -LiteralPath $destinationDll -PathType Leaf) -and -not $sameDllPath
$needsConfigInstall = -not (Test-Path -LiteralPath $destinationConfig -PathType Leaf) -or $ReplaceConfig
$needsConfigBackup = (Test-Path -LiteralPath $destinationConfig -PathType Leaf) -and $ReplaceConfig -and -not $sameConfigPath

if ($PSCmdlet.ShouldProcess($serverRoot, 'Instalar AdaptiveNet no servidor dedicado')) {
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

    if ($needsDllBackup -or $needsConfigBackup) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    }

    if ($needsDllBackup) {
        Copy-Item -LiteralPath $destinationDll -Destination (Join-Path $backupRoot 'AdaptiveNet.dll')
    }

    if (-not $sameDllPath) {
        Copy-Item -LiteralPath $sourceDll -Destination $destinationDll -Force
    }

    if ($needsConfigBackup) {
        Copy-Item -LiteralPath $destinationConfig -Destination (Join-Path $backupRoot 'Detalhes.AdaptiveNet.cfg')
    }

    if ($needsConfigInstall -and -not $sameConfigPath) {
        Copy-Item -LiteralPath $sourceConfig -Destination $destinationConfig -Force
    }
}

if ((Test-Path -LiteralPath $destinationConfig -PathType Leaf) -and -not $ReplaceConfig) {
    Write-Host "Configuracao existente preservada: $destinationConfig"
}

Write-Host "DLL de origem: $sourceDll"
Write-Host "Destino: $destinationDll"
if ($needsDllBackup -or $needsConfigBackup) {
    Write-Host "Backup: $backupRoot"
}
Write-Host 'Instalacao concluida. Inicie o servidor e confira BepInEx\LogOutput.log.'
