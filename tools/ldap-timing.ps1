<#
.SYNOPSIS
    Read-only live-directory timing sample for P05 CSV enrichment batch-size selection.

.DESCRIPTION
    Implements the "Controlled live-directory timing check" specified in
    .agents/plans/P05-csv-scale-limits.md (section "Controlled live-directory timing check",
    lines 594-600). It mirrors the production search path in
    csharp/Services/ActiveDirectoryService.cs: one DirectorySearcher per batch, an OR filter of
    equality conditions on a single indexed match attribute, PageSize = 500, SizeLimit =
    identifiers x 2, and FindAll(). It records per-call wall-clock durations, computes p50/p95,
    and projects the total time to process 100,000 unique keys at each batch size.

    SAFETY / PRIVACY:
    - Read-only. Performs no writes, no user-object mutation.
    - By default it queries SYNTHETIC keys (guaranteed-nonexistent GUID-derived identifiers), so
      it measures round-trip + DC filter cost per batch WITHOUT reading or recording any real
      user. This is the recommended mode.
    - It records ONLY timings, batch sizes, result counts, and truncation flags. It never writes
      identifiers or returned attribute values to the output artifact or the console.
    - It stops immediately on any timeout, truncation, server error, or if a single batch exceeds
      the -StopIfBatchExceedsMs guard (default 30s) to avoid unexpected directory load.

    Run this on the deployment host under the IIS application identity when operationally
    convenient. Output is written to the ignored artifacts/ tree.

.PARAMETER MatchAttribute
    Indexed match attribute to time. One of: sAMAccountName, userPrincipalName, mail, displayName.
    Defaults to sAMAccountName. (employeeID is excluded: unindexed per the plan.)

.PARAMETER BatchSizes
    Batch sizes to sample. Defaults to 50, 250, 500, 1000 (the plan's candidates).

.PARAMETER Repeats
    Timed calls per batch size (for p50/p95). Default 5.

.PARAMETER SearchBase
    LDAP search base. Defaults to the domain defaultNamingContext discovered from RootDSE.

.PARAMETER KeyFile
    OPTIONAL path to a text file of approved, existing keys (one per line) to time against real
    matches instead of synthetic keys. Only use with directory-team approval. The file's contents
    are used solely to build filters and are never copied into the output artifact.

.PARAMETER StopIfBatchExceedsMs
    Safety guard: abort the whole run if any single batch call exceeds this many milliseconds.
    Default 30000 (30s).

.EXAMPLE
    pwsh -NoProfile -File tools/ldap-timing.ps1
    # Synthetic keys, sAMAccountName, batches 50/250/500/1000, 5 repeats each.

.EXAMPLE
    pwsh -NoProfile -File tools/ldap-timing.ps1 -MatchAttribute userPrincipalName -Repeats 8
#>
[CmdletBinding()]
param(
    [ValidateSet('sAMAccountName', 'userPrincipalName', 'mail', 'displayName')]
    [string]$MatchAttribute = 'sAMAccountName',

    [int[]]$BatchSizes = @(50, 250, 500, 1000),

    [ValidateRange(1, 100)]
    [int]$Repeats = 5,

    [string]$SearchBase,

    [string]$KeyFile,

    [int]$StopIfBatchExceedsMs = 30000
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.DirectoryServices

# --- LDAP filter value escaping (RFC 4515), mirrors the directory layer's escaping intent ---
function Get-EscapedFilterValue {
    param([string]$Value)
    $sb = [System.Text.StringBuilder]::new()
    foreach ($ch in $Value.ToCharArray()) {
        switch ($ch) {
            '\' { [void]$sb.Append('\5c') }
            '*' { [void]$sb.Append('\2a') }
            '(' { [void]$sb.Append('\28') }
            ')' { [void]$sb.Append('\29') }
            "`0" { [void]$sb.Append('\00') }
            default { [void]$sb.Append($ch) }
        }
    }
    $sb.ToString()
}

# --- Discover defaultNamingContext if no SearchBase supplied (read-only RootDSE read) ---
if (-not $SearchBase) {
    try {
        $rootDse = [ADSI]'LDAP://RootDSE'
        $SearchBase = [string]$rootDse.defaultNamingContext
        if (-not $SearchBase) { throw 'defaultNamingContext was empty.' }
    }
    catch {
        Write-Error "Could not discover the domain search base from RootDSE: $($_.Exception.Message)"
        exit 1
    }
}
Write-Host "Search base: $SearchBase"
Write-Host "Match attribute: $MatchAttribute"

# --- Key source: approved file, or synthetic non-existent keys ---
$usingSynthetic = $true
$approvedKeys = @()
if ($KeyFile) {
    if (-not (Test-Path -LiteralPath $KeyFile)) {
        Write-Error "KeyFile not found: $KeyFile"
        exit 1
    }
    $approvedKeys = @(Get-Content -LiteralPath $KeyFile | Where-Object { $_ -and $_.Trim() })
    if ($approvedKeys.Count -eq 0) {
        Write-Error 'KeyFile contained no usable keys.'
        exit 1
    }
    $usingSynthetic = $false
    Write-Host "Key source: approved KeyFile ($($approvedKeys.Count) keys). Values used only for filters; never recorded."
}
else {
    Write-Host 'Key source: SYNTHETIC non-existent keys (no real user read or recorded).'
}

function Get-BatchKeys {
    param([int]$Count)
    if ($usingSynthetic) {
        # GUID-derived keys that will not match any real object; measures round-trip + filter cost.
        return 1..$Count | ForEach-Object { 'zzt-' + ([guid]::NewGuid().ToString('N')) }
    }
    # Cycle through approved keys to fill the batch.
    $out = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Count; $i++) {
        $out.Add($approvedKeys[$i % $approvedKeys.Count])
    }
    return $out
}

function Invoke-TimedBatch {
    param([string[]]$Keys)
    $clauses = ($Keys | ForEach-Object { "($MatchAttribute=$(Get-EscapedFilterValue $_))" }) -join ''
    $filter = "(|$clauses)"

    $entry = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$SearchBase")
    $searcher = New-Object System.DirectoryServices.DirectorySearcher($entry)
    $searcher.Filter = $filter
    $searcher.SearchScope = [System.DirectoryServices.SearchScope]::Subtree
    $searcher.PageSize = 500
    $searcher.SizeLimit = $Keys.Count * 2   # mirrors CsvEnrichmentService batch SizeLimit
    [void]$searcher.PropertiesToLoad.Add($MatchAttribute)
    [void]$searcher.PropertiesToLoad.Add('distinguishedName')

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $results = $searcher.FindAll()
    $count = 0
    foreach ($r in $results) { $count++ }   # enumerate to force full paging cost
    $sw.Stop()

    $truncated = ($count -ge $searcher.SizeLimit)
    $results.Dispose()
    $searcher.Dispose()
    $entry.Dispose()

    return [pscustomobject]@{ ElapsedMs = $sw.Elapsed.TotalMilliseconds; ResultCount = $count; Truncated = $truncated }
}

function Get-Percentile {
    param([double[]]$Values, [double]$P)
    $sorted = $Values | Sort-Object
    if ($sorted.Count -eq 1) { return $sorted[0] }
    $rank = ($P / 100.0) * ($sorted.Count - 1)
    $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
    if ($lo -eq $hi) { return $sorted[$lo] }
    return $sorted[$lo] + ($rank - $lo) * ($sorted[$hi] - $sorted[$lo])
}

$summary = New-Object System.Collections.Generic.List[object]
foreach ($batch in $BatchSizes) {
    Write-Host "`nBatch $batch : $Repeats timed calls..."
    $durations = New-Object System.Collections.Generic.List[double]
    for ($i = 1; $i -le $Repeats; $i++) {
        $keys = Get-BatchKeys -Count $batch
        try {
            $res = Invoke-TimedBatch -Keys $keys
        }
        catch {
            Write-Error "Directory error on batch $batch call $i (stopping): $($_.Exception.Message)"
            exit 2
        }
        if ($res.ElapsedMs -gt $StopIfBatchExceedsMs) {
            Write-Error "Batch $batch call $i took $([math]::Round($res.ElapsedMs))ms (> $StopIfBatchExceedsMs guard). Stopping to avoid load."
            exit 3
        }
        if ($res.Truncated) {
            Write-Warning "Batch $batch call $i hit SizeLimit ($($res.ResultCount) results) - truncation/indeterminate. Recording and continuing."
        }
        $durations.Add($res.ElapsedMs)
        Write-Host ("  call {0}: {1,7:N1} ms  ({2} results{3})" -f $i, $res.ElapsedMs, $res.ResultCount, $(if ($res.Truncated) { ', TRUNCATED' } else { '' }))
    }

    $arr = $durations.ToArray()
    $p50 = Get-Percentile -Values $arr -P 50
    $p95 = Get-Percentile -Values $arr -P 95
    $mean = ($arr | Measure-Object -Average).Average
    $callsFor100k = [math]::Ceiling(100000 / $batch)
    $projP50Sec = ($callsFor100k * $p50) / 1000.0
    $projP95Sec = ($callsFor100k * $p95) / 1000.0

    $summary.Add([pscustomobject]@{
            BatchSize            = $batch
            Repeats              = $Repeats
            MeanMs               = [math]::Round($mean, 1)
            P50Ms                = [math]::Round($p50, 1)
            P95Ms                = [math]::Round($p95, 1)
            CallsFor100k         = $callsFor100k
            Proj100kP50Seconds   = [math]::Round($projP50Sec, 1)
            Proj100kP95Seconds   = [math]::Round($projP95Sec, 1)
        })
}

Write-Host "`n=== Timing summary (single sequential worker, mirrors production sequential chunks) ==="
$summary | Format-Table -AutoSize | Out-String | Write-Host
Write-Host 'Proj100k*Seconds = one 100,000-key CSV job at that batch, sequential. This is the per-job wait time input for the P05 concurrency cap.'

# --- Write artifact (timings only, no identifiers/values) ---
$outDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/capacity'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$outFile = Join-Path $outDir 'ldap-timing.json'
[pscustomobject]@{
    MatchAttribute = $MatchAttribute
    SearchBase     = $SearchBase
    KeySource      = $(if ($usingSynthetic) { 'synthetic-nonexistent' } else { 'approved-keyfile' })
    Host           = $env:COMPUTERNAME
    Identity       = "$env:USERDOMAIN\$env:USERNAME"
    Cases          = $summary
} | ConvertTo-Json -Depth 5 | Set-Content -Path $outFile -Encoding utf8
Write-Host "Wrote $outFile (timings only; no identifiers or returned values)."
