# Static assertions over the delivery workflows (the YAML structure, ordering and permissions). They cannot run the workflows;
# actionlint (tests/infra/run.ps1) checks syntax and expressions, and the first real run on GitHub is the live proof.

BeforeAll {
    $yamlModule = if ($env:LIFESTREAM_YAML_MODULE) { Join-Path $env:LIFESTREAM_YAML_MODULE 'powershell-yaml' } else { 'powershell-yaml' }
    Import-Module $yamlModule -Force
    $script:Dir = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows')).Path
    function script:Load([string]$name) {
        $path = Join-Path $script:Dir $name
        [pscustomobject]@{ Text = (Get-Content $path -Raw); Yaml = (ConvertFrom-Yaml (Get-Content $path -Raw)) }
    }
    function script:StepIndex($job, [scriptblock]$match) {
        $steps = @($job.steps)
        for ($i = 0; $i -lt $steps.Count; $i++) { if (& $match $steps[$i]) { return $i } }
        -1
    }
    function script:Run($step) { if ($step.ContainsKey('run')) { [string]$step.run } else { '' } }
    function script:Uses($step) { if ($step.ContainsKey('uses')) { [string]$step.uses } else { '' } }
    $script:Deploy = Load 'deploy-api.yml'
    $script:Release = Load 'release.yml'
    $script:Scan = Load 'secret-scan.yml'
    $script:Ci = Load 'ci.yml'
    $script:SyncFw = Load 'sync-sql-firewall.yml'
}

Describe 'INFRA-13 deploy-api.yml: keyless, after build and tests, separate from the PR build' {
    It 'is a separate workflow from ci.yml and does not run on pull requests' {
        $script:Deploy.Yaml.name | Should -Not -Be $script:Ci.Yaml.name
        $script:Deploy.Yaml.on.Keys | Should -Not -Contain 'pull_request'
        $script:Deploy.Yaml.on.push.branches | Should -Contain 'master'
        $script:Deploy.Yaml.on.Keys | Should -Contain 'workflow_dispatch'
    }

    It 'triggers on the API, data, contracts, domain and infra paths' {
        $paths = $script:Deploy.Yaml.on.push.paths
        foreach ($p in 'src/Lifestream.Api/**', 'src/Lifestream.Data/**', 'src/Lifestream.Contracts/**', 'src/Lifestream.Domain/**', 'infra/**') { $paths | Should -Contain $p }
    }

    It 'deploys only after the image job, which only runs after the gate job (build and tests)' {
        $script:Deploy.Yaml.jobs.image.needs | Should -Contain 'gate'
        $script:Deploy.Yaml.jobs.deploy.needs | Should -Contain 'image'
        $gate = (@($script:Deploy.Yaml.jobs.gate.steps) | ForEach-Object { Run $_ }) -join "`n"
        $gate | Should -Match 'dotnet build'
        $gate | Should -Match 'dotnet test tests/Lifestream.Api.Tests'
    }

    It 'asks for an OIDC token in the deploy job and signs in with azure/login using ids, not a secret' {
        $script:Deploy.Yaml.jobs.deploy.permissions.'id-token' | Should -Be 'write'
        $login = @($script:Deploy.Yaml.jobs.deploy.steps | Where-Object { (Uses $_) -like 'azure/login@*' })
        $login.Count | Should -Be 1
        $login[0].with.Keys | Should -Not -Contain 'creds'
        $login[0].with.Keys | Should -Not -Contain 'client-secret'
        $login[0].with.'client-id' | Should -Match 'vars\.AZURE_CLIENT_ID'
    }

    It 'uses no secret other than the built-in GITHUB_TOKEN anywhere in the file' {
        $found = [regex]::Matches($script:Deploy.Text, 'secrets\.(\w+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        @($found | Where-Object { $_ -ne 'GITHUB_TOKEN' }) | Should -BeNullOrEmpty
        $script:Deploy.Text | Should -Not -Match '(?i)client-secret|AZURE_CREDENTIALS'
    }
}

Describe 'OWN-03 / INFRA-12 deploy-api.yml: only the newest 3 image versions remain' {
    BeforeAll { $script:Steps = @($script:Deploy.Yaml.jobs.deploy.steps) }

    It 'builds a single-architecture image tagged with the commit SHA, without provenance or SBOM attestations' {
        $build = (@($script:Deploy.Yaml.jobs.image.steps) | ForEach-Object { Run $_ }) -join "`n"
        $build | Should -Match '--provenance=false'
        $build | Should -Match '--sbom=false'
        $build | Should -Match '--platform linux/amd64'
        $build | Should -Match '\$IMAGE:\$GITHUB_SHA'
        $script:Deploy.Yaml.env.IMAGE | Should -Be 'ghcr.io/maiznadeem/lifestream-api'
    }

    It 'prunes with tools/prune-image-versions.ps1 (guards and tests live in the script), keeping 3 versions of lifestream-api' {
        $i = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'tools/prune-image-versions\.ps1' }
        $i | Should -BeGreaterThan -1
        $run = Run $script:Steps[$i]
        $run | Should -Match '-Package lifestream-api'
        $run | Should -Match '-Keep 3'
        $run | Should -Match '-DeployedTag \$env:GITHUB_SHA'
        $script:Deploy.Text | Should -Not -Match 'delete-package-versions'
    }

    It 'prunes only after the health check and the app update, so the deployed image is the newest and is never deleted' {
        $update = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'az containerapp update' }
        $health = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match '/healthz' }
        $prune = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'tools/prune-image-versions\.ps1' }
        $update | Should -BeGreaterThan -1
        $health | Should -BeGreaterThan $update
        $prune | Should -BeGreaterThan $health
    }

    It 'afterwards runs tools/verify-image-versions.ps1 (count, deployed tag and docker manifest inspect are decided in the script)' {
        $prune = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'tools/prune-image-versions\.ps1' }
        $verify = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'tools/verify-image-versions\.ps1' }
        $verify | Should -BeGreaterThan $prune
        $run = Run $script:Steps[$verify]
        $run | Should -Match '-Max 3'
        $run | Should -Match '-DeployedTag \$env:GITHUB_SHA'
        $run | Should -Match '-Image \$env:IMAGE'
        $script:Deploy.Yaml.jobs.deploy.permissions.packages | Should -Be 'write'
    }

    It 'serialises deploys so two runs cannot prune each other''s image' {
        $script:Deploy.Yaml.concurrency.group | Should -Be 'deploy-api'
        $script:Deploy.Yaml.concurrency.'cancel-in-progress' | Should -Be $false
    }
}

Describe 'deploy-api.yml order: scan before push, migrate before app update' {
    It 'scans the image before it is pushed' {
        $steps = @($script:Deploy.Yaml.jobs.image.steps)
        $scan = StepIndex $script:Deploy.Yaml.jobs.image { param($s) (Run $s) -match 'tools/scan-image\.sh' }
        $push = StepIndex $script:Deploy.Yaml.jobs.image { param($s) (Run $s) -match 'docker push' }
        $scan | Should -BeGreaterThan -1
        $push | Should -BeGreaterThan $scan
    }

    It 'runs the migration job to success before the container app is updated, and reports NO SIGNAL when it never reports a status' {
        $steps = @($script:Deploy.Yaml.jobs.deploy.steps)
        $migrate = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'az containerapp job start' }
        $update = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'az containerapp update' }
        $migrate | Should -BeGreaterThan -1
        $update | Should -BeGreaterThan $migrate
        $run = Run $steps[$migrate]
        foreach ($outcome in 'MIGRATION SUCCEEDED', 'MIGRATION FAILED', 'NO SIGNAL', 'WATCHER BROKEN', 'TIMEOUT') { $run | Should -Match $outcome }
    }
}

Describe 'INFRA-04 firewall sync is wired in, after the app update, from the deploy and a weekly workflow' {
    It 'deploy-api.yml runs infra/sync-sql-firewall.ps1 after the container app update, with the explicit subscription' {
        $steps = @($script:Deploy.Yaml.jobs.deploy.steps)
        $update = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'az containerapp update' }
        $sync = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'infra/sync-sql-firewall\.ps1' }
        $sync | Should -BeGreaterThan $update
        $run = Run $steps[$sync]
        $run | Should -Match '-SubscriptionId \$env:AZURE_SUBSCRIPTION_ID'
        $run | Should -Match '-Environment \$env:CONTAINER_ENVIRONMENT'
        $script:Deploy.Yaml.env.CONTAINER_ENVIRONMENT | Should -Be 'lifestream-env'
    }

    It 'sync-sql-firewall.yml runs weekly and on demand, signs in with OIDC, and never touches the database' {
        $y = $script:SyncFw.Yaml
        $y.on.Keys | Should -Contain 'schedule'
        $y.on.Keys | Should -Contain 'workflow_dispatch'
        @($y.on.schedule)[0].cron | Should -Match '^\d+ \d+ \* \* \d$'
        $y.jobs.sync.permissions.'id-token' | Should -Be 'write'
        $y.on.Keys | Should -Not -Contain 'pull_request'
        $script:SyncFw.Text | Should -Match 'infra/sync-sql-firewall\.ps1'
        $script:SyncFw.Text | Should -Not -Match '(?i)sqlcmd|bootstrap-sql|Invoke-Sqlcmd|client-secret|AZURE_CREDENTIALS'
        $found = [regex]::Matches($script:SyncFw.Text, 'secrets\.(\w+)') | ForEach-Object { $_.Groups[1].Value }
        @($found) | Should -BeNullOrEmpty
    }
}

Describe 'workflow robustness: the guarded steps cannot be neutered by || true or continue-on-error' {
    # The decisions are tested as scripts (SqlFirewall, ImageVersions, ScanArtifacts tests). What these tests protect is the call
    # site: a step that runs a guard must run it alone, and must be allowed to fail the job.
    It '<_> runs its guard script without ||, ; chaining, set +e or continue-on-error' -ForEach @(
        @{ File = 'Deploy'; Pattern = 'tools/scan-image\.sh|tools/prune-image-versions|tools/verify-image-versions|infra/sync-sql-firewall' }
        @{ File = 'Scan'; Pattern = 'tools/scan-image\.sh|tools/scan-artifacts\.ps1' }
        @{ File = 'Release'; Pattern = 'tools/scan-artifacts\.ps1' }
        @{ File = 'SyncFw'; Pattern = 'infra/sync-sql-firewall' }
    ) {
        $wf = (Get-Variable -Scope Script -Name $File).Value
        $guarded = @()
        foreach ($job in $wf.Yaml.jobs.Values) { foreach ($step in @($job.steps)) { if ((Run $step) -match $Pattern) { $guarded += $step } } }
        $guarded.Count | Should -BeGreaterThan 0
        foreach ($step in $guarded) {
            $step.ContainsKey('continue-on-error') | Should -BeFalse -Because ($step.name)
            (Run $step) | Should -Not -Match '\|\|\s*(true|:|exit 0)' -Because ($step.name)
            (Run $step) | Should -Not -Match 'set \+e' -Because ($step.name)
            (Run $step) | Should -Not -Match '(?m);\s*(true|exit 0)\s*$' -Because ($step.name)
        }
        foreach ($job in $wf.Yaml.jobs.Values) { if ($job.ContainsKey('continue-on-error')) { $job.'continue-on-error' | Should -Not -Be $true } }
    }

    It 'every az command in the workflows passes --subscription (az extension add and az bicep need none)' {
        foreach ($wf in $script:Deploy, $script:SyncFw, $script:Release, $script:Scan) {
            foreach ($job in $wf.Yaml.jobs.Values) {
                foreach ($step in @($job.steps)) {
                    $run = (Run $step) -replace '\\r?\n', ' '
                    foreach ($m in [regex]::Matches($run, '(?m)\baz\s+(?!extension|bicep|login)[^\r\n]*')) { $m.Value | Should -Match '--subscription' -Because $m.Value }
                }
            }
        }
    }
}

Describe 'INFRA-01 workflows scan every built artifact' {
    It 'secret-scan.yml scans the repository tree and history, the desktop and API publish output, the logs and the API image, on pushes and pull requests' {
        $script:Scan.Yaml.on.Keys | Should -Contain 'push'
        $script:Scan.Yaml.on.Keys | Should -Contain 'pull_request'
        $text = $script:Scan.Text
        $text | Should -Match 'scan-artifacts\.ps1 -Path src,tests,infra,packaging'
        $text | Should -Match 'gitleaks/gitleaks-action@v2'
        $text | Should -Match 'desktop-publish\.log'
        $text | Should -Match 'api-publish\.log'
        $text | Should -Match 'tools/scan-image\.sh lifestream-api:scan'
    }

    It 'release.yml scans the publish folder before packing and the installer and update packages before uploading' {
        $steps = @($script:Release.Yaml.jobs.release.steps)
        $scanPublish = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'scan-artifacts\.ps1.*publish' }
        $pack = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'vpk @vpkArgs' }
        $scanReleases = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'scan-artifacts\.ps1.*Releases' }
        $upload = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'vpk upload github' }
        $scanPublish | Should -BeGreaterThan -1
        $pack | Should -BeGreaterThan $scanPublish
        $scanReleases | Should -BeGreaterThan $pack
        $upload | Should -BeGreaterThan $scanReleases
    }
}

Describe 'release.yml' {
    It 'runs on v* tags, after a gate, and publishes self-contained win-x64 with ReadyToRun' {
        $script:Release.Yaml.on.push.tags | Should -Contain 'v*'
        $script:Release.Yaml.jobs.release.needs | Should -Contain 'gate'
        $script:Release.Text | Should -Match 'dotnet publish src/Lifestream\.Desktop -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true'
        $script:Release.Text | Should -Match '--runtime.*win-x64'
    }

    It 'attaches SHA256SUMS.txt, uploads with the built-in token and builds the Store MSIX as a workflow artifact, not a release asset' {
        $script:Release.Text | Should -Match 'SHA256SUMS\.txt'
        $script:Release.Text | Should -Match 'vpk upload github'
        $script:Release.Text | Should -Match 'name: store-msix'
        $script:Release.Yaml.jobs.release.permissions.contents | Should -Be 'write'
        $script:Release.Yaml.jobs.release.Keys | Should -Not -Contain 'id-token'
    }
}
