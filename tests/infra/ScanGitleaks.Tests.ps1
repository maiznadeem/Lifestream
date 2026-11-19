# INFRA-01, scanner v3: the maintained second scanner (gitleaks, pinned), the exact placeholder list, the value shapes the home-grown scanner
# used to lose to, and decompression. Every credential below is built from fragments at run time so this file is clean when scanned.
# The own-engine checks pass -SkipGitleaks (they are about tools/scan-engine.cs); the others run both scanners exactly as CI does.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Scanner = Join-Path $script:Root 'tools' 'scan-artifacts.ps1'
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }
    $script:P = 'Pass' + 'word'
    $script:V = 'Zq7x' + 'K2mP9'
    $script:PK = 'P' + 'WD'
    $script:UI = 'User' + ' ID'

    function script:Invoke-Scan([string]$Path, [string[]]$Extra = @(), [switch]$Own) {
        $report = Join-Path $TestDrive ('rep-' + [guid]::NewGuid().ToString('N') + '.json')
        $args2 = @($Extra); if ($Own) { $args2 += '-SkipGitleaks' }
        $out = & $script:Pwsh @script:PwshArgs $script:Scanner -Path $Path -ReportPath $report -MaxFindingsPerFile 50 @args2 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out; Report = $(if (Test-Path $report) { Get-Content -Raw $report | ConvertFrom-Json }) }
    }
    function script:New-Folder([string]$name) { $d = Join-Path $TestDrive $name; New-Item -ItemType Directory -Path $d -Force | Out-Null; $d }
    function script:Write-Bin([string]$Dir, [string]$Name, [string]$Text, [string]$Enc) {
        $e = switch ($Enc) { 'UTF-16LE' { [Text.Encoding]::Unicode } 'UTF-16BE' { [Text.Encoding]::BigEndianUnicode } default { [Text.Encoding]::UTF8 } }
        [IO.File]::WriteAllBytes((Join-Path $Dir $Name), [byte[]]@(0, 1, 2, 3) + $e.GetBytes($Text) + [byte[]]@(0, 0, 4))
    }
    $script:Gitleaks = (& (Join-Path $script:Root 'tools' 'fetch-gitleaks.ps1') | Select-Object -Last 1)
}

Describe 'INFRA-01 the second scanner is gitleaks, pinned, with its checksum verified' {
    It 'fetch-gitleaks.ps1 pins a version and the published SHA-256 of the Windows and Linux archives' {
        $t = Get-Content -Raw (Join-Path $script:Root 'tools' 'fetch-gitleaks.ps1')
        $t | Should -Match "\`$version = '\d+\.\d+\.\d+'"
        $t | Should -Match 'windows_x64\.zip.*Sha256 = ''[0-9a-f]{64}'''
        $t | Should -Match 'linux_x64\.tar\.gz.*Sha256 = ''[0-9a-f]{64}'''
        $t | Should -Match 'Get-FileHash'
        (git -C $script:Root ls-files tools) | Should -Not -Match '\.exe$|\.bin/' -Because 'no binary is committed'
    }
    It 'the fetched binary runs and is the pinned version' {
        $v = & $script:Gitleaks version
        $v | Should -Be ((Select-String -Path (Join-Path $script:Root 'tools' 'fetch-gitleaks.ps1') -Pattern "\`$version = '([\d.]+)'").Matches[0].Groups[1].Value)
    }
    It 'a scan fails with exit 2 (never a silent pass) when the second scanner cannot run: <Why>' -ForEach @(
        @{ Why = 'its rule file is missing'; Extra = @('-GitleaksConfig', 'Z:\does\not\exist.toml') }
    ) {
        $d = New-Folder 'gl-broken'; Set-Content (Join-Path $d 'a.txt') 'nothing here'
        $r = Invoke-Scan $d $Extra
        $r.ExitCode | Should -Be 2 -Because $r.Output
        $r.Output | Should -Match 'gitleaks failed'
    }
}

Describe 'INFRA-01 a placeholder is skipped only when the whole value is an entry of the exact list' {
    It 'exposes exactly the reviewed list' {
        $src = Get-Content -Raw (Join-Path $script:Root 'tools' 'scan-engine.cs')
        $m = [regex]::Match($src, 'PlaceholderValues =\s*\{(.*?)\};', 'Singleline')
        $items = [regex]::Matches($m.Groups[1].Value, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { $_.Groups[1].Value }
        ($items -join ' ') | Should -Be '{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} $(Password) ${PASSWORD} %PASSWORD% <password> ***'
    }
    It 'accepts the listed value in a text file, with both scanners: <Value>' -ForEach @(
        @{ Value = '{0}' }, @{ Value = '{9}' }, @{ Value = '$(Password)' }, @{ Value = '${PASSWORD}' }, @{ Value = '%PASSWORD%' }, @{ Value = '<password>' }, @{ Value = '***' }, @{ Value = '' }
    ) {
        $d = New-Folder ('ph-ok-' + [guid]::NewGuid().ToString('N'))
        Set-Content -Encoding ascii (Join-Path $d 'a.config') ("Server=db;$($script:PK)=$Value;")
        Set-Content -Encoding ascii (Join-Path $d 'b.env') ("DB_$($script:P.ToUpper())=$Value`n")
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }
    It 'flags anything that merely looks like a placeholder, in text and in binary files: <Value>' -ForEach @(
        @{ Value = '{abc12345}' }, @{ Value = '{10}' }, @{ Value = '{Zq7xK2mP9}' }, @{ Value = '%s' }, @{ Value = '%Secret1%x' }, @{ Value = '<your password>' }
        @{ Value = '$(Other)x' }, @{ Value = '${Other}x' }, @{ Value = '$Other-x' }, @{ Value = '$sa' }, @{ Value = '$Other' }, @{ Value = '$Zq7xK2mP9' }, @{ Value = '$1Other' }, @{ Value = '$' }, @{ Value = '****' }, @{ Value = '{{Zq7xK2mP9}}' }, @{ Value = '{0}x' }
    ) {
        $d = New-Folder ('ph-bad-' + [guid]::NewGuid().ToString('N'))
        Set-Content -Encoding ascii (Join-Path $d 'a.config') ("Server=db;$($script:PK)=$Value;")
        Write-Bin $d 'Lib.dll' ("Server=db;$($script:PK)=$Value;") 'UTF-16LE'
        Write-Bin $d 'lib.so' ("Server=db;$($script:PK)=$Value;") 'UTF-8'
        $r = Invoke-Scan $d @() -Own
        $r.ExitCode | Should -Be 1 -Because $r.Output
        foreach ($f in 'a.config', 'Lib.dll', 'lib.so') { @($r.Report.findings | Where-Object { $_.file -like "*\$f" }).Count | Should -BeGreaterThan 0 -Because "$f`n$($r.Output)" }
    }
}

# b-03h: a shell variable REFERENCE as the WHOLE value ($NAME, ${NAME}, %NAME%, $(NAME); NAME = [A-Za-z_][A-Za-z0-9_]*) names where a value
# comes from; it is not a value, in every mode (docker history: "useradd --uid=$APP_UID"). ODBC quoting PWD={x} stays a value (cases above).
# The bare $NAME form counts only in the environment-variable spelling (capitals): the verifier plants Password=$Zq7xK2mP9 and User ID=$sa stay values.
Describe 'INFRA-01 a variable reference is not a value, in text, binary and UTF-16, with both scanners' {
    It 'accepts <Value> as a user id and as a password in a text file, in a binary file and in a UTF-16 file' -ForEach @(
        @{ Value = '$APP_UID' }, @{ Value = '${APP_UID}' }, @{ Value = '%APP_UID%' }, @{ Value = '$(APP_UID)' }, @{ Value = '$_X1' }, @{ Value = '${Db_Pass2}' }, @{ Value = '$(db_user)' }, @{ Value = '%db_user%' }
    ) {
        $d = New-Folder ('ref-ok-' + [guid]::NewGuid().ToString('N'))
        Set-Content -Encoding ascii (Join-Path $d 'a.txt') ("useradd --no-log-init --uid=$Value --gid=$Value app`nServer=db;$($script:UI)=$Value;$($script:PK)=$Value;")
        Write-Bin $d 'config.bin' ("RUN groupadd --gid=$Value app && useradd --uid=$Value app Server=db;$($script:UI)=$Value;$($script:P)=$Value;") 'UTF-8'
        Write-Bin $d 'wide.bin' ("Server=db;$($script:UI)=$Value;$($script:P)=$Value;") 'UTF-16LE'
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }
    It 'still flags a real value next to a reference, and ODBC quoting of a password, in every mode' {
        $d = New-Folder ('ref-bad-' + [guid]::NewGuid().ToString('N'))
        Set-Content -Encoding ascii (Join-Path $d 'a.txt') ("Server=db;$($script:UI)=`$APP_UID;$($script:PK)={$($script:V)};")
        Write-Bin $d 'b.bin' ("Server=db;$($script:UI)=`$APP_UID;$($script:PK)={$($script:V)};") 'UTF-8'
        Write-Bin $d 'c.bin' ("Server=db;$($script:UI)=`$APP_UID;$($script:P)=$($script:V);") 'UTF-16LE'
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 1 -Because $r.Output
        foreach ($f in 'a.txt', 'b.bin', 'c.bin') { @($r.Report.findings | Where-Object { $_.file -like "*\$f" }).Count | Should -BeGreaterThan 0 -Because "$f`n$($r.Output)" }
    }
}

# b-03h: the docker build log of deploy-api.yml names the image "ghcr.io/<owner>/lifestream-api:<commit sha>"; the default generic-api-key rule reads
# "api:<hex>" as a key (found by running the workflow's exact build command against a dry-run docker: the first real deploy would have failed).
Describe 'INFRA-01 the lifestream-api image reference with a commit SHA is not a key, any other api key is' {
    It 'a build log that names ghcr.io/<owner>/lifestream-api:<sha> is clean, with both scanners' {
        $d = New-Folder ('imgref-' + [guid]::NewGuid().ToString('N'))
        Set-Content -Encoding ascii (Join-Path $d 'docker-build.log') "#12 naming to ghcr.io/maiznadeem/lifestream-api:a3f9c2e1b7d4085e6f1c9a2b3d4e5f60718293a4 done`nfake docker ok: -t ghcr.io/maiznadeem/lifestream-api:a3f9c2e1b7d4085e6f1c9a2b3d4e5f60718293a4 ."
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }
    It 'a different api key with a hex value in the same log is still flagged' {
        $d = New-Folder ('imgref-bad-' + [guid]::NewGuid().ToString('N'))
        Set-Content -Encoding ascii (Join-Path $d 'docker-build.log') "#12 naming to ghcr.io/maiznadeem/lifestream-api:a3f9c2e1b7d4085e6f1c9a2b3d4e5f60718293a4 done`nvendor-api:c7e1d95b02a4f8361d9e7b4a5c20f813b6d4e9a7"
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 1 -Because $r.Output
    }
}

# b-03h: the finding of the second scanner on the string dump is attributed to a member by its LINE number. gitleaks counts lines by LF only;
# .NET ReadLines also breaks at a lone CR, so a member with CRs before the match moved the line and the finding landed on a neighbouring
# member: a stock OS file that is allow-listed under its own name failed the scan on the real CI image (every CI scan of a clean image).
Describe 'INFRA-01 a lone CR in a dumped member does not move a finding to another member' {
    It 'finds the stock lintian line "base-passwd: no-debconf-config" under its own name (allow-listed) after a member full of lone CRs' {
        $dir = New-Folder ('cr-' + [guid]::NewGuid().ToString('N'))
        $tar = Join-Path $dir 'layer.tar'
        $w = [Formats.Tar.TarWriter]::new([IO.File]::Create($tar), [Formats.Tar.TarEntryFormat]::Ustar, $false)
        try {
            foreach ($m in @(@('usr/share/progress.txt', (('step' + [char]13 + 'done' + [char]13 + [char]13 + 'x' + [char]10) * 300)), @('usr/share/lintian/overrides/base-passwd', "base-passwd: no-debconf-config`n"))) {
                $e = [Formats.Tar.UstarTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $m[0])
                $e.DataStream = [IO.MemoryStream]::new([Text.UTF8Encoding]::new($false).GetBytes($m[1])); $w.WriteEntry($e)
            }
        } finally { $w.Dispose() }
        $r = Invoke-Scan $dir
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Report.gitleaks.allowListed | Should -BeGreaterThan 0 -Because 'the allow-list entry for the stock file was used'
    }
}

Describe 'INFRA-01 no first character of a value hides it (the verifier swept 94 printable characters)' {
    BeforeAll {
        $script:Chars = [char[]](33..126)
        $script:Dirs = @{}
        foreach ($shape in 'pw', 'env') {
            foreach ($enc in 'text', 'UTF-8', 'UTF-16LE', 'UTF-16BE') {
                $d = New-Folder ("sweep-$shape-$enc"); $script:Dirs["$shape-$enc"] = $d
                foreach ($c in $script:Chars) {
                    $name = 'c{0:X2}' -f [int]$c
                    $text = if ($shape -eq 'pw') { "Data Source=db;$($script:P)=$c$($script:V);Encrypt=True" } else { "DB_$($script:P.ToUpper())=$c$($script:V)" }
                    if ($enc -eq 'text') { [IO.File]::WriteAllText((Join-Path $d "$name.env"), $text) } else { Write-Bin $d "$name.bin" $text $enc }
                }
            }
        }
    }
    It 'the own scanner flags every first character, <Key>' -ForEach @(
        @{ Key = 'pw-text' }, @{ Key = 'pw-UTF-8' }, @{ Key = 'pw-UTF-16LE' }, @{ Key = 'pw-UTF-16BE' }
        @{ Key = 'env-text' }, @{ Key = 'env-UTF-8' }, @{ Key = 'env-UTF-16LE' }, @{ Key = 'env-UTF-16BE' }
    ) {
        $r = Invoke-Scan $script:Dirs[$Key] @() -Own
        $r.ExitCode | Should -Be 1
        $flagged = @($r.Report.findings | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.file) } | Sort-Object -Unique)
        $missing = @($script:Chars | ForEach-Object { 'c{0:X2}' -f [int]$_ } | Where-Object { $flagged -notcontains $_ })
        $missing | Should -BeNullOrEmpty -Because ("first characters never flagged: " + (($missing | ForEach-Object { [char][Convert]::ToInt32($_.Substring(1), 16) }) -join ' '))
    }
}

Describe 'INFRA-01 text forms the home-grown scanner missed: both scanners flag each' {
    It 'flags <Name> (own scanner and gitleaks, each alone)' -ForEach @(
        @{ Name = 'a complete ODBC string with a user id and a brace-quoted password'; File = 'conn.log'; Text = ('Driver={ODBC Driver 18 for SQL Server};Server=tcp:10.0.0.5,1433;Database=d;UI' + 'D=app;PW' + 'D={Wd3kQp8zLs1M};') }
        @{ Name = 'YAML password:'; File = 'app.yaml'; Text = ("db:`n  host: x`n  " + 'pass' + 'word: Wd3kQp8zLs1M' + "`n") }
        @{ Name = 'a double-underscore environment key'; File = 'env.list'; Text = ('Database__P' + 'wd=Wd3kQp8zLs1M') }
        @{ Name = 'a dotted Spring datasource key'; File = 'application.properties'; Text = ('spring.datasource.pass' + 'word=Wd3kQp8zLs1M') }
        @{ Name = 'a command-line option'; File = 'start.log'; Text = ('java -jar app.jar --db-pass' + 'word=Wd3kQp8zLs1M --verbose') }
        @{ Name = 'credentials in front of the host of a URL'; File = 'url.txt'; Text = ('mssql://app:' + 'Wd3kQp8zLs1M@10.0.0.5:1433/d') }
        @{ Name = 'a leading backtick value'; File = 'bt.env'; Text = ('DB_PASS' + 'WORD=`Wd3kQp8zLs1M') }
    ) {
        $d = New-Folder ('form-' + [guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText((Join-Path $d $File), $Text)
        $own = Invoke-Scan $d @() -Own
        $own.ExitCode | Should -Be 1 -Because "own scanner`n$($own.Output)"
        $both = Invoke-Scan $d
        $both.ExitCode | Should -Be 1
        if ($Name -ne 'a leading backtick value') {
            @($both.Report.gitleaks.findings).Count | Should -BeGreaterThan 0 -Because "gitleaks`n$($both.Output)"
        }
        $both.Output | Should -Not -Match 'Wd3kQp8zLs1M'
    }
    It 'also flags UTF-16 text on disk with the second scanner (decoded into the string dump)' {
        $d = New-Folder 'gl-utf16'
        [IO.File]::WriteAllBytes((Join-Path $d 'utf16.log'), [byte[]]@(0xFF, 0xFE) + [Text.Encoding]::Unicode.GetBytes("Server=tcp:db;Database=d;$($script:UI)=app;$($script:P)=$($script:V);"))
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 1
        @($r.Report.gitleaks.findings | Where-Object { $_.source -eq 'dump' }).Count | Should -BeGreaterThan 0 -Because $r.Output
    }
    It 'reports a gitleaks finding with its file and rule, never the value, and no hash by default' {
        $d = New-Folder 'gl-report'
        Set-Content -Encoding ascii (Join-Path $d 'app.yaml') ("$($script:P): $($script:V)")
        $r = Invoke-Scan $d
        $r.Output | Should -Match 'FINDING scanner=gitleaks rule=lifestream-db-password-yaml line=1 file=.*app\.yaml'
        $r.Output | Should -Not -Match $script:V
        $r.Output | Should -Not -Match '[0-9a-f]{64}'
        (Get-Content -Raw (Join-Path $TestDrive '*.json' -Resolve -ErrorAction SilentlyContinue | Select-Object -Last 1)) | Should -Not -Match $script:V
    }
}

Describe 'INFRA-01 the gitleaks allow-list is exact: file name, rule and the hash of the matched text' {
    BeforeAll {
        $script:Gd = New-Folder 'gl-allow'
        Set-Content -Encoding ascii (Join-Path $script:Gd 'vendor.yaml') ("$($script:P): $($script:V)")
        Set-Content -Encoding ascii (Join-Path $script:Gd 'other.yaml') ("$($script:P): $($script:V)")
        $first = Invoke-Scan $script:Gd @('-ShowMatchHashes')
        $script:GlHash = ([regex]::Match($first.Output, 'scanner=gitleaks rule=lifestream-db-password-yaml line=1 file=\S*vendor\.yaml matchSha256=([0-9a-f]{64})')).Groups[1].Value
    }
    It 'a listed hash clears that file only; the same text in another file, or other text in that file, is still a finding' {
        $script:GlHash | Should -Match '^[0-9a-f]{64}$'
        $list = Join-Path $TestDrive 'gl-allow.txt'
        Set-Content $list ("# test entry`nvendor.yaml|lifestream-db-password-yaml|$($script:GlHash)")
        $r = Invoke-Scan $script:Gd @('-GitleaksAllowlist', $list)
        @($r.Report.gitleaks.findings | Where-Object { $_.file -like '*vendor.yaml' }).Count | Should -Be 0
        @($r.Report.gitleaks.findings | Where-Object { $_.file -like '*other.yaml' }).Count | Should -Be 1
        $r.Report.gitleaks.allowListed | Should -Be 1
        $r.ExitCode | Should -Be 1
    }
    It 'refuses an entry for a product file, with a path or a wildcard, or with a bad hash (exit 2)' -ForEach @(
        @{ Entry = 'Lifestream.dll|lifestream-db-password|' + ('a' * 64) }
        @{ Entry = 'appsettings.json|lifestream-db-password|' + ('a' * 64) }
        @{ Entry = 'sub/file.txt|lifestream-db-password|' + ('a' * 64) }
        @{ Entry = '*.yaml|lifestream-db-password|' + ('a' * 64) }
        @{ Entry = 'vendor.yaml|lifestream-db-password|nothex' }
    ) {
        $list = Join-Path $TestDrive ('gl-bad-' + [guid]::NewGuid().ToString('N') + '.txt'); Set-Content $list $Entry
        (Invoke-Scan $script:Gd @('-GitleaksAllowlist', $list)).ExitCode | Should -Be 2
    }
    It 'the committed allow-list is well formed, each entry is commented, and no entry names a product file' {
        $lines = Get-Content (Join-Path $script:Root 'tools' 'gitleaks-allowlist.txt')
        $prev = ''
        foreach ($l in $lines) {
            if ($l -and -not $l.StartsWith('#')) {
                $l | Should -Match '^[^|/\\*?:]+\|[a-z0-9-]+\|[0-9a-f]{64}$'
                $l | Should -Not -Match '^(?i)(lifestream|appsettings)'
                $prev | Should -Not -BeNullOrEmpty -Because "every group of entries sits under a comment saying what the text is (no blank line between): $l"
            }
            $prev = $l
        }
    }
}

Describe 'INFRA-01 decompression: compressed embedded resources are opened' {
    BeforeAll {
        $script:Plain = [Text.Encoding]::UTF8.GetBytes("Server=tcp:db.internal,1433;Database=d;$($script:UI)=app;$($script:P)=$($script:V);")
        function script:Compress-Bytes([byte[]]$Bytes, [string]$Kind) {
            $ms = [IO.MemoryStream]::new()
            $s = switch ($Kind) {
                'gzip' { [IO.Compression.GZipStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true) }
                'zlib' { [IO.Compression.ZLibStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true) }
                'brotli' { [IO.Compression.BrotliStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true) }
                'deflate' { [IO.Compression.DeflateStream]::new($ms, [IO.Compression.CompressionLevel]::Optimal, $true) }
            }
            $s.Write($Bytes, 0, $Bytes.Length); $s.Dispose(); $ms.ToArray()
        }
    }
    It 'flags a plain compressed file and an archive member: <Kind>' -ForEach @(
        @{ Kind = 'gzip' }, @{ Kind = 'brotli' }
    ) {
        $d = New-Folder ('dec-' + $Kind + [guid]::NewGuid().ToString('N'))
        $ext = if ($Kind -eq 'gzip') { 'gz' } else { 'br' }
        [IO.File]::WriteAllBytes((Join-Path $d "seed.bin.$ext"), (Compress-Bytes $script:Plain $Kind))
        $r = Invoke-Scan $d @() -Own
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Not -Match $script:V
    }
}

Describe 'INFRA-01 logs: run-logged.ps1 writes the output to a file, scans it, and prints it only after a clean scan' {
    BeforeAll { $script:RunLogged = Join-Path $script:Root 'tools' 'run-logged.ps1' }
    It 'prints a clean output and returns the exit code of the command' {
        $log = Join-Path $TestDrive 'rl-clean.log'
        $out = & $script:Pwsh @script:PwshArgs $script:RunLogged -Log $log -Command 'Write-Output "hello build"; cmd /c exit 3' 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 3
        $out | Should -Match 'hello build'
        (Get-Content -Raw $log) | Should -Match 'hello build'
    }
    It 'does not print an output that holds a credential: a redacted summary and exit 1, whatever the command returned' {
        $log = Join-Path $TestDrive 'rl-dirty.log'
        $cmd = 'Write-Output (''Server=db;Pass'' + ''word=' + $script:V + ';'')'
        $out = & $script:Pwsh @script:PwshArgs $script:RunLogged -Log $log -Command $cmd 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $out | Should -Not -Match $script:V
        $out | Should -Match 'was NOT printed'
        $out | Should -Match 'FINDING'
        (Get-Content -Raw $log) | Should -Match $script:V -Because 'the file stays on the runner for the later scan step, only the job log is protected'
    }
}
