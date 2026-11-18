# INFRA-05: tests for infra/enable-entra-only.ps1 with a mocked `az` (no Azure access).

BeforeAll {
    $script:Script = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'infra' 'enable-entra-only.ps1')).Path
    $script:Sub = '88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65'

    function global:az {
        $a = @($args)
        $global:LASTEXITCODE = 0
        $global:Fake.Calls.Add(($a -join ' '))
        switch -Regex ($a[0..3] -join ' ') {
            '^sql server ad-admin list' { return (ConvertTo-Json -InputObject @($global:Fake.Admins) -Depth 4) }
            '^sql server ad-only-auth get' { return ([pscustomobject]@{ azureAdOnlyAuthentication = $global:Fake.On } | ConvertTo-Json) }
            '^sql server ad-only-auth enable' { if ($global:Fake.EnableWorks) { $global:Fake.On = $true }; return '{}' }
        }
        if ($a[0..3] -join ' ' -notmatch '^sql server ad-') { $global:LASTEXITCODE = 1; return "unexpected az call: $($a -join ' ')" }
    }
    function script:New-Fake($Admins, [bool]$On, [bool]$EnableWorks = $true) {
        $global:Fake = @{ Admins = $Admins; On = $On; EnableWorks = $EnableWorks; Calls = [System.Collections.Generic.List[string]]::new() }
    }
    function script:Run([switch]$Proven) {
        $p = @{ SubscriptionId = $script:Sub; ResourceGroup = 'Lifestream'; Server = 'lifestream-server' }
        if ($Proven) { $p.ManagedIdentityLoginProven = $true }
        & $script:Script @p 6>&1 | Out-String
    }
    $script:Admin = [pscustomobject]@{ login = 'admin-group'; sid = '00000000-0000-0000-0000-000000000001' }
}

AfterAll { Remove-Item function:global:az -ErrorAction SilentlyContinue; Remove-Variable Fake -Scope Global -ErrorAction SilentlyContinue }

Describe 'enable-entra-only.ps1 (mocked az)' {
    It 'enables Entra-only, then verifies it with ad-only-auth get' {
        New-Fake -Admins @($script:Admin) -On $false
        $out = Run -Proven
        $out | Should -Match 'OK: Entra-only authentication is on'
        $global:Fake.On | Should -BeTrue
        $calls = @($global:Fake.Calls)
        $enable = ($calls | Select-String 'ad-only-auth enable').LineNumber
        $lastGet = ($calls | Select-String 'ad-only-auth get' | Select-Object -Last 1).LineNumber
        $lastGet | Should -BeGreaterThan $enable
    }

    It 'is idempotent: when it is already on, it makes no enable call' {
        New-Fake -Admins @($script:Admin) -On $true
        (Run -Proven) | Should -Match 'already on'
        @($global:Fake.Calls | Where-Object { $_ -match 'ad-only-auth enable' }) | Should -BeNullOrEmpty
    }

    It 'fails when the post-check does not read true (enable reported success but nothing changed)' {
        New-Fake -Admins @($script:Admin) -On $false -EnableWorks $false
        { Run -Proven } | Should -Throw '*does not report true*'
    }

    It 'refuses when the server has no Entra administrator (it would lock everyone out) and changes nothing' {
        New-Fake -Admins @() -On $false
        { Run -Proven } | Should -Throw '*no Microsoft Entra administrator*'
        @($global:Fake.Calls | Where-Object { $_ -match 'ad-only-auth enable' }) | Should -BeNullOrEmpty
    }

    It 'refuses to run unless the managed identity login is declared proven' {
        New-Fake -Admins @($script:Admin) -On $false
        { Run } | Should -Throw
        $global:Fake.On | Should -BeFalse
    }

    It 'passes --subscription with the explicit id on every az call' {
        New-Fake -Admins @($script:Admin) -On $false
        Run -Proven | Out-Null
        foreach ($c in $global:Fake.Calls) { $c | Should -Match "--subscription $($script:Sub)" }
    }
}
