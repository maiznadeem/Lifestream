# INFRA-04: tests for infra/sync-sql-firewall.ps1 with a mocked `az` (a stateful fake firewall, no Azure access).

BeforeAll {
    $script:Script = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'infra' 'sync-sql-firewall.ps1')).Path
    $script:Sub = '88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65'

    # The fake: every az call is recorded; firewall rules live in $global:Fake.Rules and change like the real server's would.
    function global:az {
        $a = @($args)
        $global:LASTEXITCODE = 0
        $global:Fake.Calls.Add(($a -join ' '))
        $pos = $a | Where-Object { $_ -notlike '--*' } | Select-Object -First 4
        $named = @{}
        for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -like '--*' -and $i + 1 -lt $a.Count) { $named[$a[$i]] = $a[$i + 1] } }
        if ($global:Fake.FailOn -and ($a -join ' ') -match $global:Fake.FailOn) { $global:LASTEXITCODE = 1; return 'ERROR: simulated failure' }
        switch -Regex ($a[0..2] -join ' ') {
            '^sql server ad-only-auth' { return ([pscustomobject]@{ azureAdOnlyAuthentication = $global:Fake.EntraOnly } | ConvertTo-Json) }
            '^containerapp env show' { return ([pscustomobject]@{ properties = [pscustomobject]@{ outboundIpAddresses = $global:Fake.EnvIps } } | ConvertTo-Json -Depth 5) }
            '^containerapp list' { return (ConvertTo-Json -InputObject @($global:Fake.Apps) -Depth 6) }
            '^lock list' { return (ConvertTo-Json -InputObject @($global:Fake.Locks | ForEach-Object { [pscustomobject]@{ id = $_.id; name = $_.name; level = $_.level; notes = $_.notes } }) -Depth 4) }
            '^lock delete' {
                if (-not $global:Fake.LockAdmin) { $global:LASTEXITCODE = 1; return 'ERROR: (AuthorizationFailed) does not have authorization to perform action Microsoft.Authorization/locks/delete' }
                $global:Fake.Locks = @($global:Fake.Locks | Where-Object { $_.id -ne $named['--ids'] }); return ''
            }
            '^lock create' {
                if (-not $global:Fake.LockAdmin -or $global:Fake.FailRelock) { $global:LASTEXITCODE = 1; return 'ERROR: simulated lock create failure' }
                $global:Fake.Locks = @($global:Fake.Locks) + @([pscustomobject]@{ id = "/subscriptions/x/resourceGroups/Lifestream/providers/Microsoft.Sql/servers/lifestream-server/providers/Microsoft.Authorization/locks/$($named['--name'])"; name = $named['--name']; level = $named['--lock-type']; notes = $named['--notes'] })
                return ''
            }
            '^sql server firewall-rule' {
                switch ($a[3]) {
                    'list' { return (ConvertTo-Json -InputObject @($global:Fake.Rules | ForEach-Object { [pscustomobject]@{ name = $_.name; startIpAddress = $_.start; endIpAddress = $_.end } }) -Depth 4) }
                    { $_ -in 'create', 'update' } {
                        $global:Fake.Rules = @($global:Fake.Rules | Where-Object { $_.name -ne $named['--name'] }) + @([pscustomobject]@{ name = $named['--name']; start = $named['--start-ip-address']; end = $named['--end-ip-address'] })
                        return ''
                    }
                    'delete' {
                        # A CanNotDelete lock on the server blocks deleting its child resources, like the real service.
                        if (@($global:Fake.Locks | Where-Object { $_.level -eq 'CanNotDelete' -and $_.id -notmatch '/databases/' }).Count -gt 0) { $global:LASTEXITCODE = 1; return 'ERROR: (ScopeLocked) The scope is locked.' }
                        $global:Fake.Rules = @($global:Fake.Rules | Where-Object { $_.name -ne $named['--name'] }); return '' }
                }
            }
        }
        $global:LASTEXITCODE = 1
        "unexpected az call: $($a -join ' ')"
    }

    function script:New-Fake([string[]]$EnvIps, $Rules, $Apps = @(), [string]$FailOn = '', [bool]$EntraOnly = $true, $Locks = @(), [bool]$LockAdmin = $true, [bool]$FailRelock = $false) {
        $global:Fake = @{ Locks = @($Locks); LockAdmin = $LockAdmin; FailRelock = $FailRelock; EntraOnly = $EntraOnly; EnvIps = $EnvIps; Rules = @($Rules); Apps = $Apps; Calls = [System.Collections.Generic.List[string]]::new(); FailOn = $FailOn }
    }
    function script:ServerLock([string]$name = 'lifestream-server-no-delete', [string]$level = 'CanNotDelete') {
        [pscustomobject]@{ id = "/subscriptions/x/resourceGroups/Lifestream/providers/Microsoft.Sql/servers/lifestream-server/providers/Microsoft.Authorization/locks/$name"; name = $name; level = $level; notes = 'Lifestream SQL server. Remove this lock deliberately before deleting.' }
    }
    function script:Rule([string]$name, [string]$start, [string]$end) { [pscustomobject]@{ name = $name; start = $start; end = $end } }
    function script:Sync {
        & $script:Script -SubscriptionId $script:Sub -ResourceGroup 'Lifestream' -Server 'lifestream-server' -Environment 'lifestream-env' 6>&1 | Out-Null
    }
    function script:RulesNow { @($global:Fake.Rules | ForEach-Object { "$($_.name)=$($_.start)-$($_.end)" } | Sort-Object) }
}

AfterAll { Remove-Item function:global:az -ErrorAction SilentlyContinue; Remove-Variable Fake -Scope Global -ErrorAction SilentlyContinue }

Describe 'sync-sql-firewall.ps1 (mocked az)' {
    It 'removes the Allow anyone rule, keeps AllowAllWindowsAzureIps, and adds the environment addresses' {
        New-Fake -EnvIps @('20.1.1.2', '20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'), (Rule 'AllowAllWindowsAzureIps' '0.0.0.0' '0.0.0.0'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'aca-outbound-2=20.1.1.2-20.1.1.2', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
        @($global:Fake.Calls | Where-Object { $_ -match 'firewall-rule (update|delete) .*AllowAllWindowsAzureIps' }) | Should -BeNullOrEmpty
    }

    It 'adds AllowAllWindowsAzureIps back when it is missing (the shared outbound pool is larger than the rules and it changes)' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
    }

    It 'puts AllowAllWindowsAzureIps back to 0.0.0.0-0.0.0.0 if its range was changed' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'), (Rule 'AllowAllWindowsAzureIps' '0.0.0.0' '255.255.255.255'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
    }

    It 'adds only the missing addresses and keeps the correct rules untouched' {
        New-Fake -EnvIps @('20.1.1.1', '20.1.1.2', '20.1.1.3') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'), (Rule 'aca-outbound-2' '20.1.1.2' '20.1.1.2'), (Rule 'AllowAllWindowsAzureIps' '0.0.0.0' '0.0.0.0'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'aca-outbound-2=20.1.1.2-20.1.1.2', 'aca-outbound-3=20.1.1.3-20.1.1.3', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
        @($global:Fake.Calls | Where-Object { $_ -match 'firewall-rule (create|update|delete)' }).Count | Should -Be 1
    }

    It 'removes stale addresses (an old aca-outbound rule and a hand-added client rule)' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'), (Rule 'aca-outbound-2' '20.9.9.9' '20.9.9.9'), (Rule 'my-laptop' '81.2.3.4' '81.2.3.4'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
    }

    It 'rewrites a rule that has the right name but the wrong address' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'aca-outbound-1' '20.5.5.5' '20.5.5.5'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
    }

    It 'is idempotent: a second run makes no create, update or delete call' {
        New-Fake -EnvIps @('20.1.1.1', '20.1.1.2') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'))
        Sync
        $before = RulesNow
        $global:Fake.Calls.Clear()
        Sync
        RulesNow | Should -Be $before
        @($global:Fake.Calls | Where-Object { $_ -match 'firewall-rule (create|update|delete)' }) | Should -BeNullOrEmpty
    }

    It 'fails loudly and deletes nothing when the environment returns no addresses' {
        New-Fake -EnvIps @() -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'), (Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'))
        { Sync } | Should -Throw '*no outbound IP addresses*'
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'Allow anyone=0.0.0.0-255.255.255.255')
        @($global:Fake.Calls | Where-Object { $_ -match 'firewall-rule' }) | Should -BeNullOrEmpty
    }

    It 'fails loudly and deletes nothing when the environment query itself fails' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -FailOn '^containerapp env show'
        { Sync } | Should -Throw '*failed*'
        RulesNow | Should -Be @('Allow anyone=0.0.0.0-255.255.255.255')
    }

    It 'refuses to write an open range if the environment ever reports 0.0.0.0 or something that is not an IPv4 address' {
        New-Fake -EnvIps @('0.0.0.0') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'))
        { Sync } | Should -Throw '*open the server*'
        New-Fake -EnvIps @('not-an-ip') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'))
        { Sync } | Should -Throw '*not an IPv4*'
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1')
    }

    It 'also takes the outbound addresses of the apps that run in the environment, and ignores apps of other environments' {
        $mine = [pscustomobject]@{ properties = [pscustomobject]@{ environmentId = '/subscriptions/x/resourceGroups/Lifestream/providers/Microsoft.App/managedEnvironments/lifestream-env'; outboundIpAddresses = @('20.2.2.2') } }
        $other = [pscustomobject]@{ properties = [pscustomobject]@{ environmentId = '/subscriptions/x/resourceGroups/Lifestream/providers/Microsoft.App/managedEnvironments/other-env'; outboundIpAddresses = @('20.3.3.3') } }
        New-Fake -EnvIps @() -Rules @() -Apps @($mine, $other)
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.2.2.2-20.2.2.2', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
    }

    It 'passes --subscription with the explicit id on every az call' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'))
        Sync
        $global:Fake.Calls.Count | Should -BeGreaterThan 5
        foreach ($c in $global:Fake.Calls) { $c | Should -Match "--subscription $($script:Sub)" }
    }

    It 'a delete failure is reported (the lock hint is added for ScopeLocked)' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -FailOn 'firewall-rule delete'
        $global:Fake.Calls.Clear()
        { Sync } | Should -Throw '*simulated failure*'
    }

    Context 'INFRA-04 the delete lock of INFRA-08 (the fake service refuses firewall-rule delete while a CanNotDelete lock exists on the server)' {
        It 'removes the lock, deletes the stale rules, and puts the identical lock back' {
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'), (Rule 'AllowAllWindowsAzureIps' '0.0.0.0' '0.0.0.0')) -Locks @(ServerLock)
            Sync
            RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'AllowAllWindowsAzureIps=0.0.0.0-0.0.0.0')
            @($global:Fake.Locks).Count | Should -Be 1
            $global:Fake.Locks[0].name | Should -Be 'lifestream-server-no-delete'
            $global:Fake.Locks[0].level | Should -Be 'CanNotDelete'
            $global:Fake.Locks[0].notes | Should -Be 'Lifestream SQL server. Remove this lock deliberately before deleting.'
        }

        It 'orders the calls: add rule, lock delete, rule deletes, lock create, final list' {
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -Locks @(ServerLock)
            Sync
            $calls = @($global:Fake.Calls)
            $idx = { param($re) ($calls | Select-String -Pattern $re | Select-Object -First 1).LineNumber }
            $create = & $idx 'firewall-rule create'; $unlock = & $idx '^lock delete'; $del = & $idx 'firewall-rule delete'; $relock = & $idx '^lock create'
            $create | Should -BeLessThan $unlock
            $unlock | Should -BeLessThan $del
            $del | Should -BeLessThan $relock
        }

        It 'does not touch the lock when there is nothing to delete' {
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1')) -Locks @(ServerLock)
            Sync
            @($global:Fake.Calls | Where-Object { $_ -match '^lock (delete|create)' }) | Should -BeNullOrEmpty
            @($global:Fake.Locks).Count | Should -Be 1
        }

        It 'puts the lock back even when a rule delete fails, and fails loudly' {
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -Locks @(ServerLock) -FailOn 'firewall-rule delete'
            { Sync } | Should -Throw '*simulated failure*'
            @($global:Fake.Locks).Count | Should -Be 1 -Because 'the finally block restores the lock'
        }

        It 'fails loudly, deleting nothing and leaving the lock, when the identity cannot manage locks (Contributor)' {
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -Locks @(ServerLock) -LockAdmin $false
            { Sync } | Should -Throw '*Contributor lacks it*'
            RulesNow | Should -Contain 'Allow anyone=0.0.0.0-255.255.255.255'
            RulesNow | Should -Contain 'aca-outbound-1=20.1.1.1-20.1.1.1' -Because 'the address the API needs was added first'
            @($global:Fake.Locks).Count | Should -Be 1
        }

        It 'fails loudly when the lock cannot be put back' {
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -Locks @(ServerLock) -FailRelock $true
            { Sync } | Should -Throw '*UNLOCKED*'
        }

        It 'refuses a ReadOnly lock (it would also block adds) and a lock on the resource group, naming them' {
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -Locks @(ServerLock 'ro' 'ReadOnly')
            { Sync } | Should -Throw '*ReadOnly*'
            $rg = [pscustomobject]@{ id = '/subscriptions/x/resourceGroups/Lifestream/providers/Microsoft.Authorization/locks/rg-lock'; name = 'rg-lock'; level = 'CanNotDelete'; notes = '' }
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -Locks @($rg)
            { Sync } | Should -Throw '*resource group*'
        }

        It 'ignores locks on other resources (the database lock does not block server firewall rules)' {
            $db = [pscustomobject]@{ id = '/subscriptions/x/resourceGroups/Lifestream/providers/Microsoft.Sql/servers/lifestream-server/databases/BloodBank/providers/Microsoft.Authorization/locks/lifestream-database-no-delete'; name = 'lifestream-database-no-delete'; level = 'CanNotDelete'; notes = '' }
            New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -Locks @($db)
            { Sync } | Should -Not -Throw
            @($global:Fake.Calls | Where-Object { $_ -match '^lock (delete|create)' }) | Should -BeNullOrEmpty
        }
    }

    It 'refuses to add any outbound-address rule while Entra-only authentication is off, and deletes nothing' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -EntraOnly $false
        { Sync } | Should -Throw '*Entra-only authentication is not on*'
        RulesNow | Should -Be @('Allow anyone=0.0.0.0-255.255.255.255')
        @($global:Fake.Calls | Where-Object { $_ -match 'firewall-rule (create|update|delete)' }) | Should -BeNullOrEmpty
        @($global:Fake.Calls | Where-Object { $_ -match 'containerapp' }) | Should -BeNullOrEmpty -Because 'the gate runs before anything else is read'
    }

    It 'checks Entra-only with az sql server ad-only-auth get before it touches the firewall' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @()
        Sync
        $calls = @($global:Fake.Calls)
        $gate = ($calls | Select-String 'sql server ad-only-auth get' | Select-Object -First 1).LineNumber
        $firstWrite = ($calls | Select-String 'firewall-rule create' | Select-Object -First 1).LineNumber
        $gate | Should -BeLessThan $firstWrite
    }

    It 'changes nothing with -WhatIfOnly' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'))
        & $script:Script -SubscriptionId $script:Sub -ResourceGroup 'Lifestream' -Server 'lifestream-server' -Environment 'lifestream-env' -WhatIfOnly 6>&1 | Out-Null
        RulesNow | Should -Be @('Allow anyone=0.0.0.0-255.255.255.255')
    }
}
