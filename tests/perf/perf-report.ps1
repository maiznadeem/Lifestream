# The judging half of tests/perf/run-perf.ps1, in functions so that tests/infra/PerfHarness.Tests.ps1 can feed it records and see the verdict.
# Dot-sourced by run-perf.ps1; has no side effects of its own.
#
# The rule it enforces (OWN-07): the run can only succeed when every expected measurement is present and passed, every test run succeeded
# and something was measured. A measurement that is absent is a failure of its own ("MISSED <key>"), never an empty cell that reads as "met".

$script:PerfScreens = @('Dashboard', 'Donors', 'Appointments', 'Patients', 'Requests', 'Stock', 'Receipts', 'Staff', 'Profile')
$script:PerfPhaseNames = @('launch', 'timing', '1x', '10x', '100x')

function Get-PerfPhases {
    <# Splits and validates the -Phases argument. Throws on an unknown phase and on an empty list: a run that measures nothing is not a run. #>
    param([string[]]$Phases)
    $list = @($Phases | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if (-not $list.Count) { throw "NO SIGNAL: no phase was selected, so nothing would be measured (valid phases: $($script:PerfPhaseNames -join ', '))" }
    $unknown = @($list | Where-Object { $_ -notin $script:PerfPhaseNames })
    if ($unknown.Count) { throw "NO SIGNAL: unknown phase '$($unknown -join "', '")', so nothing would be measured (valid phases: $($script:PerfPhaseNames -join ', '))" }
    return $list
}

function Get-PerfExpectations {
    <# What a complete run of these phases must have recorded: whole budgets (at least one row) and single rows by key, each in its dataset. #>
    param([string[]]$Phases)
    $items = New-Object System.Collections.Generic.List[object]
    function Budget($b, $d) { $items.Add([pscustomobject]@{ Kind = 'budget'; Value = $b; Dataset = $d }) }
    function Key($k, $d) { $items.Add([pscustomobject]@{ Kind = 'key'; Value = $k; Dataset = $d }) }
    if ($Phases -contains 'launch') { Budget 'launch' 'n/a'; Budget 'launch-cwd' 'n/a' }
    if ($Phases -contains '1x') { Budget 'fixture' '1x'; Budget 'lists' '1x'; Budget 'dashboard' '1x' }
    if ($Phases -contains '10x') { Budget 'fixture' '10x'; Budget 'staff-payload' '10x' }
    if ($Phases -contains '100x') {
        foreach ($b in 'fixture', 'lists', 'dashboard', 'grid', 'cold-dashboard') { Budget $b '100x' }
        foreach ($s in $script:PerfScreens) {
            $k = $s.ToLowerInvariant()
            Key "skeleton $k" '100x'; Key "api-calls $k" '100x'; Key "ui-gap $k" '100x'
        }
        Key 'api-calls ui-thread' '100x'; Key 'ui-gap worst' '100x'
    }
    return $items.ToArray()
}

function Get-RowKey($row) { if ($row.PSObject.Properties['key'] -and $row.key) { [string]$row.key } else { [string]$row.budget } }

function Get-PerfMedian([double[]]$values) {
    $v = @($values | Sort-Object)
    if ($v.Count % 2) { return $v[[int][math]::Floor($v.Count / 2)] }
    return ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2
}

function Get-PerfVerdict {
    <#
    .OUTPUTS  An object: ExitCode (0 met, 1 missed or incomplete, 2 no signal), Records (what the report lists), Misses, Missing, Messages (lines to print), Result (the closing line).
    #>
    param(
        [object[]]$Lines = @(),                 # the parsed measurements.jsonl, in order (rows and expect declarations)
        [string[]]$Phases,
        [string[]]$TestFailures = @(),
        [switch]$AssertBudgets,
        [int]$ColdRuns = 7
    )
    $messages = New-Object System.Collections.Generic.List[string]
    $declared = @($Lines | Where-Object { $_.PSObject.Properties['expect'] })
    $records = @($Lines | Where-Object { -not $_.PSObject.Properties['expect'] })

    # The cold dashboard is judged on the median of its fresh-process runs, not on any one run; a run that could not measure fails it.
    $cold = @($records | Where-Object { $_.budget -eq 'cold-dashboard' })
    if ($cold.Count) {
        $good = @($cold | Where-Object { -not ($_.PSObject.Properties['missed'] -and $_.missed) })
        $values = @($good | ForEach-Object { [double]$_.value } | Sort-Object)
        $med = if ($values.Count) { Get-PerfMedian $values } else { 0 }
        $complete = ($good.Count -eq $cold.Count) -and ($cold.Count -ge $ColdRuns)
        $records = @($records | Where-Object { $_.budget -ne 'cold-dashboard' })
        $records += [pscustomobject]@{
            budget = 'cold-dashboard'; key = 'cold-dashboard'; name = 'Dashboard first chart render after the data arrives, fresh process'; dataset = '100x'
            stat = "median of $($cold.Count) fresh processes"; n = $cold.Count; value = [math]::Round($med, 1); unit = 'ms'; limit = '< 300'
            pass = ($values.Count -gt 0 -and $med -lt 300 -and $complete)
            detail = "runs (ms): $(($cold | ForEach-Object { $_.value }) -join ', '); max $(if ($values.Count) { $values[-1] } else { 'n/a' }) ms$(if (-not $complete) { "; MISSED: $($cold.Count - $good.Count) run(s) took no measurement, $($cold.Count) of $ColdRuns runs recorded" })"
        }
    }

    # Every expected row must be present. Present but missed (no value) is reported with the misses below.
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($e in (Get-PerfExpectations $Phases)) {
        $has = if ($e.Kind -eq 'budget') { @($records | Where-Object { $_.budget -eq $e.Value -and $_.dataset -eq $e.Dataset }).Count } else { @($records | Where-Object { (Get-RowKey $_) -eq $e.Value -and $_.dataset -eq $e.Dataset }).Count }
        if (-not $has) { $missing.Add("$($e.Value) [$($e.Dataset)]") }
    }
    foreach ($d in $declared) {
        foreach ($k in @($d.expect)) {
            if (-not @($records | Where-Object { (Get-RowKey $_) -eq $k -and $_.dataset -eq $d.dataset }).Count) { $missing.Add("$k [$($d.dataset)]") }
        }
    }
    $missing = @($missing | Select-Object -Unique)
    foreach ($m in $missing) { $messages.Add("MISSED $($m -replace ' \[[^\]]*\]$', ''): no measurement was recorded$(if ($m -match '\[([^\]]*)\]$') { " (dataset $($Matches[1]))" })") }

    $misses = @($records | Where-Object { -not $_.pass })
    foreach ($r in $misses) {
        $isMissed = $r.PSObject.Properties['missed'] -and $r.missed
        $messages.Add($(if ($isMissed) { [string]$r.detail } else { "MISSED $(Get-RowKey $r): [$($r.budget)] $($r.name) ($($r.dataset)): $($r.value) $($r.unit), limit $($r.limit) - $($r.detail)" }))
    }
    if ($TestFailures.Count) { $messages.Add("test runs with failures: $($TestFailures -join '; ')") }

    $measuring = @($Phases | Where-Object { $_ -ne 'timing' })
    $empty = ($measuring.Count -gt 0 -and $records.Count -eq 0)
    if ($empty) { $messages.Add('NO SIGNAL: the run recorded no measurement at all') }

    $exit = 0
    $result = 'RESULT: every budget met'
    if ($empty) { $exit = 2; $result = 'RESULT: NO SIGNAL (exit 2): nothing was measured' }
    elseif ($missing.Count -or $TestFailures.Count) {
        $exit = 1
        $result = "RESULT: INCOMPLETE (exit 1): $($missing.Count) expected measurement(s) missing, $($TestFailures.Count) test run(s) failed, $($misses.Count) measurement(s) missed"
    }
    elseif ($misses.Count) {
        $result = "RESULT: $($misses.Count) budget measurement(s) MISSED"
        if ($AssertBudgets) { $exit = 1 }
    }
    return [pscustomobject]@{ ExitCode = $exit; Records = $records; Misses = $misses; Missing = $missing; Messages = $messages.ToArray(); Result = $result }
}

function Get-AncestorPids {
    <# The ids of this process and every process that started it, up to the root: whatever launched the script is not "another process". #>
    param([int]$StartPid = $PID)
    $ids = New-Object System.Collections.Generic.List[int]
    $current = $StartPid
    while ($current -gt 0 -and -not $ids.Contains($current)) {
        $ids.Add($current)
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $current" -ErrorAction SilentlyContinue
        if (-not $proc) { break }
        $current = [int]$proc.ParentProcessId
    }
    return $ids.ToArray()
}

function Get-BusyRegisteredProcesses {
    <# Lines of `killproc.py --list` that name a live registered process other than the run monitor and other than the one that launched this script (any name). #>
    param([string[]]$ListLines, [int[]]$AncestorPids = (Get-AncestorPids))
    foreach ($line in $ListLines) {
        if ($line -notmatch 'live=yes' -or $line -match '^monitor ') { continue }
        $id = if ($line -match '\bpid=(\d+)') { [int]$Matches[1] } else { 0 }
        if ($id -and $AncestorPids -contains $id) { continue }
        ($line -split ' ')[0]
    }
}
