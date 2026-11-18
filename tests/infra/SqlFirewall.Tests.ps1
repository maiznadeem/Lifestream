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
            '^sql server firewall-rule' {
                switch ($a[3]) {
                    'list' { return (ConvertTo-Json -InputObject @($global:Fake.Rules | ForEach-Object { [pscustomobject]@{ name = $_.name; startIpAddress = $_.start; endIpAddress = $_.end } }) -Depth 4) }
                    { $_ -in 'create', 'update' } {
                        $global:Fake.Rules = @($global:Fake.Rules | Where-Object { $_.name -ne $named['--name'] }) + @([pscustomobject]@{ name = $named['--name']; start = $named['--start-ip-address']; end = $named['--end-ip-address'] })
                        return ''
                    }
                    'delete' { $global:Fake.Rules = @($global:Fake.Rules | Where-Object { $_.name -ne $named['--name'] }); return '' }
                }
            }
        }
        $global:LASTEXITCODE = 1
        "unexpected az call: $($a -join ' ')"
    }

    function script:New-Fake([string[]]$EnvIps, $Rules, $Apps = @(), [string]$FailOn = '', [bool]$EntraOnly = $true) {
        $global:Fake = @{ EntraOnly = $EntraOnly; EnvIps = $EnvIps; Rules = @($Rules); Apps = $Apps; Calls = [System.Collections.Generic.List[string]]::new(); FailOn = $FailOn }
    }
    function script:Rule([string]$name, [string]$start, [string]$end) { [pscustomobject]@{ name = $name; start = $start; end = $end } }
    function script:Sync {
        & $script:Script -SubscriptionId $script:Sub -ResourceGroup 'Lifestream' -Server 'lifestream-server' -Environment 'lifestream-env' 6>&1 | Out-Null
    }
    function script:RulesNow { @($global:Fake.Rules | ForEach-Object { "$($_.name)=$($_.start)-$($_.end)" } | Sort-Object) }
}

AfterAll { Remove-Item function:global:az -ErrorAction SilentlyContinue; Remove-Variable Fake -Scope Global -ErrorAction SilentlyContinue }

Describe 'sync-sql-firewall.ps1 (mocked az)' {
    It 'removes the Allow anyone and AllowAllWindowsAzureIps rules and adds the environment addresses' {
        New-Fake -EnvIps @('20.1.1.2', '20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'), (Rule 'AllowAllWindowsAzureIps' '0.0.0.0' '0.0.0.0'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'aca-outbound-2=20.1.1.2-20.1.1.2')
    }

    It 'adds only the missing addresses and keeps the correct rules untouched' {
        New-Fake -EnvIps @('20.1.1.1', '20.1.1.2', '20.1.1.3') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'), (Rule 'aca-outbound-2' '20.1.1.2' '20.1.1.2'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1', 'aca-outbound-2=20.1.1.2-20.1.1.2', 'aca-outbound-3=20.1.1.3-20.1.1.3')
        @($global:Fake.Calls | Where-Object { $_ -match 'firewall-rule (create|update|delete)' }).Count | Should -Be 1
    }

    It 'removes stale addresses (an old aca-outbound rule and a hand-added client rule)' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'aca-outbound-1' '20.1.1.1' '20.1.1.1'), (Rule 'aca-outbound-2' '20.9.9.9' '20.9.9.9'), (Rule 'my-laptop' '81.2.3.4' '81.2.3.4'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1')
    }

    It 'rewrites a rule that has the right name but the wrong address' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'aca-outbound-1' '20.5.5.5' '20.5.5.5'))
        Sync
        RulesNow | Should -Be @('aca-outbound-1=20.1.1.1-20.1.1.1')
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
        RulesNow | Should -Be @('aca-outbound-1=20.2.2.2-20.2.2.2')
    }

    It 'passes --subscription with the explicit id on every az call' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'))
        Sync
        $global:Fake.Calls.Count | Should -BeGreaterThan 5
        foreach ($c in $global:Fake.Calls) { $c | Should -Match "--subscription $($script:Sub)" }
    }

    It 'says what to do when a CanNotDelete lock blocks the delete' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255')) -FailOn 'firewall-rule delete'
        $global:Fake.Calls.Clear()
        { Sync } | Should -Throw '*simulated failure*'
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
        $gate = ($calls | Select-String 'sql server ad-only-auth get').LineNumber
        $firstWrite = ($calls | Select-String 'firewall-rule create').LineNumber
        $gate | Should -BeLessThan $firstWrite
    }

    It 'changes nothing with -WhatIfOnly' {
        New-Fake -EnvIps @('20.1.1.1') -Rules @((Rule 'Allow anyone' '0.0.0.0' '255.255.255.255'))
        & $script:Script -SubscriptionId $script:Sub -ResourceGroup 'Lifestream' -Server 'lifestream-server' -Environment 'lifestream-env' -WhatIfOnly 6>&1 | Out-Null
        RulesNow | Should -Be @('Allow anyone=0.0.0.0-255.255.255.255')
    }
}
