<#
.SYNOPSIS
Runs the real app and its UI Automation driver (tests/realapp) on an isolated desktop, with the person's desktop watched.

.DESCRIPTION
The real app is never started on the desktop of the person at the PC: on 2026-11-20 its off-screen windows there took the
owner's keyboard focus. This script creates a desktop of its own (tests/isolated/Lifestream.Isolated: never switched to,
a job object for everything started there) and starts the app and the driver on it; a monitor watches the person's input
desktop every 10 ms the whole time, and anything of ours reaching it (a window, the foreground, the focus, the caret) ends
everything on the isolated desktop at once (exit code 3). The app (LIFESTREAM_OFFSCREEN=1) and the driver refuse to run
anywhere else. There the app's windows behave as for a user (activation, focus, popups, maximizing) and are drawn in
software. Nothing here sends input to the person's desktop.

  start  creates the desktop, starts the monitor and launches Lifestream.exe against -Api; writes <Work>/pid.
  run    runs a driver script (tests/realapp/Lifestream.RealApp/Program.cs lists the commands) against the app; captures go to -Captures.
  stop   ends the app, the desktop and the monitor, and prints what the monitor saw. Exit code 3 if anything reached the person's desktop.

Build first: the Desktop app (Release), tests/realapp/Lifestream.RealApp and tests/isolated/Lifestream.Isolated.

.EXAMPLE
tools/realapp-isolated.ps1 start -Work G:/scratch/walk -Api http://localhost:5199/
tools/realapp-isolated.ps1 run -Work G:/scratch/walk -Script walk.txt -Captures G:/scratch/walk/captures
tools/realapp-isolated.ps1 stop -Work G:/scratch/walk
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('start', 'run', 'stop')][string]$Command,
    # A folder for this session: the monitor's state and log, the app's preferences and log, the pid.
    [Parameter(Mandatory)][string]$Work,
    # start: the API the app talks to.
    [string]$Api,
    # start: the theme the app starts in.
    [ValidateSet('light', 'dark', 'system')][string]$Theme = 'light',
    # run: the driver script, and where its captures go.
    [string]$Script,
    [string]$Captures,
    # The checkout (default: the one this script is in), dotnet.exe of the SDK (default: tools/dotnet beside the worktrees folder).
    [string]$Worktree,
    [string]$Dotnet,
    # start: the processors the isolated desktop's processes may use, and the longest the session may last.
    [long]$Affinity = 0x3,
    [int]$Minutes = 120,
    # run: the longest one script may take.
    [int]$TimeoutSeconds = 1800
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$checkout = if ($Worktree) { (Resolve-Path -LiteralPath $Worktree).ProviderPath } else { Split-Path -Parent $PSScriptRoot }
if (-not $Dotnet) {
    $beside = Join-Path (Split-Path -Parent (Split-Path -Parent $checkout)) 'tools\dotnet\dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $beside) { $beside } else { (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source }
}

# The tools are apphosts: without DOTNET_ROOT a machine without .NET 11 installed would show a dialog for a GUI one.
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
$tool = Join-Path $checkout 'tests\isolated\Lifestream.Isolated\bin\Release\net11.0-windows\Lifestream.Isolated.exe'
$driver = Join-Path $checkout 'tests\realapp\Lifestream.RealApp\bin\Release\net11.0-windows\Lifestream.RealApp.exe'
$app = @(Get-ChildItem -Path (Join-Path $checkout 'src\Lifestream.Desktop\bin\Release') -Filter 'Lifestream.exe' -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
foreach ($needed in @($tool, $driver)) {
    if (-not (Test-Path -LiteralPath $needed)) {
        throw "Not built: $needed"
    }
}

[void](New-Item -ItemType Directory -Force -Path $Work)
$Work = (Resolve-Path -LiteralPath $Work).ProviderPath
$state = Join-Path $Work 'isolated'

function Invoke-Exec([string[]]$arguments, [int]$timeout) {
    & $tool exec $state --timeout "$timeout" -- @arguments | Out-Host
    return $LASTEXITCODE
}

switch ($Command) {
    'start' {
        if (-not $Api) { throw '-Api is needed to start the app.' }
        if ($app.Count -eq 0) { throw 'No Release build of Lifestream.exe.' }
        $hold = Start-Process -FilePath $tool -ArgumentList @('hold', "`"$state`"", '--minutes', "$Minutes", '--affinity', ('0x{0:X}' -f $Affinity)) -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $Work 'hold.out') -RedirectStandardError (Join-Path $Work 'hold.err')
        $ready = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath (Join-Path $state 'ready'))) {
            if ($hold.HasExited -or $ready.Elapsed.TotalSeconds -gt 30) {
                throw "The isolated desktop could not be set up; see $(Join-Path $Work 'hold.err')."
            }

            Start-Sleep -Milliseconds 100
        }

        $code = Invoke-Exec @($driver, 'launch', $app[0].FullName, $Api, $Work, "--$Theme") 120
        if ($code -ne 0) {
            & $tool stop $state | Out-Null
            throw "The app could not be launched on the isolated desktop (exit code $code)."
        }

        Write-Host "isolated desktop $((Get-Content -LiteralPath (Join-Path $state 'desktop.txt') -Raw).Trim()); app pid $((Get-Content -LiteralPath (Join-Path $Work 'pid') -Raw).Trim())"
    }

    'run' {
        if (-not $Script -or -not $Captures) { throw '-Script and -Captures are needed to run a script.' }
        $appPid = (Get-Content -LiteralPath (Join-Path $Work 'pid') -Raw).Trim()
        [void](New-Item -ItemType Directory -Force -Path $Captures)
        $code = Invoke-Exec @($driver, 'run', $appPid, (Resolve-Path -LiteralPath $Captures).ProviderPath, (Resolve-Path -LiteralPath $Script).ProviderPath) $TimeoutSeconds
        exit $code
    }

    'stop' {
        & $tool stop $state
        exit $LASTEXITCODE
    }
}
