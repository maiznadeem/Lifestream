<#
.SYNOPSIS
Builds the solution once and runs every test of the four test projects in about two minutes.

.DESCRIPTION
All four projects start together: Api is the longest single piece, and one Domain test runs 'dotnet build' twice,
which takes 3 to 40 seconds depending on what else the machine does, so nothing waits for anything else. With
-DomainAndClientFirst, Api and the Desktop tests start when Domain and Client have ended.

Desktop: a WPF test process runs its tests one at a time (WPF cannot build views on several threads of one process),
so the Desktop test executable is started several times, each process (a shard) running its own share of the test
classes. The classes are shared out by their known durations (tools/test-fast.durations.json; unknown classes are
dealt round-robin). A class that starts its own worker processes gets a shard to itself ("ownShard" in that file).
A class that takes longer alone than the whole run should ("split" in that file: class name and number of parts) has
its test cases dealt over that many shards of their own: its fixed waits cannot be made shorter by more processors.

Api: one process, as 'dotnet test' runs it (it runs its classes in parallel itself). With -ApiShards 2 or more the
classes are shared over that many processes, each class with its own database as before: the classes on the copy of
the 100x database stay in one process ("apiTogether": they share one template per process), and the classes that
must run alone ("apiLast": the one-at-a-time collection) run in a process of their own once the other Api processes
have ended, so SQL Server is as quiet for them as in an unsharded run. That only pays when processors are idle.

The Api tests keep the backup of their 100x template database on the server between runs and say in the summary
whether this run restored it or had to build it (a change in the generator, the seed code, the migrations or the day).

The real-window tests of the Desktop project need the compositor drawing. While it runs, this script asks Windows to
keep the display on; it never sends input (no pointer movement, no keystrokes), so it cannot turn on a display that is
already off. Those tests then skip at once ("display is off: real-window checks not run"), as they do when the session
is locked. The summary says on a line of its own how many real-window checks did not run, and names them with the
reason; because a release gate must not lose those checks silently, the run then ends FAILED unless
-AllowRealWindowSkip says that a run without them is acceptable.

Every test process runs with DOTNET_TieredPGO=0: the same tests and results for about 30 % less processor time (the
JIT does not instrument code for a profile that a process this short never gets to use).

The run uses at most 16 logical processors (8 to 23) at BelowNormal priority, shows no window, and holds a
machine-wide mutex, so a second caller waits for the first to finish.

Nothing is dropped: for every project the number of tests run must equal the number the unsharded executable lists,
every listed Desktop test must have run exactly once, and every class must have run in exactly the shard it was
given to (a split class: in its own parts and nowhere else); otherwise the run fails.

.EXAMPLE
tools/test-fast.ps1
.EXAMPLE
test-fast.ps1 -Worktree G:/path/to/checkout
#>
[CmdletBinding()]
param(
    # The checkout to build and test (default: the checkout this script is in).
    [string]$Worktree,
    # How many Desktop test processes run at once (own-shard classes and the parts of split classes are among them).
    [int]$Shards = 14,
    # How many Api test processes run at once (1: one unsharded process; with more, the process of the run-alone classes comes
    # after them and is not counted).
    [int]$ApiShards = 1,
    # How many classes one Api process runs at once (0: the allowed processors shared over the Api processes, so that SQL Server
    # gets as many classes at once as in an unsharded run; more than that and tests with a time window fail).
    [int]$ApiThreads = 0,
    # How many worker processes the walk over every command starts. 0: 8 when at least three quarters of the allowed processors are
    # free, else 6 (a worker is 40 % processor time and 60 % fixed waits; more of them finish sooner but starve the rest when
    # processors are short). The test's own default, one per two processors, is meant for a walk that runs by itself.
    [int]$Walkers = 0,
    # dotnet.exe of the SDK to build with (default: tools/dotnet beside the worktrees folder when it exists, else PATH).
    [string]$Dotnet,
    # Where logs and result files go (default: a folder per checkout under the temp folder).
    [string]$Out,
    # The class durations file (default: tools/test-fast.durations.json of the checkout, else the one beside this script).
    [string]$Durations,
    # Accept a run in which real-window checks did not run (display off, session locked): they are still counted and named, but the
    # run is not failed for them.
    [switch]$AllowRealWindowSkip,
    # Run Domain and Client by themselves first; Api and the Desktop tests start when both have ended.
    [switch]$DomainAndClientFirst,
    # Skip the build (the Release output must be up to date).
    [switch]$NoBuild,
    # Write the class durations measured by this run to the durations file of the checkout.
    [switch]$UpdateDurations,
    # Logical processors to run on (default: 8 to 23) and the limit for one test process, in minutes.
    [long]$Affinity = 0xFFFF00,
    [int]$TimeoutMinutes = 15
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

function Resolve-Checkout([string]$path) {
    if (-not $path) {
        $path = Split-Path -Parent $PSScriptRoot
    }

    $full = (Resolve-Path -LiteralPath $path).ProviderPath
    if (-not (Test-Path -LiteralPath (Join-Path $full 'Lifestream.slnx'))) {
        throw "No Lifestream.slnx in '$full': pass -Worktree <checkout>."
    }

    return $full
}

function Resolve-Dotnet([string]$path, [string]$checkout) {
    if ($path) {
        return (Resolve-Path -LiteralPath $path).ProviderPath
    }

    # The workspace layout: <workspace>/worktrees/<name> beside <workspace>/tools/dotnet.
    $beside = Join-Path (Split-Path -Parent (Split-Path -Parent $checkout)) 'tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $beside) {
        return $beside
    }

    return (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
}

function Join-Arguments([string[]]$arguments) {
    return (($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ')
}

$script:Started = New-Object System.Collections.ArrayList

# Starts a process with no window, on the allowed processors at BelowNormal priority; its output is collected for the log.
function Start-Hidden([string]$name, [string]$file, [string[]]$arguments, [hashtable]$environment = @{}, [string]$log) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $file
    $info.Arguments = Join-Arguments $arguments
    $info.WorkingDirectory = $script:Checkout
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($key in $environment.Keys) {
        $info.EnvironmentVariables[$key] = [string]$environment[$key]
    }

    $process = [System.Diagnostics.Process]::Start($info)
    try {
        # Inherited from this script already; set again in case the caller's own limits were tighter or looser.
        $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
        $process.ProcessorAffinity = [IntPtr]$Affinity
    }
    catch {
        Write-Verbose "limits not set on $name (it had already ended): $_"
    }

    $run = [pscustomobject]@{
        Name = $name
        Project = ''
        Note = ''
        Result = ''
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        Error = $process.StandardError.ReadToEndAsync()
        Log = $log
        Watch = [System.Diagnostics.Stopwatch]::StartNew()
        StartedAt = [DateTime]::UtcNow
        Seconds = 0.0
        Cpu = 0.0
        ExitCode = $null
        TimedOut = $false
    }
    [void]$script:Started.Add($run)
    return $run
}

function Stop-Tree($run) {
    if (-not $run.Process.HasExited) {
        & taskkill.exe /PID $run.Process.Id /T /F 2>&1 | Out-Null
    }
}

# Looks at each run once: notes the ones that ended, and ends one that outlived the limit together with its child processes.
# Returns $true while any of them is still running.
function Update-Runs($runs, [int]$timeoutMinutes) {
    $running = $false
    foreach ($run in $runs) {
        if (-not $run.Watch.IsRunning) {
            continue
        }

        if ($run.Process.HasExited) {
            $run.Watch.Stop()
        }
        elseif ($run.Watch.Elapsed.TotalMinutes -ge $timeoutMinutes) {
            $run.TimedOut = $true
            Stop-Tree $run
            $run.Process.WaitForExit()
            $run.Watch.Stop()
        }
        else {
            $running = $true
        }
    }

    return $running
}

function Complete-Runs($runs) {
    foreach ($run in $runs) {
        $run.Process.WaitForExit()
        $run.Seconds = $run.Watch.Elapsed.TotalSeconds
        $run.ExitCode = $run.Process.ExitCode
        try { $run.Cpu = $run.Process.TotalProcessorTime.TotalSeconds } catch { $run.Cpu = 0.0 }
        if ($run.Log) {
            [System.IO.File]::WriteAllText($run.Log, $run.Output.Result + [Environment]::NewLine + $run.Error.Result)
        }
    }
}

function Wait-Runs($runs, [int]$timeoutMinutes) {
    while (Update-Runs $runs $timeoutMinutes) {
        Start-Sleep -Milliseconds 100
    }

    Complete-Runs $runs
}

# Starts a test executable with a listing option; Read-Listing gives the listed lines (the runner's banner and blank lines dropped).
function Start-Listing([string]$exe, [string]$what) {
    return Start-Hidden "list $what" $exe @('-list', $what, '-preEnumerateTheories', '-trait-', 'Category=Perf')
}

function Read-Listing($run) {
    if ($run.ExitCode -ne 0) {
        throw "'$($run.Process.StartInfo.FileName) $($run.Process.StartInfo.Arguments)' exited with $($run.ExitCode): $($run.Output.Result) $($run.Error.Result)"
    }

    return @($run.Output.Result -split "`r?`n" | Where-Object { $_.Trim() -and $_ -notmatch '^xUnit\.net ' } | ForEach-Object { $_.Trim() })
}

# The tests of one process as the runner reported them (xUnit.net v2+ XML): one object per test with its class, collection and result.
function Read-Results([string]$file) {
    if (-not (Test-Path -LiteralPath $file)) {
        return @()
    }

    [xml]$document = Get-Content -LiteralPath $file -Raw
    $tests = New-Object System.Collections.ArrayList
    foreach ($collection in $document.SelectNodes('//collection')) {
        $collectionName = $collection.GetAttribute('name')
        foreach ($test in $collection.SelectNodes('test')) {
            [void]$tests.Add([pscustomobject]@{
                Name = $test.GetAttribute('name')
                Class = $test.GetAttribute('type')
                Collection = $collectionName
                Result = $test.GetAttribute('result')
                Reason = $(if ($test.SelectSingleNode('reason')) { $test.SelectSingleNode('reason').InnerText.Trim() } else { '' })
                Seconds = [double]::Parse($test.GetAttribute('time'), [System.Globalization.CultureInfo]::InvariantCulture)
            })
        }
    }

    return $tests.ToArray()
}

# The listing prints a test's name as it is; the result file writes the quotes inside it with a backslash. One form for comparing the two.
function Get-NameKey([string]$name) {
    # A character outside ASCII (the infinity sign of a theory row) reaches this script through the listing's console output in whatever
    # encoding the caller's console has, so it can arrive as another character or as several: any run of them counts as one.
    return (($name -replace '\\', '') -replace '[^\x20-\x7E]+', '?')
}

function New-Shard([string]$note) {
    return [pscustomobject]@{ Classes = (New-Object System.Collections.ArrayList); Cases = (New-Object System.Collections.ArrayList); Seconds = 0.0; Note = $note; Last = $false; Whole = $false }
}

# Shares classes over the given shards: the ones with a known duration longest first, each to the shard with the least so far; the ones with
# no known duration are dealt round-robin.
function Add-Classes($shards, [string[]]$classes, $known) {
    $timed = @($classes | Where-Object { $known.ContainsKey($_) } | Sort-Object @{ Expression = { $known[$_] }; Descending = $true }, @{ Expression = { $_ } })
    foreach ($class in $timed) {
        $shard = $shards | Sort-Object Seconds | Select-Object -First 1
        [void]$shard.Classes.Add($class)
        $shard.Seconds += $known[$class]
    }

    $next = 0
    foreach ($class in @($classes | Where-Object { -not $known.ContainsKey($_) } | Sort-Object)) {
        [void]$shards[$next % $shards.Count].Classes.Add($class)
        $next++
    }
}

function Get-Seconds([string[]]$classes, $known) {
    $sum = 0.0
    foreach ($class in $classes) {
        if ($known.ContainsKey($class)) { $sum += $known[$class] }
    }

    return $sum
}

# Shares the Desktop test cases over the shards. A class that starts its own worker processes gets a shard of its own. A split class has its
# cases dealt over its number of shards (in name order, one each in turn, so every part gets the same mix). The other classes go to the shards
# that are left. A shard runs either whole classes (Classes) or the cases of one split class (Cases).
function Get-DesktopPlan($cases, $known, [string[]]$alone, $split, [int]$count) {
    $byClass = @{}
    foreach ($case in $cases) {
        if (-not $byClass.ContainsKey($case.Class)) { $byClass[$case.Class] = New-Object System.Collections.ArrayList }
        [void]$byClass[$case.Class].Add($case)
    }

    $plan = New-Object System.Collections.ArrayList
    $shared = New-Object System.Collections.ArrayList
    foreach ($class in ($byClass.Keys | Sort-Object)) {
        $seconds = Get-Seconds @($class) $known
        $short = $class.Substring($class.LastIndexOf('.') + 1)
        if ($alone -contains $class) {
            $shard = New-Shard "$short (starts its own worker processes: $script:WalkerCount)"
            [void]$shard.Classes.Add($class)
            $shard.Seconds = $seconds
            [void]$plan.Add($shard)
        }
        elseif ($split.ContainsKey($class) -and $split[$class] -gt 1) {
            $parts = [Math]::Min([int]$split[$class], $byClass[$class].Count)
            $shards = @(1..$parts | ForEach-Object { New-Shard "$short, part $_ of $parts" })
            $index = 0
            foreach ($case in ($byClass[$class] | Sort-Object DisplayName, ID)) {
                [void]$shards[$index % $parts].Cases.Add($case)
                $index++
            }

            foreach ($shard in $shards) {
                $shard.Seconds = $seconds * $shard.Cases.Count / $byClass[$class].Count
                [void]$plan.Add($shard)
            }
        }
        else {
            [void]$shared.Add($class)
        }
    }

    $bins = [Math]::Max(1, $count - $plan.Count)
    $shards = @(1..$bins | ForEach-Object { New-Shard '' })
    Add-Classes $shards @($shared) $known
    foreach ($shard in $shards) {
        if ($shard.Classes.Count -gt 0) {
            $shard.Note = "$($shard.Classes.Count) classes"
            [void]$plan.Add($shard)
        }
    }

    return $plan.ToArray()
}

# Shares the Api classes over the processes: the classes that must stay together in the first, the rest by duration, and the run-alone
# classes in a process that starts when the others have ended.
function Get-ApiPlan([string[]]$classes, $known, [string[]]$together, [string[]]$last, [int]$count) {
    if ($count -le 1) {
        $shard = New-Shard "$($classes.Count) classes, unsharded"
        $shard.Whole = $true
        foreach ($class in $classes) {
            [void]$shard.Classes.Add($class)
        }

        return @($shard)
    }

    $lastClasses = @($classes | Where-Object { $last -contains $_ })
    $togetherClasses = @($classes | Where-Object { $together -contains $_ -and $last -notcontains $_ })
    $rest = @($classes | Where-Object { $last -notcontains $_ -and $together -notcontains $_ })
    $shards = @(1..([Math]::Max(1, $count)) | ForEach-Object { New-Shard '' })
    foreach ($class in $togetherClasses) {
        [void]$shards[0].Classes.Add($class)
    }

    $shards[0].Seconds = Get-Seconds $togetherClasses $known
    Add-Classes $shards $rest $known
    $plan = New-Object System.Collections.ArrayList
    foreach ($shard in $shards) {
        if ($shard.Classes.Count -gt 0) {
            $shard.Note = "$($shard.Classes.Count) classes"
            [void]$plan.Add($shard)
        }
    }

    if ($togetherClasses.Count -gt 0) {
        $plan[0].Note += " ($($togetherClasses.Count) on the copy of the 100x database)"
    }

    if ($lastClasses.Count -gt 0) {
        $shard = New-Shard "$($lastClasses.Count) classes, one at a time, after the other Api processes"
        $shard.Last = $true
        foreach ($class in $lastClasses) {
            [void]$shard.Classes.Add($class)
        }

        [void]$plan.Add($shard)
    }

    return $plan.ToArray()
}

$script:Checkout = Resolve-Checkout $Worktree
$dotnetExe = Resolve-Dotnet $Dotnet $script:Checkout
if (-not $Out) {
    $Out = Join-Path ([System.IO.Path]::GetTempPath()) ('lifestream-test-fast\' + (($script:Checkout -replace '[:\\/ ]+', '_').Trim('_')))
}

if (-not $Durations) {
    $Durations = Join-Path $script:Checkout 'tools\test-fast.durations.json'
    if (-not (Test-Path -LiteralPath $Durations)) {
        $Durations = Join-Path $PSScriptRoot 'test-fast.durations.json'
    }
}

$known = @{}
$split = @{}
$settings = @{ ownShard = @(); apiTogether = @(); apiLast = @() }
if (Test-Path -LiteralPath $Durations) {
    $file = Get-Content -LiteralPath $Durations -Raw | ConvertFrom-Json
    foreach ($property in $file.seconds.PSObject.Properties) {
        $known[$property.Name] = [double]$property.Value
    }

    foreach ($key in @($settings.Keys)) {
        if ($file.PSObject.Properties[$key]) {
            $settings[$key] = @($file.$key)
        }
    }

    if ($file.PSObject.Properties['split']) {
        foreach ($property in $file.split.PSObject.Properties) {
            $split[$property.Name] = [int]$property.Value
        }
    }
}

$self = [System.Diagnostics.Process]::GetCurrentProcess()
$before = @{ Affinity = $self.ProcessorAffinity; Priority = $self.PriorityClass; Root = $env:DOTNET_ROOT; HostPath = $env:DOTNET_HOST_PATH; Pgo = $env:DOTNET_TieredPGO; TemplateLog = $env:LIFESTREAM_SCALE_TEMPLATE_LOG; Reuse = $env:MSBUILDDISABLENODEREUSE; Server = $env:DOTNET_CLI_USE_MSBUILD_SERVER }
$mutex = New-Object System.Threading.Mutex($false, 'Global\Lifestream.TestFast')
$held = $false
$displayHeld = $false
$poolBefore = $null
$exitCode = 1
try {
    try {
        if (-not $mutex.WaitOne(0)) {
            Write-Host 'Another test-fast run is in progress on this machine: waiting for it to finish...'
            [void]$mutex.WaitOne()
        }
    }
    catch [System.Threading.AbandonedMutexException] {
        Write-Verbose 'The previous holder ended without releasing the mutex; it is ours now.'
    }

    $held = $true
    $self.ProcessorAffinity = [IntPtr]$Affinity
    $self.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
    $env:DOTNET_ROOT = Split-Path -Parent $dotnetExe
    $env:DOTNET_HOST_PATH = $dotnetExe # what 'dotnet test' sets; the repository tests build a sample project with it
    $env:DOTNET_TieredPGO = '0' # no profile instrumentation in processes this short: the same tests for about 30 % less processor time
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

    if (Test-Path -LiteralPath $Out) {
        Remove-Item -LiteralPath $Out -Recurse -Force
    }

    [void](New-Item -ItemType Directory -Force -Path $Out)
    $templateLog = Join-Path $Out 'scale-template.log'
    $env:LIFESTREAM_SCALE_TEMPLATE_LOG = $templateLog # each Api process says here whether it restored the kept 100x template or built it
    Write-Host "test-fast: $($script:Checkout)"
    Write-Host "  logs and results: $Out"

    # What else is using the allowed processors: this run needs most of them, and tests that wait real time fail when they get too little.
    $busy = 0.0
    $allowed = 0
    try {
        foreach ($sample in (Get-Counter '\Processor(*)\% Processor Time' -SampleInterval 1 -MaxSamples 1).CounterSamples) {
            if ($sample.InstanceName -match '^\d+$' -and (($Affinity -shr [int]$sample.InstanceName) -band 1)) {
                $busy += $sample.CookedValue / 100
                $allowed++
            }
        }

        $warning = ''
        if ($busy -gt $allowed / 4) {
            $warning = ': more than a quarter is taken, so expect a slower run, and tests with a real-time window can fail'
        }

        Write-Host ('  processors: {0} allowed, {1:N1} of them busy with other work before this run{2}' -f $allowed, $busy, $warning)
    }
    catch {
        Write-Verbose "The processor load could not be read: $_"
    }

    if ($Walkers -le 0) {
        $Walkers = 6
        if ($allowed -gt 0 -and ($allowed - $busy) -ge 0.75 * $allowed) {
            $Walkers = 8
        }
    }

    # The display is kept on while this runs (the request ends in the finally block below). Nothing here sends input.
    try {
        Add-Type -Namespace LifestreamTestFast -Name Power -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
        [void][LifestreamTestFast.Power]::SetThreadExecutionState([uint32]2147483651) # continuous + system required + display required
        $displayHeld = $true
    }
    catch {
        Write-Verbose "Windows could not be asked to keep the display on: $_"
    }

    # Every child's output is read through a pipe, and each pending read holds a thread of the thread pool. With some twenty children started
    # together the pool's ready threads (one per processor) run out, and a child that prints more than its pipe holds (the Client tests log
    # about 15 KB from a real server) then waits in its own write until the pool grows: measured, a child printing 68 KB beside 30 silent
    # ones took 24.9 s to exit instead of 0.1 s, and a Client run stayed 51 s after its last test. Enough ready threads for every read.
    $workers = 0
    $ports = 0
    [System.Threading.ThreadPool]::GetMinThreads([ref]$workers, [ref]$ports)
    $poolBefore = @($workers, $ports)
    [void][System.Threading.ThreadPool]::SetMinThreads([Math]::Max($workers, 256), [Math]::Max($ports, 256))

    $buildSeconds = 0.0
    if (-not $NoBuild) {
        $build = Start-Hidden 'build' $dotnetExe @('build', (Join-Path $script:Checkout 'Lifestream.slnx'), '-c', 'Release', '-maxcpucount:8', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-nologo', '-v:q') @{} (Join-Path $Out 'build.log')
        Wait-Runs @($build) 30
        $buildSeconds = $build.Seconds
        if ($build.ExitCode -ne 0) {
            Write-Host ($build.Output.Result)
            throw "The build failed (exit code $($build.ExitCode)); see $(Join-Path $Out 'build.log')."
        }

        Write-Host ('  build: {0:N0} s' -f $buildSeconds)
    }

    $projects = [ordered]@{}
    foreach ($name in 'Domain', 'Client', 'Api', 'Desktop') {
        $exe = @(Get-ChildItem -Path (Join-Path $script:Checkout "tests\Lifestream.$name.Tests\bin\Release") -Filter "Lifestream.$name.Tests.exe" -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        if ($exe.Count -eq 0) {
            throw "No Release build of Lifestream.$name.Tests: run without -NoBuild."
        }

        $projects[$name] = $exe[0].FullName
    }

    $tests = [System.Diagnostics.Stopwatch]::StartNew()
    $common = @('-noLogo', '-noColor', '-preEnumerateTheories', '-trait-', 'Category=Perf')
    function Start-Tests([string]$label, [string]$project, [string[]]$filter = @(), [hashtable]$environment = @{}, [string]$note = '') {
        $result = Join-Path $Out "$label.xml"
        $run = Start-Hidden $label $projects[$project] ($common + $filter + @('-result-xml', $result)) $environment (Join-Path $Out "$label.log")
        $run.Result = $result
        $run.Project = $project
        $run.Note = $note
        return $run
    }

    # Domain and Client start while the unsharded executables list what they hold: the reference everything below is checked against.
    $first = @((Start-Tests 'Domain' 'Domain'), (Start-Tests 'Client' 'Client'))
    $listings = [ordered]@{
        Domain = Start-Listing $projects['Domain'] 'tests'
        Client = Start-Listing $projects['Client'] 'tests'
        Api = Start-Listing $projects['Api'] 'tests'
        ApiClasses = Start-Listing $projects['Api'] 'classes'
        Desktop = Start-Listing $projects['Desktop'] 'full/json'
    }
    Wait-Runs @($listings.Values) 5
    $expected = @{}
    foreach ($name in 'Domain', 'Client', 'Api') {
        $expected[$name] = (Read-Listing $listings[$name]).Count
    }

    $parsed = ConvertFrom-Json -InputObject ((Read-Listing $listings['Desktop']) -join "`n")
    $cases = @($parsed | ForEach-Object { $_ })
    $expected['Desktop'] = $cases.Count
    $listedNames = @{}
    $casesOf = @{}
    foreach ($case in $cases) {
        $key = Get-NameKey $case.DisplayName
        $listedNames[$key] = [int]$listedNames[$key] + 1
        if (-not $casesOf.ContainsKey($case.Class)) { $casesOf[$case.Class] = New-Object System.Collections.ArrayList }
        [void]$casesOf[$case.Class].Add($case)
    }

    $classes = @{ Desktop = @($casesOf.Keys | Sort-Object); Api = @(Read-Listing $listings['ApiClasses'] | Sort-Object -Unique) }

    # Every listed Desktop test case and every Api class is given to exactly one shard, before anything runs.
    $script:WalkerCount = $Walkers
    $plans = @{
        Desktop = @(Get-DesktopPlan $cases $known $settings.ownShard $split $Shards)
        Api = @(Get-ApiPlan $classes.Api $known $settings.apiTogether $settings.apiLast $ApiShards)
    }
    $shardsOfClass = @{ Desktop = @{}; Api = @{} }
    $shardOfCase = @{}
    foreach ($project in 'Api', 'Desktop') {
        for ($i = 0; $i -lt $plans[$project].Count; $i++) {
            $label = "$project-{0:D2}" -f ($i + 1)
            $shard = $plans[$project][$i]
            if ($shard.Whole) {
                $label = $project
            }

            foreach ($class in $shard.Classes) {
                if ($shardsOfClass[$project].ContainsKey($class)) {
                    throw "Class $class was given to two shards."
                }

                $shardsOfClass[$project][$class] = @{ $label = $true }
            }

            foreach ($case in $shard.Cases) {
                if ($shardOfCase.ContainsKey($case.ID)) {
                    throw "Test '$($case.DisplayName)' was given to two shards: $($shardOfCase[$case.ID]) and $label."
                }

                $shardOfCase[$case.ID] = $label
                if (-not $shardsOfClass[$project].ContainsKey($case.Class)) { $shardsOfClass[$project][$case.Class] = @{} }
                $shardsOfClass[$project][$case.Class][$label] = $true
            }
        }

        foreach ($class in $classes[$project]) {
            if (-not $shardsOfClass[$project].ContainsKey($class)) {
                throw "Class $class was given to no shard."
            }

            if ($shardsOfClass[$project][$class].Count -ne 1 -and -not $split.ContainsKey($class)) {
                throw "Class $class was given to $($shardsOfClass[$project][$class].Count) shards."
            }
        }
    }

    $wholeClasses = @{}
    foreach ($shard in $plans.Desktop) {
        foreach ($class in $shard.Classes) { $wholeClasses[$class] = $true }
    }

    foreach ($case in $cases) {
        $given = [int]$wholeClasses.ContainsKey($case.Class) + [int]$shardOfCase.ContainsKey($case.ID)
        if ($given -ne 1) {
            throw "Test '$($case.DisplayName)' was given to $given shards."
        }
    }

    if ($DomainAndClientFirst) {
        Wait-Runs $first $TimeoutMinutes
    }

    $second = New-Object System.Collections.ArrayList
    $apiLast = $null
    if ($ApiThreads -le 0) {
        $processors = 0
        for ($bit = 0; $bit -lt 64; $bit++) {
            if (($Affinity -shr $bit) -band 1) { $processors++ }
        }

        $ApiThreads = [int][Math]::Ceiling($processors / [Math]::Max(1, @($plans.Api | Where-Object { -not $_.Last }).Count))
    }

    for ($i = 0; $i -lt $plans.Api.Count; $i++) {
        $shard = $plans.Api[$i]
        $label = "Api-{0:D2}" -f ($i + 1)
        $filter = @($shard.Classes | ForEach-Object { '-class'; $_ })
        if ($shard.Whole) {
            [void]$second.Add((Start-Tests 'Api' 'Api' @() @{} $shard.Note))
        }
        elseif ($shard.Last) {
            $apiLast = @{ Label = $label; Filter = $filter; Note = $shard.Note }
        }
        else {
            [void]$second.Add((Start-Tests $label 'Api' ($filter + @('-maxThreads', "$ApiThreads")) @{} "$($shard.Note), $ApiThreads at once"))
        }
    }

    for ($i = 0; $i -lt $plans.Desktop.Count; $i++) {
        $shard = $plans.Desktop[$i]
        $label = "Desktop-{0:D2}" -f ($i + 1)
        # A class filter and a test case filter cannot be combined (the runner applies both), so a shard has one kind or the other.
        $filter = @($shard.Classes | ForEach-Object { '-class'; $_ }) + @($shard.Cases | ForEach-Object { '-id'; $_.ID })
        # Each shard writes its render captures to its own folder, so two processes never write one file.
        $captures = Join-Path $Out ("captures\$label")
        [void](New-Item -ItemType Directory -Force -Path $captures)
        $environment = @{ LIFESTREAM_CAPTURE_DIR = $captures; LIFESTREAM_WAKING_WALKERS = $Walkers }

        [void]$second.Add((Start-Tests $label 'Desktop' $filter $environment $shard.Note))
    }

    while ($true) {
        $running = (Update-Runs $second $TimeoutMinutes) -bor (Update-Runs $first $TimeoutMinutes)
        if ($apiLast -and @($second | Where-Object { $_.Project -eq 'Api' -and $_.Watch.IsRunning }).Count -eq 0) {
            # The run-alone classes start when no other Api process is left: SQL Server is theirs, as it is at the end of an unsharded run.
            [void]$second.Add((Start-Tests $apiLast.Label 'Api' $apiLast.Filter @{} $apiLast.Note))
            $apiLast = $null
            $running = $true
        }

        if (-not $running) {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    Complete-Runs $first
    Complete-Runs $second
    $tests.Stop()

    # The summary, and the proof that nothing was dropped.
    $problems = New-Object System.Collections.ArrayList
    $rows = New-Object System.Collections.ArrayList
    $failed = New-Object System.Collections.ArrayList
    $realWindowSkips = New-Object System.Collections.ArrayList
    $ran = @{}
    $seenIn = @{ Domain = @{}; Client = @{}; Api = @{}; Desktop = @{} }
    $collections = @{}
    $ranNames = @{}
    $measured = @{}
    $span = @{}
    foreach ($run in (@($first) + @($second) | Sort-Object @{ Expression = { [array]::IndexOf(@('Domain', 'Client', 'Api', 'Desktop'), $_.Project) } }, Name)) {
        $results = @(Read-Results $run.Result)
        $counts = @{ Pass = 0; Fail = 0; Skip = 0; NotRun = 0 }
        foreach ($test in $results) {
            $counts[$test.Result]++
            if ($test.Result -eq 'Fail') {
                [void]$failed.Add("$($run.Name): $(Get-NameKey $test.Name)")
            }
            elseif ($test.Result -eq 'Skip' -and ($test.Reason -like 'display is off: real-window checks not run*' -or $test.Reason -like 'Real-window tests *')) {
                # What the real-window tests skip with when they cannot see a window (RealWindow.Unavailable in the Desktop tests).
                [void]$realWindowSkips.Add([pscustomobject]@{ Name = (Get-NameKey $test.Name); Reason = $test.Reason })
            }

            if (-not $seenIn[$run.Project].ContainsKey($test.Class)) { $seenIn[$run.Project][$test.Class] = @{} }
            $seenIn[$run.Project][$test.Class][$run.Name] = $true
            $measured[$test.Class] = [double]$measured[$test.Class] + $test.Seconds
            if ($run.Project -eq 'Desktop') {
                $key = Get-NameKey $test.Name
                $ranNames[$key] = [int]$ranNames[$key] + 1
            }
            elseif ($run.Project -eq 'Api' -and $test.Collection -notlike 'Test collection for *') {
                if (-not $collections.ContainsKey($test.Collection)) { $collections[$test.Collection] = @{} }
                $collections[$test.Collection][$run.Name] = $true
            }
        }

        $ran[$run.Project] = [int]$ran[$run.Project] + $results.Count
        $ended = $run.StartedAt.AddSeconds($run.Seconds)
        if (-not $span.ContainsKey($run.Project)) {
            $span[$run.Project] = @{ From = $run.StartedAt; To = $ended }
        }
        else {
            if ($run.StartedAt -lt $span[$run.Project].From) { $span[$run.Project].From = $run.StartedAt }
            if ($ended -gt $span[$run.Project].To) { $span[$run.Project].To = $ended }
        }

        $note = $run.Note
        if ($run.TimedOut) {
            $note = "ENDED after $TimeoutMinutes minutes; $note"
            [void]$problems.Add("$($run.Name) did not finish in $TimeoutMinutes minutes and was ended.")
        }
        elseif ($results.Count -eq 0) {
            $note = "NO RESULTS (exit code $($run.ExitCode)); $note"
            [void]$problems.Add("$($run.Name) reported no results (exit code $($run.ExitCode)); see $($run.Log).")
        }
        elseif ($run.ExitCode -ne 0 -and $counts.Fail -eq 0) {
            $note = "EXIT CODE $($run.ExitCode); $note"
            [void]$problems.Add("$($run.Name) exited with $($run.ExitCode) although no test failed; see $($run.Log).")
        }

        if ($counts.NotRun -gt 0) {
            [void]$problems.Add("$($run.Name): $($counts.NotRun) test(s) were not run.")
        }

        [void]$rows.Add([pscustomobject]@{ Name = $run.Name; Passed = $counts.Pass; Failed = $counts.Fail; Skipped = $counts.Skip; Seconds = $run.Seconds; Cpu = $run.Cpu; Note = $note })
    }

    foreach ($name in $projects.Keys) {
        if ([int]$ran[$name] -ne $expected[$name]) {
            [void]$problems.Add("$name ran $([int]$ran[$name]) tests but the unsharded executable lists $($expected[$name]).")
        }
    }

    # Every listed Desktop test ran exactly once...
    foreach ($name in $listedNames.Keys) {
        if ([int]$ranNames[$name] -ne $listedNames[$name]) {
            [void]$problems.Add("Desktop test '$name' ran $([int]$ranNames[$name]) time(s); the unsharded executable lists it $($listedNames[$name]) time(s).")
        }
    }

    foreach ($name in $ranNames.Keys) {
        if (-not $listedNames.ContainsKey($name)) {
            [void]$problems.Add("Desktop test '$name' ran but the unsharded executable does not list it.")
        }
    }

    # ...every class ran in the shard it was given to and in no other (a split class: in its own parts and nowhere else)...
    foreach ($project in 'Api', 'Desktop') {
        foreach ($class in $classes[$project]) {
            $ranIn = @()
            if ($seenIn[$project].ContainsKey($class)) { $ranIn = @($seenIn[$project][$class].Keys | Sort-Object) }
            $givenTo = @($shardsOfClass[$project][$class].Keys | Sort-Object)
            if (($ranIn -join ',') -ne ($givenTo -join ',')) {
                [void]$problems.Add("Class $class ran in [$($ranIn -join ', ')] but was given to [$($givenTo -join ', ')].")
            }
        }

        foreach ($class in $seenIn[$project].Keys) {
            if ($classes[$project] -notcontains $class) {
                [void]$problems.Add("Class $class ran but the unsharded executable does not list it.")
            }
        }
    }

    # ...and an Api collection that several classes share (one that runs its classes one at a time) stayed in one process.
    foreach ($collection in $collections.Keys) {
        if ($collections[$collection].Count -ne 1) {
            [void]$problems.Add("The Api collection '$collection' ran in $($collections[$collection].Count) processes ($(($collections[$collection].Keys | Sort-Object) -join ', ')): list its classes together under apiLast or apiTogether in $Durations.")
        }
    }

    $measuredFile = Join-Path $Out 'durations.json'
    $ordered = [ordered]@{}
    foreach ($class in ($measured.Keys | Where-Object { $_ -like 'Lifestream.Api.Tests.*' -or $_ -like 'Lifestream.Desktop.Tests.*' } | Sort-Object)) {
        $ordered[$class] = [Math]::Round($measured[$class], 1)
    }

    ([ordered]@{ ownShard = @($settings.ownShard); split = $split; apiTogether = @($settings.apiTogether); apiLast = @($settings.apiLast); seconds = $ordered } | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $measuredFile -Encoding ascii
    if ($UpdateDurations) {
        Copy-Item -LiteralPath $measuredFile -Destination (Join-Path $script:Checkout 'tools\test-fast.durations.json') -Force
    }

    Write-Host ''
    Write-Host ('{0,-12} {1,7} {2,7} {3,8} {4,8} {5,6}  {6}' -f 'Project', 'Passed', 'Failed', 'Skipped', 'Seconds', 'CPU s', '')
    foreach ($row in $rows) {
        Write-Host ('{0,-12} {1,7} {2,7} {3,8} {4,8:N1} {5,6:N0}  {6}' -f $row.Name, $row.Passed, $row.Failed, $row.Skipped, $row.Seconds, $row.Cpu, $row.Note)
    }

    $sum = @{ Passed = 0; Failed = 0; Skipped = 0 }
    foreach ($row in $rows) { $sum.Passed += $row.Passed; $sum.Failed += $row.Failed; $sum.Skipped += $row.Skipped }
    Write-Host ('{0,-12} {1,7} {2,7} {3,8} {4,8:N1} {5,6}  {6}' -f 'Total', $sum.Passed, $sum.Failed, $sum.Skipped, $tests.Elapsed.TotalSeconds, '', 'wall time of the tests')
    Write-Host ''
    Write-Host ('Wall time by project: ' + (($projects.Keys | ForEach-Object { '{0} {1:N1} s' -f $_, ($span[$_].To - $span[$_].From).TotalSeconds }) -join ', ') + " (Api in $($plans.Api.Count) processes, Desktop in $($plans.Desktop.Count)).")
    Write-Host ('Counts: ' + (($projects.Keys | ForEach-Object { "$_ $([int]$ran[$_]) of $($expected[$_])" }) -join ', ') + "; $($classes.Api.Count) Api and $($classes.Desktop.Count) Desktop classes, each in one shard" + $(if ($split.Count -gt 0) { " ($($split.Count) Desktop classes split over shards of their own)" } else { '' }) + '.')
    if (Test-Path -LiteralPath $templateLog) {
        Get-Content -LiteralPath $templateLog | Sort-Object -Unique | ForEach-Object { Write-Host "Api $_" }
    }

    Write-Host "Real-window checks not run: $($realWindowSkips.Count)"
    if ($realWindowSkips.Count -gt 0) {
        foreach ($group in ($realWindowSkips | Group-Object Reason)) {
            Write-Host "  why: $($group.Name)"
            $group.Group | Sort-Object Name | ForEach-Object { Write-Host "    $($_.Name)" }
        }

        if ($AllowRealWindowSkip) {
            Write-Host '  accepted for this run (-AllowRealWindowSkip)'
        }
        else {
            [void]$problems.Add("$($realWindowSkips.Count) real-window check(s) did not run ($((@($realWindowSkips | ForEach-Object { $_.Reason } | Sort-Object -Unique)) -join '; ')): they are named above. A release gate must not lose them: run again with the display on and the session unlocked, or pass -AllowRealWindowSkip to accept a run without them.")
        }
    }

    if ($failed.Count -gt 0) {
        Write-Host ''
        Write-Host "Failed tests ($($failed.Count)):"
        $failed | ForEach-Object { Write-Host "  $_" }
    }

    if ($problems.Count -gt 0) {
        $problems | Set-Content -LiteralPath (Join-Path $Out 'problems.txt') -Encoding utf8
        Write-Host ''
        Write-Host "Problems ($($problems.Count); all of them in $(Join-Path $Out 'problems.txt')):"
        $problems | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" }
    }

    Write-Host ''
    Write-Host ('Tests: {0:N1} s wall. Build: {1:N1} s.' -f $tests.Elapsed.TotalSeconds, $buildSeconds)
    if ($failed.Count -eq 0 -and $problems.Count -eq 0) {
        Write-Host 'PASSED'
        $exitCode = 0
    }
    else {
        Write-Host 'FAILED'
    }
}
finally {
    foreach ($run in $script:Started) {
        Stop-Tree $run
    }

    $self.ProcessorAffinity = $before.Affinity
    $self.PriorityClass = $before.Priority
    $env:DOTNET_ROOT = $before.Root
    $env:DOTNET_HOST_PATH = $before.HostPath
    $env:DOTNET_TieredPGO = $before.Pgo
    $env:LIFESTREAM_SCALE_TEMPLATE_LOG = $before.TemplateLog
    $env:MSBUILDDISABLENODEREUSE = $before.Reuse
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = $before.Server
    if ($displayHeld) {
        [void][LifestreamTestFast.Power]::SetThreadExecutionState([uint32]2147483648) # continuous only: the display may turn off again
    }

    if ($poolBefore) {
        [void][System.Threading.ThreadPool]::SetMinThreads($poolBefore[0], $poolBefore[1])
    }

    if ($held) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}

exit $exitCode
