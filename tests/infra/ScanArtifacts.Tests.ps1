# Pester 5 tests for tools/scan-artifacts.ps1. Every credential below is built from fragments at run time so this file
# is itself clean when the repository is scanned.

BeforeAll {
    $script:Scanner = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools' 'scan-artifacts.ps1')).Path
    # pwsh can be hosted by `dotnet pwsh.dll` (global tool); then the child must be started the same way.
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }

    # Same fragments as a real leak would have, split so no literal sits in the source.
    $script:PwKey   = 'Pass' + 'word='
    $script:PwdKey  = 'P' + 'wd='
    $script:UidKey  = 'User' + ' ID='
    $script:Secret  = 'S3cr' + 'et-' + 'value-9'

    function script:Invoke-Scan([string]$Path, [string[]]$Extra = @()) {
        # A child process so `exit` codes are what CI would see.
        $out = & $script:Pwsh @script:PwshArgs $script:Scanner -Path $Path @Extra 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }

    function script:New-Folder([string]$name) {
        $d = Join-Path $TestDrive $name; New-Item -ItemType Directory -Path $d -Force | Out-Null; $d
    }

    function script:Write-Utf16([string]$file, [string]$text) {
        [IO.File]::WriteAllBytes($file, ([Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes($text)))
    }
}

Describe 'scan-artifacts.ps1' {
    It 'exits 0 on a clean folder' {
        $d = New-Folder 'clean'
        Set-Content (Join-Path $d 'appsettings.json') '{ "Logging": { "LogLevel": { "Default": "Information" } } }'
        [IO.File]::WriteAllBytes((Join-Path $d 'blob.bin'), [byte[]](0..255))
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }

    It 'exits 1 on a planted UTF-16LE credential and reports the file and encoding, not the value' {
        $d = New-Folder 'utf16'
        Write-Utf16 (Join-Path $d 'config.dat') ("Server=tcp:x.example.net;" + $script:PwKey + $script:Secret + ";")
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'config\.dat'
        $r.Output | Should -Match 'UTF-16LE'
        $r.Output | Should -Not -Match ([regex]::Escape($script:Secret))
    }

    It 'exits 1 on a UTF-16LE credential that starts at an odd byte offset' {
        $d = New-Folder 'utf16-odd'
        $bytes = [byte[]](0x41) + [Text.Encoding]::Unicode.GetBytes($script:PwKey + $script:Secret)
        [IO.File]::WriteAllBytes((Join-Path $d 'odd.bin'), $bytes)
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'exits 1 on a planted UTF-8 credential' {
        $d = New-Folder 'utf8'
        Set-Content -Encoding utf8 (Join-Path $d 'app.config') ('<add connectionString="Server=a;' + $script:PwKey + $script:Secret + '" />')
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'exits 1 on each credential keyword with a value: Password=, Pwd=, User ID=' {
        foreach ($case in @($script:PwKey, $script:PwdKey, $script:UidKey, ('user' + ' id='), ('PASS' + 'WORD ='))) {
            $d = New-Folder ('kw-' + [guid]::NewGuid().ToString('N'))
            Set-Content -Encoding ascii (Join-Path $d 'x.txt') ('a;' + $case + 'abc123;b')
            (Invoke-Scan $d).ExitCode | Should -Be 1 -Because $case
        }
    }

    It 'exits 1 on a quoted value' {
        $d = New-Folder 'quoted'
        Set-Content -Encoding ascii (Join-Path $d 'x.txt') ($script:PwKey + '"' + $script:Secret + '"')
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'exits 1 on a database.windows.net connection string carrying credentials (UTF-8 and UTF-16LE)' {
        $cs = 'Server=tcp:lifestream-server.database.windows.net,1433;Initial Catalog=BloodBank;' + $script:UidKey + 'admin;' + $script:PwKey + $script:Secret
        $d = New-Folder 'sqlcs'
        Set-Content -Encoding ascii (Join-Path $d 'a.txt') $cs
        Write-Utf16 (Join-Path $d 'b.bin') $cs
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'a\.txt'
        $r.Output | Should -Match 'b\.bin'
    }

    It 'exits 1 on credentials embedded in a URL to database.windows.net' {
        $d = New-Folder 'urlcred'
        Set-Content -Encoding ascii (Join-Path $d 'u.txt') ('sqlserver://admin:' + $script:Secret + '@lifestream-server.database.windows.net:1433')
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'exits 1 on a JWT' {
        $d = New-Folder 'jwt'
        $jwt = ('eyJ' + 'hbGciOiJIUzI1NiJ9') + '.' + ('eyJ' + 'zdWIiOiJ0ZXN0LXVzZXIifQ') + '.' + 'abcdefghij0123456789ABCDEF'
        Set-Content -Encoding ascii (Join-Path $d 'token.txt') ('Authorization: Bearer ' + $jwt)
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'exits 1 on a private key header' {
        $d = New-Folder 'pem'
        Set-Content -Encoding ascii (Join-Path $d 'k.pem') ('-----BEGIN ' + 'RSA PRIVATE KEY-----' + "`nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC`n-----END RSA PRIVATE KEY-----")
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'finds a credential inside a zip or nupkg (UTF-16LE entry) so installers and update packages are covered' {
        $d = New-Folder 'zipped'
        $stage = New-Folder 'zip-stage'
        Write-Utf16 (Join-Path $stage 'inner.dll') ($script:PwKey + $script:Secret)
        Compress-Archive -Path (Join-Path $stage 'inner.dll') -DestinationPath (Join-Path $d 'Lifestream-2.0.0-full.nupkg.zip')
        Rename-Item (Join-Path $d 'Lifestream-2.0.0-full.nupkg.zip') 'Lifestream-2.0.0-full.nupkg'
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 1 -Because $r.Output
        $r.Output | Should -Match 'inner\.dll'
    }

    It 'finds a credential past the first buffer of a large file' {
        $d = New-Folder 'large'
        $f = Join-Path $d 'big.bin'
        $fs = [IO.File]::Create($f)
        try {
            $fs.SetLength(9MB)
            $fs.Position = 9MB - 200
            $b = [Text.Encoding]::Unicode.GetBytes($script:PwKey + $script:Secret)
            $fs.Write($b, 0, $b.Length)
        } finally { $fs.Dispose() }
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'finds a credential that straddles the internal chunk boundary' {
        $d = New-Folder 'straddle'
        $f = Join-Path $d 'edge.bin'
        $chunk = 4MB
        $fs = [IO.File]::Create($f)
        try {
            $fs.SetLength($chunk + 1MB)
            $b = [Text.Encoding]::ASCII.GetBytes($script:PwKey + $script:Secret)
            $fs.Position = $chunk - 6
            $fs.Write($b, 0, $b.Length)
        } finally { $fs.Dispose() }
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'does not flag keywords without a value, placeholders or format strings' {
        $d = New-Folder 'benign'
        $lines = @(
            ('connection string keys: ' + $script:PwKey + ';' + $script:UidKey + ';'),
            ('"' + $script:PwKey + '"'),
            ($script:PwKey + '{0}'),
            ($script:PwKey + '${DB_PASSWORD}'),
            ($script:PwKey + '$password;TrustServerCertificate=true'),
            ($script:PwKey + '%PASSWORD%'),
            'Authentication=Active Directory Managed Identity;Server=tcp:x.database.windows.net'
        )
        Set-Content -Encoding ascii (Join-Path $d 'doc.txt') $lines
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }

    It 'does not flag the compiled string table of a .NET record that has a Password property (ToString text followed by the next heap entry)' {
        # C# record.ToString() stores "Password = " as a UTF-16 literal; the next user-string heap entry follows it, whose
        # first UTF-16 unit is (terminator byte, length byte), a code point of U+0100 or higher. Seen in Lifestream.Contracts.dll.
        $d = New-Folder 'record-tostring'
        $bytes = [Text.Encoding]::Unicode.GetBytes($script:PwKey.Replace('=', ' = ')) + [byte[]](0x00, 0x1D) + [Text.Encoding]::Unicode.GetBytes('RefreshRequest')
        [IO.File]::WriteAllBytes((Join-Path $d 'Contracts.dll'), $bytes)
        $r = Invoke-Scan $d
        $r.ExitCode | Should -Be 0 -Because $r.Output
    }

    It 'does not flag identifier-style user id text such as library error messages, but flags the exact UserID keyword' {
        $d = New-Folder 'userid-style'
        Write-Utf16 (Join-Path $d 'msal.dll') 'pass userId=UserIdentifier.AnyUser instead of userId=null.'
        (Invoke-Scan $d).ExitCode | Should -Be 0
        $d2 = New-Folder 'userid-keyword'
        Set-Content -Encoding ascii (Join-Path $d2 'x.txt') ('Data Source=a;UserID=' + $script:Secret)
        (Invoke-Scan $d2).ExitCode | Should -Be 1
    }

    It 'still flags a UTF-16LE credential whose value has Latin-1 letters' {
        $d = New-Folder 'latin1-value'
        Write-Utf16 (Join-Path $d 'x.bin') ($script:PwKey + 'caf' + [char]0xE9 + '-' + $script:Secret)
        (Invoke-Scan $d).ExitCode | Should -Be 1
    }

    It 'scans a single file path as well as a folder, and several paths at once' {
        $clean = New-Folder 'multi-clean'; Set-Content (Join-Path $clean 'a.txt') 'nothing here'
        $dirty = New-Folder 'multi-dirty'; Set-Content -Encoding ascii (Join-Path $dirty 'b.txt') ($script:PwKey + $script:Secret)
        (Invoke-Scan (Join-Path $dirty 'b.txt')).ExitCode | Should -Be 1
        $out = & $script:Pwsh @script:PwshArgs $script:Scanner -Path $clean, $dirty 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1 -Because $out
    }

    It 'exits 2, not 0, when the path does not exist (a typo must not look like a clean scan)' {
        $r = Invoke-Scan (Join-Path $TestDrive 'no-such-folder')
        $r.ExitCode | Should -Be 2
    }

    It 'exits 2 when the folder holds no files at all (an empty scan is not proof of anything)' {
        $d = New-Folder 'empty'
        (Invoke-Scan $d).ExitCode | Should -Be 2
    }

    It 'writes a JSON report with counts when asked' {
        $d = New-Folder 'report'
        Set-Content -Encoding ascii (Join-Path $d 'x.txt') ($script:PwKey + $script:Secret)
        $report = Join-Path $TestDrive 'report.json'
        $r = Invoke-Scan $d @('-ReportPath', $report)
        $r.ExitCode | Should -Be 1
        $j = Get-Content $report -Raw | ConvertFrom-Json
        $j.filesScanned | Should -Be 1
        @($j.findings).Count | Should -Be 1
        (Get-Content $report -Raw) | Should -Not -Match ([regex]::Escape($script:Secret))
    }
}
