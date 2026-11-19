# INFRA-13 / OWN-03: decision logic of the image prune and verify steps, tested with mocked `gh` and `docker`.
# A planted `-gt 30`, a missing guard or an `|| true` is caught here by behaviour, not by matching workflow text.

BeforeAll {
    $script:Tools = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools')).Path
    $script:Prune = Join-Path $script:Tools 'prune-image-versions.ps1'
    $script:Verify = Join-Path $script:Tools 'verify-image-versions.ps1'

    function global:gh {
        $a = @($args)
        $global:LASTEXITCODE = 0
        $global:Fake.GhCalls.Add(($a -join ' '))
        if ($global:Fake.GhFails) { $global:LASTEXITCODE = 1; return 'gh: simulated failure' }
        if ($a -contains '-X' -and $a -contains 'DELETE') {
            $id = [int64](($a | Where-Object { $_ -like '/users/*' }) -replace '.*/versions/', '')
            if ($global:Fake.DeleteFails -contains $id) { $global:LASTEXITCODE = 1; return 'gh: delete failed' }
            $global:Fake.Versions = @($global:Fake.Versions | Where-Object { $_.id -ne $id })
            return ''
        }
        # list: one compact JSON object per line, like the --jq in the script produces
        foreach ($v in $global:Fake.Versions) { ($v | ConvertTo-Json -Compress -Depth 4) }
    }
    function global:docker {
        $global:Fake.DockerCalls.Add((@($args) -join ' '))
        $global:LASTEXITCODE = [int]$global:Fake.DockerExit
    }
    # Versions: id 100 is the oldest, higher id is newer. Tags: the commit SHA.
    function script:New-Fake([int]$Count, [int[]]$DeleteFails = @(), [bool]$GhFails = $false, [int]$DockerExit = 0) {
        $v = @(if ($Count -gt 0) { 1..$Count }) | ForEach-Object { [pscustomobject]@{ id = (100 + $_); created = ([datetime]'2026-11-01').AddDays($_).ToString('o'); tags = @("sha$_") } }
        $global:Fake = @{ Versions = $v; DeleteFails = $DeleteFails; GhFails = $GhFails; DockerExit = $DockerExit
                          GhCalls = [System.Collections.Generic.List[string]]::new(); DockerCalls = [System.Collections.Generic.List[string]]::new() }
    }
    function script:Prune([string]$Tag, [int]$Keep = 3) { & $script:Prune -Owner 'maiznadeem' -Package 'lifestream-api' -DeployedTag $Tag -Keep $Keep 6>&1 | Out-Null }
    function script:Verify([string]$Tag, [int]$Max = 3) { & $script:Verify -Owner 'maiznadeem' -Package 'lifestream-api' -Image 'ghcr.io/maiznadeem/lifestream-api' -DeployedTag $Tag -Max $Max 6>&1 | Out-Null }
    function script:Ids { @($global:Fake.Versions | ForEach-Object { $_.id } | Sort-Object) }
}

AfterAll {
    Remove-Item function:global:gh, function:global:docker -ErrorAction SilentlyContinue
    Remove-Variable Fake -Scope Global -ErrorAction SilentlyContinue
}

Describe 'prune-image-versions.ps1 (mocked gh)' {
    It 'keeps exactly the newest 3 and deletes the older ones' {
        New-Fake 6
        Prune 'sha6'
        Ids | Should -Be @(104, 105, 106)
    }

    It 'deletes nothing when 3 or fewer versions exist' {
        New-Fake 3
        Prune 'sha3'
        Ids | Should -Be @(101, 102, 103)
        @($global:Fake.GhCalls | Where-Object { $_ -match 'DELETE' }) | Should -BeNullOrEmpty
    }

    It 'refuses to prune when the deployed tag is not among the newest versions (it would delete the running image), deleting nothing' {
        New-Fake 6
        { Prune 'sha2' } | Should -Throw '*would delete the running image*'
        (Ids).Count | Should -Be 6
    }

    It 'refuses when no version carries the deployed tag, deleting nothing' {
        New-Fake 6
        { Prune 'sha99' } | Should -Throw '*no version*carries the deployed tag*'
        (Ids).Count | Should -Be 6
    }

    It 'refuses when the package lists no versions (a broken query is not "nothing to keep")' {
        New-Fake 0
        { Prune 'sha1' } | Should -Throw '*lists no versions*'
    }

    It 'fails when the version query fails, deleting nothing' {
        New-Fake 6 -GhFails $true
        { Prune 'sha6' } | Should -Throw '*failed*'
    }

    It 'refuses -Keep 0 (it would delete every version)' {
        New-Fake 6
        { Prune 'sha6' -Keep 0 } | Should -Throw '*at least 1*'
        (Ids).Count | Should -Be 6
    }

    It 'fails when one delete fails instead of reporting success' {
        New-Fake 6 -DeleteFails @(102)
        { Prune 'sha6' } | Should -Throw '*deleting version 102*'
    }
}

Describe 'verify-image-versions.ps1 (mocked gh and docker)' {
    It 'passes with 3 versions, the deployed tag among them and a manifest that inspects' {
        New-Fake 3
        { Verify 'sha3' } | Should -Not -Throw
        $global:Fake.DockerCalls | Should -Contain 'manifest inspect ghcr.io/maiznadeem/lifestream-api:sha3'
    }

    It 'fails with 4 versions (the limit is 3, so a planted -gt 30 cannot pass)' {
        New-Fake 4
        { Verify 'sha4' } | Should -Throw '*4 versions remain, expected at most 3*'
    }

    It 'fails when the deployed tag is not among the remaining versions' {
        New-Fake 3
        { Verify 'sha9' } | Should -Throw '*not among the remaining versions*'
    }

    It 'fails when the package lists no versions' {
        New-Fake 0
        { Verify 'sha1' } | Should -Throw '*lists no versions*'
    }

    It 'fails when docker manifest inspect fails' {
        New-Fake 3 -DockerExit 1
        { Verify 'sha3' } | Should -Throw '*docker manifest inspect*exited 1*'
    }

    It 'fails when the version query fails' {
        New-Fake 3 -GhFails $true
        { Verify 'sha3' } | Should -Throw '*failed*'
    }
}
