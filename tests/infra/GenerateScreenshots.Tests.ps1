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

Describe 'INFRA-19 the screenshots come from the real demo dataset, never from the fake-data tests' {
    BeforeAll {
        $script:Repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
        $script:Shots = Get-Content (Join-Path $script:Repo 'tools' 'generate-screenshots.ps1') -Raw
        $script:Tests = Join-Path $script:Repo 'tests' 'Lifestream.Desktop.Tests'
    }

    It 'the real run seeds a fresh database with the demo generator, starts the real API, runs only the real-client capture test and drops the database in a finally block' {
        $script:Shots | Should -Match 'Lifestream\.Perf\.Seed'
        $script:Shots | Should -Match '--factor 1'
        $script:Shots | Should -Match 'LIFESTREAM_SHOTS_API'
        $script:Shots | Should -Match "--filter 'Category=Screenshots'"
        $script:Shots | Should -Match '(?s)finally \{.*Stop-Quietly.*drop --name'
    }

    It 'exactly one test is tagged Category=Screenshots, and it uses the real API and the real demo generator, not the fake API or DemoData.cs' {
        $tagged = @(Get-ChildItem $script:Tests -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' } | Where-Object { (Get-Content $_.FullName -Raw) -match 'Trait\("Category", "Screenshots"\)' })
        $tagged.Count | Should -Be 1
        $tagged[0].Name | Should -Be 'DemoScreenshotTests.cs'
        $body = Get-Content $tagged[0].FullName -Raw
        $body | Should -Match 'RealRig\.CreateSignedOut'
        $body | Should -Match 'DemoCommand\.ExecuteAsync'
        $body | Should -Match 'DemoDataGenerator'
        $body | Should -Not -Match 'DemoData\.Configure|FeatureHarness|Part2Harness|new Rig\('
    }

    It 'no screenshot test that uses the fake API is tagged Category=Screenshots any more' {
        foreach ($f in 'ShellScreenTests.cs', 'Part2ShellScreenTests.cs') {
            (Get-Content (Join-Path $script:Tests 'Features' $f) -Raw) | Should -Not -Match 'Category", "Screenshots'
        }
    }

    It 'the manifest of a real run records what the dashboard showed (fake capture that writes dataset.json)' {
        $fake = Join-Path $TestDrive 'withdata.ps1'
        @"
Add-Type -AssemblyName System.Drawing
`$bmp = New-Object System.Drawing.Bitmap 1366, 768
`$bmp.Save((Join-Path `$env:LIFESTREAM_CAPTURE_DIR 'login.png'), [System.Drawing.Imaging.ImageFormat]::Png); `$bmp.Dispose()
'{"dataset":"demo","generator":"DemoDataGenerator","today":"2026-11-19","dashboard":{"Donors":40,"Patients":35}}' | Set-Content (Join-Path `$env:LIFESTREAM_CAPTURE_DIR 'dataset.json')
"@ | Set-Content $fake
        $out = Join-Path $TestDrive 'oD'; $store = Join-Path $TestDrive 'sD'
        $r = Invoke-Shots $fake @('login') '' $out $store
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $m = Get-Content (Join-Path $out 'screenshots.json') -Raw | ConvertFrom-Json
        $m.dashboard.Donors | Should -Be 40
        $m.source | Should -Match 'Try the demo'
    }
}
