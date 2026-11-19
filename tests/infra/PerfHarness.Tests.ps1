# OWN-07: the perf harness (tests/perf/run-perf.ps1 and perf-report.ps1) must be unable to report success when a measurement is missing.
# No timing is measured here: the script is run in replay mode (-FromResults) over hand-made measurement files, which goes through the same verdict code
# and the same exit codes as a real run. The one process-level test of the quiet check uses a fake launcher folder.

BeforeAll {
    $script:Perf = (Resolve-Path (Join-Path $PSScriptRoot '..' 'perf' 'run-perf.ps1')).Path
    . (Resolve-Path (Join-Path $PSScriptRoot '..' 'perf' 'perf-report.ps1')).Path
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }

    $script:Screens = 'Dashboard', 'Donors', 'Appointments', 'Patients', 'Requests', 'Stock', 'Receipts', 'Staff', 'Profile'

    function script:Row($budget, $key, $dataset = '100x', $pass = $true, $value = 10) {
        [ordered]@{ budget = $budget; key = $key; name = "$key row"; dataset = $dataset; stat = 's'; n = 1; value = $value; unit = 'ms'; limit = '< 100'; pass = $pass; detail = '' } | ConvertTo-Json -Compress
    }
    function script:MissedRow($budget, $key, $dataset = '100x') {
        [ordered]@{ budget = $budget; key = $key; name = "$key row"; dataset = $dataset; stat = 's'; n = 1; value = 0.0; unit = ''; limit = '< 100'; pass = $false; missed = $true; detail = "MISSED ${key}: no measurement" } | ConvertTo-Json -Compress
    }

    # A complete, passing 100x-only set of lines (what a good run of `-Phases 100x` leaves behind), minus the keys in -Without.
    function script:GoodLines([string[]]$Without = @(), [string[]]$Replace = @()) {
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add((Row 'fixture' 'fixture'))
        $lines.Add((Row 'lists' 'lists GET donors (page 1)'))
        $lines.Add((Row 'dashboard' 'dashboard'))
        $lines.Add((Row 'grid' 'grid'))
        1..7 | ForEach-Object { $lines.Add((Row 'cold-dashboard' 'cold-dashboard' '100x' $true 100)) }
        foreach ($s in $script:Screens) {
            $k = $s.ToLowerInvariant()
            $lines.Add((Row 'skeleton' "skeleton $k"))
            $lines.Add((Row 'api-calls' "api-calls $k"))
            $lines.Add((Row 'ui-gap' "ui-gap $k"))
        }
        $lines.Add((Row 'api-calls' 'api-calls ui-thread'))
        $lines.Add((Row 'ui-gap' 'ui-gap worst'))
        $lines.Add(([ordered]@{ expect = @(@($script:Screens | ForEach-Object { "skeleton $($_.ToLowerInvariant())" }) + 'lists GET donors (page 1)' + 'dashboard'); dataset = '100x' } | ConvertTo-Json -Compress))
        $keep = foreach ($line in $lines) {
            $drop = $false
            foreach ($w in $Without) { if ($line -match [regex]::Escape("`"key`":`"$w`"") -and $line -notmatch '"expect"') { $drop = $true } }
            if (-not $drop) { $line }
        }
        @($keep)
    }

    # Runs run-perf.ps1 in replay mode over the given lines; returns exit code and output.
    function script:Replay([string[]]$Lines, [string]$Phases = '100x', [switch]$Assert, [string]$Extra = '') {
        $file = Join-Path $TestDrive ("m-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.jsonl')
        Set-Content -Path $file -Value $Lines -Encoding utf8
        $a = @($script:Perf, '-FromResults', $file, '-Phases', $Phases)
        if ($Assert) { $a += '-AssertBudgets' }
        $text = & $script:Pwsh @script:PwshArgs @a 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $text }
    }
}

Describe 'OWN-07 harness: a missing measurement can never read as success' {
    It 'passes a complete, passing set (replay)' {
        $r = Replay (GoodLines) -Assert
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Output | Should -Match 'RESULT: every budget met'
    }

    It 'a missing Receipts skeleton (never recorded) fails with exit 1 and names it: MISSED skeleton receipts' {
        $r = Replay (GoodLines -Without 'skeleton receipts') -Assert
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'MISSED skeleton receipts'
    }

    It 'a Receipts skeleton recorded as MISSED (the test saw no skeleton) fails with exit 1 and names it' {
        $lines = (GoodLines -Without 'skeleton receipts') + (MissedRow 'skeleton' 'skeleton receipts')
        $r = Replay $lines -Assert
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'MISSED skeleton receipts'
    }

    It 'a missing screen fails without -AssertBudgets too: completeness is not a budget' {
        $r = Replay (GoodLines -Without 'skeleton receipts')
        $r.ExitCode | Should -Be 1 -Because $r.Output
    }

    It 'every expected row must be present: dropping any of the 100x per-screen rows, the grid or the cold runs fails the run' {
        foreach ($drop in 'api-calls staff', 'ui-gap profile', 'ui-gap worst', 'api-calls ui-thread', 'skeleton dashboard', 'grid', 'dashboard', 'lists GET donors (page 1)', 'cold-dashboard') {
            $r = Replay (GoodLines -Without $drop) -Assert
            $r.ExitCode | Should -Be 1 -Because "dropping '$drop': $($r.Output)"
        }
    }

    It 'a cold run that recorded fewer than the required fresh processes fails' {
        $lines = @(GoodLines -Without 'cold-dashboard') + (1..3 | ForEach-Object { Row 'cold-dashboard' 'cold-dashboard' '100x' $true 100 })
        (Replay $lines -Assert).ExitCode | Should -Be 1
    }

    It 'any test run that failed fails the run, even when every record passes (verdict function)' {
        $lines = @(GoodLines | ForEach-Object { $_ | ConvertFrom-Json })
        $v = Get-PerfVerdict -Lines $lines -Phases @('100x') -TestFailures @('PerfPhase=screens') -AssertBudgets
        $v.ExitCode | Should -Be 1
        $v.Messages -join "`n" | Should -Match 'PerfPhase=screens'
        (Get-PerfVerdict -Lines $lines -Phases @('100x') -TestFailures @('PerfPhase=screens')).ExitCode | Should -Be 1 -Because 'also without -AssertBudgets'
        (Get-PerfVerdict -Lines $lines -Phases @('100x') -AssertBudgets).ExitCode | Should -Be 0
    }

    It 'a budget miss exits 1 with -AssertBudgets and 0 without, as before' {
        $lines = @(GoodLines | Where-Object { $_ -notmatch '"key":"ui-gap worst"' }) + (Row 'ui-gap' 'ui-gap worst' '100x' $false 80)
        (Replay $lines -Assert).ExitCode | Should -Be 1
        (Replay $lines).ExitCode | Should -Be 0
    }

    It '-Phases none (or no phase, or an unknown phase) passes with no measurement no more: exit 2, NO SIGNAL' {
        foreach ($phases in 'none', '', 'launch,nonsense') {
            $r = Replay (GoodLines) -Phases $phases -Assert
            $r.ExitCode | Should -Be 2 -Because "phases '$phases': $($r.Output)"
            $r.Output | Should -Match 'NO SIGNAL'
        }
    }

    It 'an empty run (no record at all) is exit 2, NO SIGNAL' {
        $r = Replay @('') -Phases '1x' -Assert
        $r.ExitCode | Should -Be 2 -Because $r.Output
        $r.Output | Should -Match 'NO SIGNAL'
    }

    It 'run-perf.ps1 takes its verdict and its exit code from Get-PerfVerdict, with every test run feeding the failure list' {
        $source = Get-Content $script:Perf -Raw
        $source | Should -Match 'Get-PerfVerdict'
        ([regex]::Matches($source, 'testFailures \+=')).Count | Should -BeGreaterOrEqual 5
        $source | Should -Not -Match 'every budget met.
?
\s*exit 0\s*$' -Because 'the old unconditional success line is gone'
    }
}

Describe 'OWN-07 quiet check: the process that launched the script is not another process' {
    It 'ignores the launcher entry of this process chain whatever it is called, but still refuses a stranger' {
        $mine = Get-AncestorPids
        $mine | Should -Contain $PID
        $lines = @(
            "monitor pid=10668 start=2026-11-18T19:01:07Z live=yes matches=yes",
            "my-custom-launcher-name pid=$PID start=2026-11-19T07:39:29Z live=yes matches=yes",
            "some-other-agent pid=999999 start=2026-11-19T07:39:29Z live=yes matches=yes",
            "dead-one pid=999998 start=2026-11-19T07:39:29Z live=no matches=no"
        )
        @(Get-BusyRegisteredProcesses -ListLines $lines -AncestorPids $mine) | Should -Be @('some-other-agent')
    }

    It 'does not exempt a name any more: a stranger called b-10-perf-x is refused, the launcher of this run called anything else is not' {
        $lines = @("b-10-perf-impostor pid=999997 start=x live=yes matches=yes", "v-10-perf-demo4-launcher pid=$PID start=x live=yes matches=yes")
        @(Get-BusyRegisteredProcesses -ListLines $lines -AncestorPids (Get-AncestorPids)) | Should -Be @('b-10-perf-impostor')
    }

    It 'a process that started the script is found through the parent chain (a child pwsh of this one sees this process as an ancestor)' {
        $shell = @($script:PwshArgs | Select-Object -SkipLast 1)
        $child = & $script:Pwsh @shell -Command ". '$((Resolve-Path (Join-Path $PSScriptRoot '..' 'perf' 'perf-report.ps1')).Path)'; (Get-AncestorPids) -join ','"
        ($child -split ',') | Should -Contain "$PID"
    }
}
