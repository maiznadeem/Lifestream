# Tests for infra/bootstrap-sql.ps1 -PrintOnly (the generated T-SQL). The script never connects in these tests.

BeforeAll {
    $script:Script = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'infra' 'bootstrap-sql.ps1')).Path
    $script:Api = '11111111-1111-1111-1111-111111111111'
    $script:Mig = '22222222-2222-2222-2222-222222222222'
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }

    function script:Get-Sql([string[]]$Extra = @()) {
        $out = & $script:Pwsh @script:PwshArgs $script:Script -ApiPrincipalId $script:Api -MigratorPrincipalId $script:Mig -PrintOnly @Extra 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Sql = $out }
    }
}

Describe 'INFRA-06 bootstrap-sql.ps1' {
    BeforeAll { $script:r = Get-Sql }

    It 'prints the SQL and exits 0 without connecting' {
        $script:r.ExitCode | Should -Be 0 -Because $script:r.Sql
        $script:r.Sql | Should -Match 'CREATE USER'
    }

    It 'creates both users from the Entra identities by object id, guarded so a second run changes nothing' {
        $script:r.Sql | Should -Match "IF NOT EXISTS \(SELECT 1 FROM sys\.database_principals WHERE name = N'lifestream-api'\)\s+CREATE USER \[lifestream-api\] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '$($script:Api)'"
        $script:r.Sql | Should -Match "IF NOT EXISTS \(SELECT 1 FROM sys\.database_principals WHERE name = N'lifestream-migrator'\)\s+CREATE USER \[lifestream-migrator\] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '$($script:Mig)'"
    }

    It 'gives the API user db_datareader, db_datawriter and EXECUTE and nothing else' {
        $apiLines = ($script:r.Sql -split "`n" | Where-Object { $_ -match '\[lifestream-api\];' -and $_ -notmatch 'CREATE USER' })
        ($apiLines -join "`n") | Should -Match 'ALTER ROLE \[db_datareader\] ADD MEMBER \[lifestream-api\]'
        ($apiLines -join "`n") | Should -Match 'ALTER ROLE \[db_datawriter\] ADD MEMBER \[lifestream-api\]'
        ($apiLines -join "`n") | Should -Match 'GRANT EXECUTE TO \[lifestream-api\]'
        $apiLines.Count | Should -Be 3
        ($apiLines -join "`n") | Should -Not -Match 'db_ddladmin|db_owner|db_securityadmin|CONTROL|ALTER ANY'
    }

    It 'gives the migrator db_ddladmin plus read and write, and no password anywhere' {
        $migLines = ($script:r.Sql -split "`n" | Where-Object { $_ -match '\[lifestream-migrator\];' -and $_ -notmatch 'CREATE USER' }) -join "`n"
        $migLines | Should -Match 'db_ddladmin'
        $migLines | Should -Match 'db_datareader'
        $migLines | Should -Match 'db_datawriter'
        $migLines | Should -Not -Match 'db_owner'
        $script:r.Sql | Should -Not -Match '(?i)password|pwd|WITH PASSWORD|IDENTIFIED BY'
    }

    It 'ends with a verification query that fails on any extra role or permission of the API user' {
        $script:r.Sql | Should -Match 'Verification \(must return no rows\)'
        $script:r.Sql | Should -Match "r\.name NOT IN \(N'db_datareader', N'db_datawriter'\)"
        $script:r.Sql | Should -Match "p\.permission_name NOT IN \(N'EXECUTE', N'CONNECT'\)"
        $script:r.Sql | Should -Match "IS_ROLEMEMBER\(N'db_ddladmin', N'lifestream-migrator'\)"
    }

    It 'rejects an identity name that could inject SQL' {
        $r = Get-Sql @('-ApiName', "x]; DROP USER [y")
        $r.ExitCode | Should -Not -Be 0
        $r.Sql | Should -Not -Match 'CREATE USER'
    }

    It 'rejects a principal id that is not a GUID' {
        $out = & $script:Pwsh @script:PwshArgs $script:Script -ApiPrincipalId "1'; DROP TABLE x;--" -MigratorPrincipalId $script:Mig -PrintOnly 2>&1 | Out-String
        $LASTEXITCODE | Should -Not -Be 0
    }
}
