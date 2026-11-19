# INFRA-01 (container image): proves tools/scan-image.sh scans the image FILESYSTEM (every layer) and the image configuration.
# No Docker daemon: each test crafts a docker-archive tarball (layer tars, config.json, manifest.json) and tools/scan-image.sh runs
# unchanged against tests/infra/fake-docker.sh, which serves that archive the way `docker save` / `docker export` would.
# Credentials are assembled from fragments so this file is itself clean when the repository is scanned.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:ScanImage = Join-Path $script:Root 'tools' 'scan-image.sh'
    $script:FakeDocker = Join-Path $PSScriptRoot 'fake-docker.sh'
    $script:Secret = 'Zq7x' + 'K2mP9'
    $script:ConnString = 'Server=tcp:lifestream-sql.database.windows.net,1433;Initial Catalog=BloodBank;User ' + 'ID=app;Pass' + 'word=' + $script:Secret

    # A bash with msys coreutils (tar, grep, mktemp). On Windows prefer Git's bash over the WSL launcher in System32.
    $bashCandidates = @('C:\Program Files\Git\bin\bash.exe', 'C:\Program Files\Git\usr\bin\bash.exe', '/usr/bin/bash', '/bin/bash') | Where-Object { Test-Path $_ }
    $script:Bash = @($bashCandidates)[0]
    $script:BashDir = if ($IsWindows) { Join-Path (Split-Path (Split-Path $script:Bash)) 'usr' 'bin' } else { Split-Path $script:Bash }
    $script:HostPwshDir = Split-Path (Get-Process -Id $PID).Path

    function script:Add-TarFile($writer, [string]$name, [string]$text) {
        $entry = [Formats.Tar.UstarTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $name)
        $entry.DataStream = [IO.MemoryStream]::new([Text.UTF8Encoding]::new($false).GetBytes($text))
        $writer.WriteEntry($entry)
    }

    # Layers: array of hashtables name -> text. Config: the image's config JSON text.
    function script:New-ImageArchive([string]$Path, [hashtable[]]$Layers, [string]$ConfigJson) {
        $outer = [IO.File]::Create($Path)
        $w = [Formats.Tar.TarWriter]::new($outer, [Formats.Tar.TarEntryFormat]::Ustar, $false)
        try {
            Add-TarFile $w 'config.json' $ConfigJson
            $names = @()
            for ($i = 0; $i -lt $Layers.Count; $i++) {
                $ms = [IO.MemoryStream]::new()
                $lw = [Formats.Tar.TarWriter]::new($ms, [Formats.Tar.TarEntryFormat]::Ustar, $true)
                foreach ($k in $Layers[$i].Keys) { Add-TarFile $lw $k $Layers[$i][$k] }
                $lw.Dispose()
                $e = [Formats.Tar.UstarTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, "layer$i.tar")
                $ms.Position = 0; $e.DataStream = $ms; $w.WriteEntry($e)
                $names += "layer$i.tar"
            }
            Add-TarFile $w 'manifest.json' (ConvertTo-Json -InputObject @(@{ Config = 'config.json'; RepoTags = @('lifestream-api:test'); Layers = $names }) -Compress)
        } finally { $w.Dispose(); $outer.Dispose() }
    }

    function script:Invoke-ScanImage([string]$Archive, [string]$Report) {
        $state = Join-Path $TestDrive ('state-' + [guid]::NewGuid().ToString('N'))
        $shimDir = Join-Path $TestDrive ('shim-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $shimDir | Out-Null
        # LF line endings: the shim is run by bash.
        [IO.File]::WriteAllText((Join-Path $shimDir 'docker'), ((Get-Content -Raw $script:FakeDocker) -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        $psi = [Diagnostics.ProcessStartInfo]::new($script:Bash)
        $psi.ArgumentList.Add($script:ScanImage); $psi.ArgumentList.Add('lifestream-api:test'); if ($Report) { $psi.ArgumentList.Add($Report) }
        $sep = [IO.Path]::PathSeparator
        $psi.Environment['PATH'] = (@($shimDir, $script:BashDir, $script:HostPwshDir) -join $sep) + $sep + $env:PATH
        $psi.Environment['FAKE_DOCKER_ARCHIVE'] = $Archive
        $psi.Environment['FAKE_DOCKER_STATE'] = $state
        $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $p = [Diagnostics.Process]::Start($psi)
        $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync(); $p.WaitForExit()
        [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $o.Result + $e.Result }
    }

    function script:New-Config([string[]]$Env = @('PATH=/usr/bin')) {
        ConvertTo-Json -Compress -Depth 5 @{ architecture = 'amd64'; os = 'linux'; config = @{ Env = $Env; Cmd = @('dotnet', 'app.dll') } }
    }
}

Describe 'INFRA-01 container image: tools/scan-image.sh scans the image filesystem and the image configuration' {
    It 'found a bash with tar to run scan-image.sh' {
        $script:Bash | Should -Not -BeNullOrEmpty
    }

    It 'exits 0 on a clean image, and scanned the layer files (the fixture really is exported)' {
        $a = Join-Path $TestDrive 'clean.tar'
        New-ImageArchive $a @(@{ 'app/appsettings.json' = '{ "Logging": { "LogLevel": { "Default": "Information" } } }'; 'app/readme.txt' = 'hello' }) (New-Config)
        $r = Invoke-ScanImage $a (Join-Path $TestDrive 'clean-report.json')
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $report = Get-Content -Raw (Join-Path $TestDrive 'clean-report.json') | ConvertFrom-Json
        # 2 layer files + _image-inspect.json + _image-history.txt
        $report.filesScanned | Should -BeGreaterOrEqual 4
    }

    It 'fails when a credential file is inside the image filesystem (first layer), naming the file but not the value' {
        $a = Join-Path $TestDrive 'fs1.tar'
        New-ImageArchive $a @(@{ 'app/appsettings.Production.json' = ('{ "ConnectionStrings": { "Default": "' + $script:ConnString + '" } }') }) (New-Config)
        $r = Invoke-ScanImage $a
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'appsettings\.Production\.json'
        $r.Output | Should -Not -Match $script:Secret
    }

    It 'fails when the credential is in a later layer and the image configuration is clean' {
        $a = Join-Path $TestDrive 'fs2.tar'
        New-ImageArchive $a @(@{ 'app/ok.txt' = 'fine' }, @{ 'opt/secret.config' = ('<add connectionString="' + $script:ConnString + '" />') }) (New-Config)
        $r = Invoke-ScanImage $a
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'secret\.config'
    }

    It 'fails when the credential is only in the image configuration (inspect JSON: baked environment variable)' {
        $a = Join-Path $TestDrive 'cfg.tar'
        New-ImageArchive $a @(@{ 'app/ok.txt' = 'fine' }) (New-Config @('PATH=/usr/bin', ('ConnectionStrings__Default=' + $script:ConnString)))
        $r = Invoke-ScanImage $a
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match '_image-inspect\.json'
        $r.Output | Should -Not -Match $script:Secret
    }

    It 'fails when the credential is UTF-16LE in a binary file in the filesystem' {
        $a = Join-Path $TestDrive 'utf16.tar'
        $bin = [Text.Encoding]::Latin1.GetString([Text.Encoding]::Unicode.GetBytes($script:ConnString))
        New-ImageArchive $a @(@{ 'app/Some.dll' = $bin }) (New-Config)
        # Latin-1 round trip through UTF-8 would double-encode; write the layer bytes with a Latin-1-safe path instead.
        $r = Invoke-ScanImage $a
        # The helper writes text as UTF-8; bytes >= 0x80 do not occur in this ASCII credential, only NULs, which UTF-8 keeps.
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'UTF-16LE'
    }
}
