[CmdletBinding()]
param(
    [string[]]$ValheimInstalls = @(
        'D:\valheim-ref',
        'D:\dh-local\server'
    )
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($install in $ValheimInstalls) {
    $resolvedInstall = [System.IO.Path]::GetFullPath($install).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $serverExecutable = Join-Path $resolvedInstall 'valheim_server.exe'
    $serverManaged = Join-Path $resolvedInstall 'valheim_server_Data\Managed'
    $clientManaged = Join-Path $resolvedInstall 'valheim_Data\Managed'
    $isDedicated = (Test-Path -LiteralPath $serverExecutable -PathType Leaf) -or
        ((Test-Path -LiteralPath $serverManaged -PathType Container) -and
         -not (Test-Path -LiteralPath $clientManaged -PathType Container))
    $managedName = if ($isDedicated) {
        'valheim_server_Data\Managed'
    } else {
        'valheim_Data\Managed'
    }

    $assemblyPath = Join-Path $resolvedInstall "$managedName\assembly_valheim.dll"
    $utilsAssemblyPath = Join-Path $resolvedInstall "$managedName\assembly_utils.dll"
    $cecilPath = Join-Path $resolvedInstall 'BepInEx\core\Mono.Cecil.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        $failures.Add("Missing assembly: $assemblyPath")
        continue
    }
    if (-not (Test-Path -LiteralPath $cecilPath)) {
        $failures.Add("Missing Mono.Cecil: $cecilPath")
        continue
    }
    if (-not (Test-Path -LiteralPath $utilsAssemblyPath)) {
        $failures.Add("Missing assembly: $utilsAssemblyPath")
        continue
    }

    if (-not ('Mono.Cecil.AssemblyDefinition' -as [type])) {
        Add-Type -LiteralPath $cecilPath
    }

    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)
    try {
        $zdoMan = $assembly.MainModule.Types | Where-Object Name -EQ 'ZDOMan'
        $sendZdos = $zdoMan.Methods | Where-Object Name -EQ 'SendZDOs' | Select-Object -First 1
        $sendToPeers = $zdoMan.Methods | Where-Object Name -EQ 'SendZDOToPeers2' | Select-Object -First 1
        $budgetConstants = @($sendZdos.Body.Instructions | Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldc_I4 -and $_.Operand -eq 10240
        })
        $minimumConstants = @($sendZdos.Body.Instructions | Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldc_I4 -and $_.Operand -eq 2048
        })

        $steamSocket = $assembly.MainModule.Types | Where-Object Name -EQ 'ZSteamSocket'
        $requiredFields = @('m_con', 'm_sendQueue', 'm_totalSent')
        $missingFields = @($requiredFields | Where-Object { $_ -notin $steamSocket.Fields.Name })
        $sendQueued = $steamSocket.Methods | Where-Object Name -EQ 'SendQueuedPackages' | Select-Object -First 1
        $sendCalls = @($sendQueued.Body.Instructions | Where-Object {
            $_.OpCode.Code -in @([Mono.Cecil.Cil.Code]::Call, [Mono.Cecil.Cil.Code]::Callvirt)
        } | ForEach-Object { $_.Operand.FullName })
        $requiredSendCalls = @(
            'System.IntPtr System.Runtime.InteropServices.Marshal::AllocHGlobal(System.Int32)',
            'System.Void System.Runtime.InteropServices.Marshal::Copy(System.Byte[],System.Int32,System.IntPtr,System.Int32)',
            'System.Void System.Runtime.InteropServices.Marshal::FreeHGlobal(System.IntPtr)',
            'Steamworks.EResult Steamworks.SteamGameServerNetworkingSockets::SendMessageToConnection(Steamworks.HSteamNetConnection,System.IntPtr,System.UInt32,System.Int32,System.Int64&)'
        )
        $missingSendCalls = @($requiredSendCalls | Where-Object { $_ -notin $sendCalls })

        $routedRpc = $assembly.MainModule.Types | Where-Object Name -EQ 'ZRoutedRpc'
        $routedRegisterPackage = @($routedRpc.Methods | Where-Object {
            $_.Name -eq 'Register' -and $_.HasGenericParameters -and $_.Parameters.Count -eq 2
        }).Count -gt 0
        $routedInvoke = @($routedRpc.Methods | Where-Object {
            $_.Name -eq 'InvokeRoutedRPC' -and $_.Parameters.Count -eq 2 -and
            $_.Parameters[0].ParameterType.FullName -eq 'System.String'
        }).Count -gt 0
        $character = $assembly.MainModule.Types | Where-Object Name -EQ 'Character'
        $characterMethods = @('GetAllCharacters', 'GetOwner', 'IsPlayer')
        $missingCharacterMethods = @($characterMethods | Where-Object { $_ -notin $character.Methods.Name })
        $player = $assembly.MainModule.Types | Where-Object Name -EQ 'Player'
        $playerTelemetryShape = $null -ne $player -and
            'm_localPlayer' -in $player.Fields.Name -and
            'IsTeleporting' -in $player.Methods.Name

        if ($null -eq $sendZdos) {
            $failures.Add("${resolvedInstall}: ZDOMan.SendZDOs missing")
        } elseif ($null -eq $sendToPeers -or $sendToPeers.Parameters.Count -ne 1 -or $sendToPeers.Parameters[0].ParameterType.FullName -ne 'System.Single') {
            $failures.Add("${resolvedInstall}: ZDOMan.SendZDOToPeers2(float) missing")
        } elseif ($budgetConstants.Count -ne 2) {
            $failures.Add("${resolvedInstall}: expected two 10240 guards, found $($budgetConstants.Count)")
        } elseif ($minimumConstants.Count -lt 1) {
            $failures.Add("${resolvedInstall}: 2048 minimum package guard missing")
        }
        if ($null -eq $steamSocket -or $missingFields.Count -gt 0) {
            $failures.Add("${resolvedInstall}: missing ZSteamSocket fields: $($missingFields -join ', ')")
        }
        if ($isDedicated -and ($null -eq $sendQueued -or $missingSendCalls.Count -gt 0)) {
            $failures.Add("${resolvedInstall}: pinned-send patch shape changed")
        }
        if ($null -eq $routedRpc -or -not $routedRegisterPackage -or -not $routedInvoke) {
            $failures.Add("${resolvedInstall}: client telemetry routed-RPC shape changed")
        }
        if ($null -eq $character -or $missingCharacterMethods.Count -gt 0 -or -not $playerTelemetryShape) {
            $failures.Add("${resolvedInstall}: FPS/ownership telemetry game shape changed")
        }

        if ($failures.Count -eq 0 -or -not ($failures[-1] -like "$resolvedInstall*")) {
            Write-Host "Verified: $resolvedInstall (scheduler, ZDO guards 2/2, Steam and telemetry APIs present)"
        }
    } finally {
        $assembly.Dispose()
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'All requested Valheim assemblies are compatible with this AdaptiveNet build.'
