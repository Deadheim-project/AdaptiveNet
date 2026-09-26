[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaselineCsv,
    [Parameter(Mandatory)]
    [string]$AdaptiveCsv
)

$ErrorActionPreference = 'Stop'

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if ($Values.Count -eq 0) { return 0 }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Min($sorted.Count - 1, [Math]::Ceiling($sorted.Count * $Percentile) - 1))
    return $sorted[$index]
}

function Get-Summary {
    param([string]$Path, [string]$Name)
    $allRows = @(Import-Csv -LiteralPath ([System.IO.Path]::GetFullPath($Path)))
    if ($allRows.Count -eq 0) {
        throw "CSV vazio: $Path"
    }
    $requiredColumns = @('peers', 'network_sample_peers', 'ping_p95_ms', 'queue_max_ms', 'queued_bytes')
    $columns = @($allRows[0].PSObject.Properties.Name)
    $missingColumns = @($requiredColumns | Where-Object { $_ -notin $columns })
    if ($missingColumns.Count -gt 0) {
        throw "Schema incompativel em ${Path}: faltam $($missingColumns -join ', ')"
    }

    $rows = @($allRows | Where-Object {
        [int]$_.peers -gt 0 -and [int]$_.network_sample_peers -gt 0
    })
    if ($rows.Count -eq 0) {
        throw "No connected-peer samples in $Path"
    }

    $ping = [double[]]@($rows | ForEach-Object { [double]::Parse($_.ping_p95_ms, [Globalization.CultureInfo]::InvariantCulture) })
    $queue = [double[]]@($rows | ForEach-Object { [double]::Parse($_.queue_max_ms, [Globalization.CultureInfo]::InvariantCulture) })
    $peerCounts = [double[]]@($rows | ForEach-Object { [double]$_.peers })
    $queuedBytesPerPeer = [double[]]@($rows | ForEach-Object {
        [double]::Parse($_.queued_bytes, [Globalization.CultureInfo]::InvariantCulture) / [int]$_.network_sample_peers
    })

    [pscustomobject]@{
        Profile = $Name
        Samples = $rows.Count
        PeersMean = [Math]::Round(($peerCounts | Measure-Object -Average).Average, 2)
        PeerP95MeanMs = [Math]::Round(($ping | Measure-Object -Average).Average, 2)
        PingP95Ms = [Math]::Round((Get-Percentile $ping 0.95), 2)
        QueueMeanMs = [Math]::Round(($queue | Measure-Object -Average).Average, 2)
        QueueP95Ms = [Math]::Round((Get-Percentile $queue 0.95), 2)
        QueuedPerPeerMeanKiB = [Math]::Round((($queuedBytesPerPeer | Measure-Object -Average).Average / 1024), 2)
        QueuedPerPeerP95KiB = [Math]::Round(((Get-Percentile $queuedBytesPerPeer 0.95) / 1024), 2)
    }
}

$baseline = Get-Summary -Path $BaselineCsv -Name 'Baseline'
$adaptive = Get-Summary -Path $AdaptiveCsv -Name 'AdaptiveNet'
$baseline, $adaptive | Format-Table -AutoSize

Write-Host ''
Write-Host ('P95 ping delta:  {0:+0.00;-0.00;0.00} ms' -f ($adaptive.PingP95Ms - $baseline.PingP95Ms))
Write-Host ('P95 queue delta: {0:+0.00;-0.00;0.00} ms' -f ($adaptive.QueueP95Ms - $baseline.QueueP95Ms))
Write-Host ('P95 bytes/peer delta: {0:+0.00;-0.00;0.00} KiB' -f ($adaptive.QueuedPerPeerP95KiB - $baseline.QueuedPerPeerP95KiB))

$largerPeers = [Math]::Max($baseline.PeersMean, $adaptive.PeersMean)
if ($largerPeers -gt 0 -and [Math]::Abs($adaptive.PeersMean - $baseline.PeersMean) / $largerPeers -gt 0.1) {
    Write-Warning 'A media de jogadores difere mais de 10%; trate o A/B como inconclusivo e repita com populacao/carga comparaveis.'
}
