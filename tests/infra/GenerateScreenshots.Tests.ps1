# Tests for tools/generate-screenshots.ps1. The capture step is replaced by a FAKE command that draws plain PNGs, so these
# prove the script's checks, copies and manifest; the real capture tests (Category=Screenshots) belong to the client modules.

BeforeAll {
    $script:Script = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools' 'generate-screenshots.ps1')).Path
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }

    # A script file the child process runs instead of dotnet test: writes PNGs of the given size for the given screens.
    function script:New-FakeCapture([string]$name, [string[]]$screens, [int]$w = 1366, [int]$h = 768) {
        $f = Join-Path $TestDrive "$name.ps1"
        @"
Add-Type -AssemblyName System.Drawing
foreach (`$s in '$($screens -join "','")') {
    `$bmp = New-Object System.Drawing.Bitmap $w, $h
    `$g = [System.Drawing.Graphics]::FromImage(`$bmp); `$g.Clear([System.Drawing.Color]::FromArgb(200,16,46)); `$g.Dispose()
    `$bmp.Save((Join-Path `$env:LIFESTREAM_CAPTURE_DIR "`$s.png"), [System.Drawing.Imaging.ImageFormat]::Png); `$bmp.Dispose()
}
"@ | Set-Content $f
        $f
    }

    function script:Invoke-Shots([string]$fake, [string[]]$screens, [string]$sql = '', [string]$out, [string]$store) {
        $a = @($script:Script, '-OutputDir', $out, '-StoreDir', $store, '-ExpectedScreens', ($screens -join ','), '-CaptureScript', $fake)
        if ($sql) { $a += @('-SqlConnection', $sql) }
        $text = & $script:Pwsh @script:PwshArgs @a 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $text }
    }
}

Describe 'INFRA-19 generate-screenshots.ps1' {
    It 'copies every expected screen to the README and Store folders and records the demo dataset in a manifest (fake capture)' {
        $fake = New-FakeCapture 'good' @('login', 'dashboard')
        $out = Join-Path $TestDrive 'readme'; $store = Join-Path $TestDrive 'store'
        $r = Invoke-Shots $fake @('login', 'dashboard') '' $out $store
        $r.ExitCode | Should -Be 0 -Because $r.Output
        foreach ($d in $out, $store) {
            Test-Path (Join-Path $d 'login.png') | Should -BeTrue
            Test-Path (Join-Path $d 'dashboard.png') | Should -BeTrue
            $m = Get-Content (Join-Path $d 'screenshots.json') -Raw | ConvertFrom-Json
            $m.dataset | Should -Be 'demo'
            @($m.files).Count | Should -Be 2
            $m.files[0].width | Should -Be 1366
            $m.files[0].sha256 | Should -Match '^[0-9a-f]{64}$'
        }
    }

    It 'fails with exit 2 when an expected screen was not captured (fake capture)' {
        $fake = New-FakeCapture 'partial' @('login')
        $r = Invoke-Shots $fake @('login', 'donors') '' (Join-Path $TestDrive 'o2') (Join-Path $TestDrive 's2')
        $r.ExitCode | Should -Be 2
        $r.Output | Should -Match "missing screenshot for 'donors'"
        Test-Path (Join-Path $TestDrive 'o2' 'login.png') | Should -BeFalse -Because 'nothing is published when a screen is missing'
    }

    It 'fails with exit 2 when an image is smaller than the Store minimum (fake capture)' {
        $fake = New-FakeCapture 'small' @('login') 800 600
        $r = Invoke-Shots $fake @('login') '' (Join-Path $TestDrive 'o3') (Join-Path $TestDrive 's3')
        $r.ExitCode | Should -Be 2
        $r.Output | Should -Match 'smaller than 1366x768'
    }

    It 'refuses to run against Azure SQL or a credentialed connection, before capturing anything' {
        $fake = New-FakeCapture 'never' @('login')
        $azure = 'Server=tcp:lifestream-server.database.windows.net,1433;Authentication=Active Directory Default'
        $r = Invoke-Shots $fake @('login') $azure (Join-Path $TestDrive 'o4') (Join-Path $TestDrive 's4')
        $r.ExitCode | Should -Be 2
        $r.Output | Should -Match 'refusing'
        Test-Path (Join-Path $TestDrive 'o4') | Should -BeFalse

        $pw = 'Server=localhost;User ' + 'ID=sa;Pass' + 'word=x'
        (Invoke-Shots $fake @('login') $pw (Join-Path $TestDrive 'o5') (Join-Path $TestDrive 's5')).ExitCode | Should -Be 2
    }

    It 'fails with exit 2 when the capture produced no images at all, as the real run does until the capture tests exist' {
        $fake = Join-Path $TestDrive 'nothing.ps1'; Set-Content $fake '# captures nothing'
        $r = Invoke-Shots $fake @('login') '' (Join-Path $TestDrive 'o6') (Join-Path $TestDrive 's6')
        $r.ExitCode | Should -Be 2
    }
}
