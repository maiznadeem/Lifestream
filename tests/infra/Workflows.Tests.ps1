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

Describe 'INFRA-13 deploy-api.yml: keyless, separate from the PR build, no build-and-test job of its own' {
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

    # The build and every test are proven on the developer's PC before the push; the deploy does only what cannot be done there.
    It 'has two jobs, image then deploy, and builds or tests nothing but the image' {
        @($script:Deploy.Yaml.jobs.Keys | Sort-Object) | Should -Be @('deploy', 'image')
        $script:Deploy.Yaml.jobs.image.ContainsKey('needs') | Should -BeFalse
        $script:Deploy.Yaml.jobs.deploy.needs | Should -Contain 'image'
        $script:Deploy.Text | Should -Not -Match 'dotnet (build|test)'
        $script:Deploy.Text | Should -Not -Match 'mssql/server'
    }

    It 'gives every job and every step that waits on Azure a time limit that matches its own deadline' {
        $script:Deploy.Yaml.jobs.image.'timeout-minutes' | Should -BeLessOrEqual 10
        $steps = @($script:Deploy.Yaml.jobs.deploy.steps)
        $limit = @{}
        foreach ($name in 'migrate', 'update', 'health') {
            $pattern = @{ migrate = 'az containerapp job start'; update = 'az containerapp update'; health = '/healthz' }[$name]
            $i = StepIndex $script:Deploy.Yaml.jobs.deploy { param($st) (Run $st) -match $pattern }
            $i | Should -BeGreaterThan -1 -Because $name
            $steps[$i].ContainsKey('timeout-minutes') | Should -BeTrue -Because $name
            $limit[$name] = [int]$steps[$i].'timeout-minutes'
        }
        # the migration watcher gives up by itself at 1080 s and the health check at 300 s: the step limits are just above those
        $limit.migrate | Should -BeGreaterThan 18
        $limit.migrate | Should -BeLessOrEqual 22
        $limit.health | Should -BeGreaterThan 5
        $limit.health | Should -BeLessOrEqual 8
        $job = [int]$script:Deploy.Yaml.jobs.deploy.'timeout-minutes'
        $job | Should -BeGreaterOrEqual ($limit.migrate + $limit.update + $limit.health)
        $job | Should -BeLessOrEqual 35
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

# INFRA-04 as it is since 2026-11-19: the Consumption environment's shared outbound pool is 170+ addresses and adding them one rule at a
# time took longer than the deploy job, so the deploy does not sync the firewall and nothing runs on a schedule. Entra-only
# authentication is the access control; infra/sync-sql-firewall.ps1 and its workflow stay for manual use (tests/infra/BicepTests.ps1).
Describe 'INFRA-04 firewall sync is manual only: not in the deploy, not on a schedule' {
    It 'deploy-api.yml does not run infra/sync-sql-firewall.ps1, and requires Entra-only authentication BEFORE the migration job' {
        $sync = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'infra/sync-sql-firewall\.ps1' }
        $sync | Should -Be -1
        $entra = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'az sql server ad-only-auth get' }
        $migrate = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'az containerapp job start' }
        $entra | Should -BeGreaterThan -1
        $entra | Should -BeLessThan $migrate
        (Run @($script:Deploy.Yaml.jobs.deploy.steps)[$entra]) | Should -Match 'REFUSING TO DEPLOY'
    }

    It 'the docs say so too: no file that describes the sync claims it runs weekly or with every deploy' {
        $root = Join-Path $script:Dir '..' '..'
        foreach ($f in '.github/workflows/sync-sql-firewall.yml', 'infra/README.md', 'infra/main.bicep') {
            (Get-Content -Raw (Join-Path $root $f)) | Should -Not -Match '(?i)\bweekly\b|after every app update|in every `?deploy-api\.yml`? run' -Because $f
        }
    }

    It 'sync-sql-firewall.yml runs on demand only, signs in with OIDC, and never touches the database' {
        $y = $script:SyncFw.Yaml
        @($y.on.Keys) | Should -Be @('workflow_dispatch')
        $y.jobs.sync.permissions.'id-token' | Should -Be 'write'
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
        @{ File = 'Deploy'; Pattern = 'tools/scan-image\.sh|tools/prune-image-versions|tools/verify-image-versions' }
        @{ File = 'Scan'; Pattern = 'tools/scan-image\.sh|tools/scan-artifacts\.ps1' }
        @{ File = 'Release'; Pattern = 'tools/scan-artifacts\.ps1' }
        @{ File = 'Ci'; Pattern = 'tools/scan-artifacts\.ps1' }
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
    It 'secret-scan.yml scans the repository tree and history on pull requests and pushes to master, and keeps the desktop, API publish and image scans for manual runs' {
        $script:Scan.Yaml.on.Keys | Should -Contain 'push'
        $script:Scan.Yaml.on.Keys | Should -Contain 'pull_request'
        $script:Scan.Yaml.on.Keys | Should -Contain 'workflow_dispatch'
        $text = $script:Scan.Text
        $text | Should -Match 'scan-artifacts\.ps1 -Path src,tests,infra,packaging'
        $text | Should -Match 'gitleaks/gitleaks-action@v3'
        $text | Should -Match 'desktop-publish\.log'
        $text | Should -Match 'api-publish\.log'
        $text | Should -Match 'tools/scan-image\.sh lifestream-api:scan'
        # both scans of the repository job are in the job that runs by itself
        $repo = (@($script:Scan.Yaml.jobs.repository.steps) | ForEach-Object { (Run $_) + ' ' + (Uses $_) }) -join "`n"
        $repo | Should -Match 'scan-artifacts\.ps1 -Path src,tests,infra,packaging -GitTracked'
        $repo | Should -Match 'gitleaks/gitleaks-action@v3'
    }

    # The desktop job of secret-scan.yml was red from 2026-11-19: it published the API without a runtime identifier, which copies
    # the native libraries of every platform (two macOS sign-in libraries had 66 findings), while the image is built for linux-x64.
    It 'secret-scan.yml publishes what ships, as it ships: the API exactly as the Dockerfile publishes it, the desktop app for win-x64 self-contained' {
        $dockerfile = Get-Content (Join-Path $script:Dir '..' '..' 'src' 'Lifestream.Api' 'Dockerfile')
        $imagePublish = @($dockerfile | Where-Object { $_ -match '^RUN dotnet publish ' })
        $imagePublish.Count | Should -Be 1
        $steps = @($script:Scan.Yaml.jobs.desktop.steps)
        $api = @($steps | Where-Object { (Run $_) -match 'dotnet publish src/Lifestream\.Api\b' })
        $api.Count | Should -Be 1
        $command = (Run $api[0]) -replace '\s+', ' '
        # every switch of the image's publish that decides which files are published (the output folder and --no-restore do not)
        $switches = @([regex]::Matches($imagePublish[0], '(?<= )(-c \S+|-r \S+|--self-contained \S+|-p:\S+)') | ForEach-Object Value)
        $switches | Should -Contain '-r linux-x64'
        $switches | Should -Contain '--self-contained false'
        foreach ($s in $switches) { $command | Should -Match ([regex]::Escape($s) + '(?= |$)') -Because "the Dockerfile publishes with $s" }
        $desktop = @($steps | Where-Object { (Run $_) -match 'dotnet publish src/Lifestream\.Desktop\b' })
        $desktop.Count | Should -Be 1
        ((Run $desktop[0]) -replace '\s+', ' ') | Should -Match '-r win-x64 --self-contained(?= )'
        # no publish in the scan workflow may leave the runtime identifier out
        foreach ($step in $steps) { foreach ($m in [regex]::Matches(((Run $step) -replace '\s+', ' '), 'dotnet publish [^'']*')) { $m.Value | Should -Match ' -r \S+' -Because $m.Value } }
    }

    It 'release.yml scans the publish folder before packing and the installer and update packages before uploading' {
        $steps = @($script:Release.Yaml.jobs.release.steps)
        $scanPublish = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'scan-artifacts\.ps1.*publish' }
        $pack = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'vpk @global:vpkArgs' }
        $scanReleases = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'scan-artifacts\.ps1.*Releases' }
        $upload = StepIndex $script:Release.Yaml.jobs.release { param($s) (Run $s) -match 'vpk upload github' }
        $scanPublish | Should -BeGreaterThan -1
        $pack | Should -BeGreaterThan $scanPublish
        $scanReleases | Should -BeGreaterThan $pack
        $upload | Should -BeGreaterThan $scanReleases
    }
}

Describe 'release.yml' {
    It 'is started by hand only and on a version tag, after a gate, and publishes self-contained win-x64 with ReadyToRun' {
        @($script:Release.Yaml.on.Keys) | Should -Be @('workflow_dispatch')
        $version = @($script:Release.Yaml.jobs.release.steps | Where-Object { $_.ContainsKey('id') -and $_.id -eq 'version' })[0]
        (Run $version) | Should -Match "GITHUB_REF_TYPE -ne 'tag'"
        (Run $version) | Should -Match '\^v\(\?<v>'
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

# GitHub Actions minutes are limited (2026-11-20: a release waited 20 minutes on them). The build and every test are proven on the
# developer's PC before a push, so what a push, a pull request or a tag starts by itself must finish in under 2 minutes.
# deploy-api.yml is the stated exception: its migration job and rollout are Azure's time (about 5 minutes, see its header).
Describe 'automatic runs are short: one run per push, under two minutes' {
    BeforeAll {
        $script:All = @{}
        foreach ($f in Get-ChildItem $script:Dir -Filter '*.yml') { $script:All[$f.Name] = Load $f.Name }
        function script:Auto($job) { -not ($job.ContainsKey('if') -and [string]$job.if -match "github\.event_name == 'workflow_dispatch'") }
        function script:Scheduled($job) { $job.ContainsKey('if') -and [string]$job.if -match "github\.event_name == 'schedule'" }
    }

    It 'no workflow starts on a tag or on a branch other than master, and only secret-scan.yml has a schedule, once a week' {
        foreach ($name in $script:All.Keys) {
            $on = $script:All[$name].Yaml.on
            if ($name -eq 'secret-scan.yml') {
                @($on.schedule).Count | Should -Be 1 -Because 'one weekly run of the full-history scan'
                [string]@($on.schedule)[0].cron | Should -Match '^\d{1,2} \d{1,2} \* \* [0-6]$' -Because 'a minute, an hour and one day of the week'
            }
            else { $on.Keys | Should -Not -Contain 'schedule' -Because $name }
            $on.Keys | Should -Not -Contain 'release' -Because $name
            $on.Keys | Should -Not -Contain 'create' -Because $name
            if ($on.Keys -contains 'push') {
                # a bare `push:` runs on every branch and every tag
                $on.push | Should -Not -BeNullOrEmpty -Because "$name has a bare push trigger"
                $on.push.Keys | Should -Not -Contain 'tags' -Because $name
                @($on.push.branches) | Should -Be @('master') -Because $name
            }
        }
    }

    It 'release.yml and screenshots.yml are started by hand only' {
        @($script:Release.Yaml.on.Keys) | Should -Be @('workflow_dispatch')
        @($script:All['screenshots.yml'].Yaml.on.Keys) | Should -Be @('workflow_dispatch')
    }

    # A branch is checked by its pull request and master by the push of the merge, so one push starts one run of each check;
    # the pull request number in the group makes a newer push cancel the older run.
    It '<_> runs once per push (pull_request for branches, push for master only) and a newer push cancels the older run' -ForEach @('ci.yml', 'secret-scan.yml') {
        $y = $script:All[$_].Yaml
        $y.on.Keys | Should -Contain 'pull_request'
        $y.on.pull_request | Should -BeNullOrEmpty
        @($y.on.push.branches) | Should -Be @('master')
        $y.concurrency.group | Should -Match 'github\.event\.pull_request\.number \|\| github\.ref'
        $y.concurrency.'cancel-in-progress' | Should -Be $true
    }

    It 'every job that starts by itself in ci.yml and secret-scan.yml has a runner limit of 2 minutes, and every other job runs on workflow_dispatch only' {
        $auto = @(); $scheduled = @()
        foreach ($name in 'ci.yml', 'secret-scan.yml') {
            $jobs = $script:All[$name].Yaml.jobs
            foreach ($jobName in $jobs.Keys) {
                $job = $jobs[$jobName]
                if (Auto $job) {
                    $auto += "$name/$jobName"
                    [int]$job.'timeout-minutes' | Should -BeLessOrEqual 2 -Because "$name/$jobName starts by itself"
                    [string]$job.if | Should -Match "github\.event_name != 'workflow_dispatch'" -Because "$name/$jobName"
                    if ($script:All[$name].Yaml.on.Keys -contains 'schedule') { [string]$job.if | Should -Match "github\.event_name != 'schedule'" -Because "$name/$jobName must not also run on the weekly schedule" }
                    [string]$job.'runs-on' | Should -Be 'ubuntu-latest' -Because "$name/$jobName"
                }
                elseif (Scheduled $job) {
                    $scheduled += "$name/$jobName"
                    [int]$job.'timeout-minutes' | Should -BeLessOrEqual 2 -Because "$name/$jobName starts by itself every week"
                    [string]$job.'runs-on' | Should -Be 'ubuntu-latest' -Because "$name/$jobName"
                }
                else { [string]$job.if | Should -Be "github.event_name == 'workflow_dispatch'" -Because "$name/$jobName" }
            }
        }
        @($auto | Sort-Object) | Should -Be @('ci.yml/fast', 'secret-scan.yml/repository')
        @($scheduled) | Should -Be @('secret-scan.yml/history')
    }

    It 'ci.yml keeps the full jobs (api, desktop, both test orders) for manual runs' {
        foreach ($jobName in 'api', 'desktop', 'test-order') {
            $script:Ci.Yaml.jobs.Keys | Should -Contain $jobName
            (Auto $script:Ci.Yaml.jobs[$jobName]) | Should -BeFalse -Because $jobName
        }
        @($script:Ci.Yaml.jobs.'test-order'.strategy.matrix.include).Count | Should -Be 2
    }

    It 'secret-scan.yml keeps the desktop and api-image jobs for manual runs' {
        foreach ($jobName in 'desktop', 'api-image') { (Auto $script:Scan.Yaml.jobs[$jobName]) | Should -BeFalse -Because $jobName }
    }

    # gitleaks-action scans the whole history (no range) on a schedule or a run started by hand, and only the event's commits on a
    # push or pull request. The reviewed findings of the history are in .gitleaksignore (ScanGitleaks.Tests.ps1 tests that list).
    It 'secret-scan.yml scans the WHOLE history in its own job, weekly and by hand, never on a push or pull request' {
        $job = $script:Scan.Yaml.jobs.history
        $job | Should -Not -BeNullOrEmpty
        [string]$job.if | Should -Be "github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'"
        $steps = @($job.steps)
        $checkout = @($steps | Where-Object { (Uses $_) -like 'actions/checkout@*' })
        $checkout.Count | Should -Be 1
        $checkout[0].with.'fetch-depth' | Should -Be 0 -Because 'every commit, not only the last one'
        $scan = @($steps | Where-Object { (Uses $_) -eq 'gitleaks/gitleaks-action@v3' })
        $scan.Count | Should -Be 1
        # nothing that narrows the scan: no range, no other configuration, no disabled report
        foreach ($k in 'BASE_REF', 'GITLEAKS_CONFIG', 'GITLEAKS_VERSION') { $scan[0].env.Keys | Should -Not -Contain $k }
        @($steps | Where-Object { (Run $_) -match 'gitleaks' }) | Should -BeNullOrEmpty -Because 'the action runs gitleaks, no step of its own'
        # the weekly run has its own concurrency group, so it never cancels a push to master
        $script:Scan.Yaml.concurrency.group | Should -Match 'github\.event_name'
    }

    It 'the fast job builds and tests Client and Domain only, through the scanning wrapper, with the NuGet cache' {
        $job = $script:Ci.Yaml.jobs.fast
        $steps = @($job.steps)
        $test = StepIndex $job { param($s) (Run $s) -match 'dotnet test' }
        $test | Should -BeGreaterThan -1
        $run = Run $steps[$test]
        $run | Should -Match "foreach \(\`$project in 'Client', 'Domain'\)"
        $run | Should -Match 'run-logged\.ps1 -Log "\$env:RUNNER_TEMP/test-logs/fast-\$project\.log" -Command "dotnet test tests/Lifestream\.\$project\.Tests -c Release"'
        $run | Should -Match 'if \(\$failed -gt 0\) \{ exit 1 \}'
        $all = ($steps | ForEach-Object { Run $_ }) -join "`n"
        $all | Should -Not -Match 'Lifestream\.(Api|Desktop)\.Tests|Lifestream\.slnx|mssql|docker'
        $cache = @($steps | Where-Object { (Uses $_) -like 'actions/cache@*' })
        $cache.Count | Should -Be 1
        $cache[0].with.path | Should -Match '\.nuget/packages'
        $cache[0].with.key | Should -Match 'hashFiles\(.*Directory\.Packages\.props'
        $scan = StepIndex $job { param($s) (Run $s) -match 'tools/scan-artifacts\.ps1 -Path "\$env:RUNNER_TEMP/test-logs"' }
        $scan | Should -BeGreaterThan $test
        $steps[$scan].if | Should -Match 'always\(\)'
    }

    It 'the fast job has a budget of 90 seconds, counted from its first step and checked by its last, which fails the run when it is exceeded' {
        $job = $script:Ci.Yaml.jobs.fast
        $steps = @($job.steps)
        [string]$job.env.BUDGET_SECONDS | Should -Be '90'
        (Run $steps[0]) | Should -Match 'FAST_STARTED=\$\(date \+%s\)" >> "\$GITHUB_ENV"'
        $last = $steps[$steps.Count - 1]
        $last.if | Should -Match 'always\(\)'
        $last.ContainsKey('continue-on-error') | Should -BeFalse
        $run = Run $last
        $run | Should -Match 'elapsed=\$\(\( \$\(date \+%s\) - FAST_STARTED \)\)'
        $run | Should -Match 'if \[ "\$elapsed" -gt "\$BUDGET_SECONDS" \]; then'
        $run | Should -Match 'BUDGET EXCEEDED'
        $run | Should -Match '(?m)^\s*exit 1\s*$'
    }

    # The budget step is run for real: 30 s into a 90 s budget passes, 120 s fails.
    It 'the budget step itself exits <Exit> when the job took <Took> s' -ForEach @(@{ Took = 30; Exit = 0 }, @{ Took = 120; Exit = 1 }) {
        $steps = @($script:Ci.Yaml.jobs.fast.steps)
        $bash = @('C:\Program Files\Git\bin\bash.exe', '/usr/bin/bash', '/bin/bash') | Where-Object { Test-Path $_ } | Select-Object -First 1
        $bash | Should -Not -BeNullOrEmpty
        $file = Join-Path $TestDrive "budget-$Took.sh"
        [IO.File]::WriteAllText($file, (Run $steps[$steps.Count - 1]).Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        $psi = [Diagnostics.ProcessStartInfo]::new($bash)
        foreach ($a in '--noprofile', '--norc', '-e', $file.Replace('\', '/')) { $psi.ArgumentList.Add($a) }
        $psi.Environment['FAST_STARTED'] = [string]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - $Took)
        $psi.Environment['BUDGET_SECONDS'] = '90'
        $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $proc = [Diagnostics.Process]::Start($psi)
        $out = $proc.StandardOutput.ReadToEnd() + $proc.StandardError.ReadToEnd(); $proc.WaitForExit()
        $proc.ExitCode | Should -Be $Exit -Because $out
        if ($Exit -ne 0) { $out | Should -Match 'BUDGET EXCEEDED' }
    }
}

Describe 'actionlint passes on every workflow, ci.yml included' {
    It 'actionlint (LIFESTREAM_ACTIONLINT or PATH) reports no problem in <_>' -ForEach @('ci.yml', 'deploy-api.yml', 'release.yml', 'screenshots.yml', 'secret-scan.yml', 'sync-sql-firewall.yml') {
        $actionlint = if ($env:LIFESTREAM_ACTIONLINT) { $env:LIFESTREAM_ACTIONLINT } else { (Get-Command actionlint -ErrorAction SilentlyContinue).Source }
        $actionlint | Should -Not -BeNullOrEmpty -Because 'actionlint is required; a missing linter must fail, not skip'
        $out = & $actionlint (Join-Path $script:Dir $_) 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0 -Because $out
    }

    It 'ci.yml sets the render capture folder in a step (the runner context is not available in a job-level env)' {
        $script:Ci.Yaml.jobs.desktop.ContainsKey('env') | Should -BeFalse
        $first = @($script:Ci.Yaml.jobs.desktop.steps)[0]
        (Run $first) | Should -Match 'LIFESTREAM_CAPTURE_DIR=\$env:RUNNER_TEMP\\captures" >> \$env:GITHUB_ENV'
    }
}

Describe 'INFRA-01 workflows scan the logs: docker build output, vpk output, test logs and captures before upload' {
    It 'ci.yml writes the output of every dotnet build and test run to a file, scans it (both scanners) before it is printed, and scans the log folder even when tests fail, in the api and desktop jobs' {
        foreach ($jobName in 'api', 'desktop') {
            $job = $script:Ci.Yaml.jobs[$jobName]
            $test = StepIndex $job { param($s) (Run $s) -match 'dotnet test' }
            $build = StepIndex $job { param($s) (Run $s) -match 'dotnet build' }
            $scan = StepIndex $job { param($s) (Run $s) -match 'tools/scan-artifacts\.ps1 -Path "\$env:RUNNER_TEMP/test-logs"' }
            $test | Should -BeGreaterThan -1 -Because $jobName
            $build | Should -BeGreaterThan -1 -Because $jobName
            $scan | Should -BeGreaterThan $test -Because $jobName
            $steps = @($job.steps)
            $steps[$scan].if | Should -Match 'always\(\)' -Because $jobName
            # no command prints live: every dotnet command runs inside tools/run-logged.ps1 (file first, scan, then print)
            foreach ($i in $build, $test) {
                $lines = (Run $steps[$i]) -split "`n" | Where-Object { $_ -match 'dotnet (build|test)' }
                foreach ($l in $lines) { $l | Should -Match 'run-logged\.ps1 .*-Log "\$env:RUNNER_TEMP/test-logs/.*-Command' -Because "$jobName`: $l" }
            }
        }
        $apiTest = @($script:Ci.Yaml.jobs.api.steps)[(StepIndex $script:Ci.Yaml.jobs.api { param($s) (Run $s) -match 'dotnet test' })]
        (Run $apiTest) | Should -Match 'if \(\$failed -gt 0\) \{ exit 1 \}'
        (Run @($script:Ci.Yaml.jobs.desktop.steps)[(StepIndex $script:Ci.Yaml.jobs.desktop { param($s) (Run $s) -match 'dotnet test' })]) | Should -Match 'if \(\$client -ne 0 -or \$desktop -ne 0\) \{ exit 1 \}'
    }

    It 'screenshots.yml writes its build and generation output to files and uploads only after a clean scan of the whole upload folder' {
        $shots = Load 'screenshots.yml'
        $wf = $shots.Text
        $job = $shots.Yaml.jobs.screenshots
        $steps = @($job.steps)
        $scan = StepIndex $job { param($s) (Run $s) -match 'scan-artifacts\.ps1 -Path "\$env:RUNNER_TEMP/screenshots"' }
        $upload = StepIndex $job { param($s) (Uses $s) -like 'actions/upload-artifact*' }
        $scan | Should -BeGreaterThan -1
        $upload | Should -BeGreaterThan $scan
        $steps[$scan].id | Should -Be 'scan-screenshots'
        $steps[$upload].if | Should -Match "steps\.scan-screenshots\.outcome == 'success'"
        $steps[$upload].with.path | Should -Match 'runner\.temp \}\}/screenshots$'
        foreach ($m in [regex]::Matches($wf, '(?m)^\s*run: .*(dotnet build|generate-screenshots).*$')) { $m.Value | Should -Match 'run-logged\.ps1' -Because $m.Value }
    }

    It 'no workflow turns the second scanner off' {
        foreach ($f in Get-ChildItem $script:Dir -Filter '*.yml') {
            (Get-Content -Raw $f.FullName) | Should -Not -Match 'SkipGitleaks' -Because $f.Name
        }
    }

    It 'ci.yml scans the render captures with the test logs and uploads them only when that scan passed' {
        $job = $script:Ci.Yaml.jobs.desktop
        $scan = StepIndex $job { param($s) (Run $s) -match 'scan-artifacts\.ps1 .*LIFESTREAM_CAPTURE_DIR' }
        $upload = StepIndex $job { param($s) (Uses $s) -like 'actions/upload-artifact*' }
        $scan | Should -BeGreaterThan -1
        $upload | Should -BeGreaterThan $scan
        $steps = @($job.steps)
        $steps[$scan].id | Should -Be 'scan-logs'
        $steps[$upload].if | Should -Match "steps\.scan-logs\.outcome == 'success'"
    }

    It '<File> keeps the docker build output (bash, pipefail) and scans it with the image before anything is pushed' -ForEach @(
        @{ File = 'Scan'; Job = 'api-image' }, @{ File = 'Deploy'; Job = 'image' }
    ) {
        $wf = (Get-Variable -Scope Script -Name $File).Value
        $job = $wf.Yaml.jobs[$Job]
        $build = StepIndex $job { param($s) (Run $s) -match 'docker buildx build' }
        $scan = StepIndex $job { param($s) (Run $s) -match 'scan-artifacts\.ps1 -Path "\$RUNNER_TEMP/docker-build\.log"' }
        $image = StepIndex $job { param($s) (Run $s) -match 'tools/scan-image\.sh' }
        $steps = @($job.steps)
        (Run $steps[$build]) | Should -Match 'run-logged\.ps1 -Log "\$RUNNER_TEMP/docker-build\.log" -Command "docker buildx build --progress=plain '
        $steps[$build].shell | Should -Be 'bash'
        $scan | Should -BeGreaterThan $build
        $image | Should -BeGreaterThan $build
        $push = StepIndex $job { param($s) (Run $s) -match 'docker push' }
        if ($push -ge 0) { $push | Should -BeGreaterThan $scan }
    }

    It 'release.yml keeps the vpk pack output (through the scanning wrapper) and scans it with the installer before the upload' {
        $job = $script:Release.Yaml.jobs.release
        $pack = StepIndex $job { param($s) (Run $s) -match 'run-logged\.ps1 -Log "\$env:RUNNER_TEMP/vpk\.log" -Command ''vpk @global:vpkArgs''' }
        $scan = StepIndex $job { param($s) (Run $s) -match 'scan-artifacts\.ps1 -Path "\$env:RUNNER_TEMP/Releases","\$env:RUNNER_TEMP/vpk\.log"' }
        $upload = StepIndex $job { param($s) (Run $s) -match 'vpk upload github' }
        $pack | Should -BeGreaterThan -1
        $scan | Should -BeGreaterThan $pack
        $upload | Should -BeGreaterThan $scan
        (Run @($job.steps)[$pack]) | Should -Match 'if \(\$LASTEXITCODE -ne 0\) \{ exit \$LASTEXITCODE \}'
    }

    It 'secret-scan.yml scans the repository as git sees it (-GitTracked), never by walking the working folder' {
        $script:Scan.Text | Should -Match 'scan-artifacts\.ps1 -Path src,tests,infra,packaging -GitTracked'
        $script:Scan.Text | Should -Not -Match 'SkipBuildOutput'
    }
}

# b-03h: the first real run of ci.yml failed with `MSBUILD : error MSB1008: Only one project can be specified` (a `dotnet build` with three
# projects), and a static match on the YAML text did not see it. These tests do two things for EVERY step of EVERY workflow that builds,
# tests, publishes, packs or builds an image: (1) assert statically that the command prints through tools/run-logged.ps1 (file, scan, then
# print: no log line reaches the public job log unscanned), and (2) EXECUTE the step's exact script the way the runner does (pwsh, or bash
# with -e) against dry-run dotnet, docker and vpk (tests/infra/fake-build-tool.ps1, which refuses a second project like MSBuild), with the
# real run-logged.ps1 and the real scanner, and assert it exits 0 and really called the tool.
BeforeDiscovery {
    $yamlModule = if ($env:LIFESTREAM_YAML_MODULE) { Join-Path $env:LIFESTREAM_YAML_MODULE 'powershell-yaml' } else { 'powershell-yaml' }
    Import-Module $yamlModule -Force
    $wfDir = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows')).Path
    # one command of a workflow that produces build output (a log line that must be scanned before it is printed)
    $buildCommand = '(?<![\w.$:/\\-])(?:dotnet\s+(?:build|test|publish|pack|msbuild|run)\b|docker\s+(?:buildx\s+build|build)\b|vpk(?![\w.-]))|&\s*"\$testExe"'
    $toolCommand = 'dotnet\s+(build|test|publish)|docker\s+buildx\s+build|(?<![\w.$:/\\-])vpk(?![\w.-])'
    $toolCases = [Collections.Generic.List[object]]::new()
    $stepCases = [Collections.Generic.List[object]]::new()
    foreach ($f in Get-ChildItem $wfDir -Filter '*.yml') {
        $y = ConvertFrom-Yaml (Get-Content $f.FullName -Raw)
        foreach ($jobName in @($y.jobs.Keys)) {
            $job = $y.jobs[$jobName]
            $entries = @($null)
            if ($job.ContainsKey('strategy') -and $job.strategy.ContainsKey('matrix') -and $job.strategy.matrix.ContainsKey('include')) { $entries = @($job.strategy.matrix.include) }
            foreach ($m in $entries) {
                $n = 0
                foreach ($step in @($job.steps)) {
                    $n++
                    if (-not $step.ContainsKey('run')) { continue }
                    $run = [string]$step.run
                    $lines = @($run -split "`r?`n" | Where-Object { $_ -notmatch '^\s*#' -and $_ -notmatch 'dotnet\s+tool\s+install' -and $_ -match $buildCommand })
                    if ($lines.Count -eq 0) { continue }
                    $runsOn = if ($m -and $m.ContainsKey('os')) { [string]$m.os } else { [string]$job.'runs-on' }
                    $shell = if ($step.ContainsKey('shell')) { [string]$step.shell } elseif ($runsOn -match 'windows') { 'pwsh' } else { 'bash' }
                    $shellExplicit = $step.ContainsKey('shell')
                    $stepEnv = @{}
                    foreach ($src in $y.env, $job.env, $step.env) { if ($src) { foreach ($k in $src.Keys) { if ([string]$src[$k] -notmatch '\$\{\{') { $stepEnv[$k] = [string]$src[$k] } } } }
                    $name = if ($step.ContainsKey('name')) { [string]$step.name } else { "step $n" }
                    $label = if ($m) { "$($f.Name) / $jobName ($($m.name)) / $name" } else { "$($f.Name) / $jobName / $name" }
                    $stepCases.Add(@{ Label = $label; Lines = $lines })
                    if ($run -match $toolCommand) {
                        $toolCases.Add(@{ Label = $label; Run = $run; Shell = $shell; ShellExplicit = $shellExplicit; Env = $stepEnv; Matrix = $m })
                    }
                }
            }
        }
    }
}

BeforeAll {
    $script:Repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:FakeTool = (Resolve-Path (Join-Path $PSScriptRoot 'fake-build-tool.ps1')).Path
    $hostPath = (Get-Process -Id $PID).Path
    # a pwsh installed as a dotnet tool runs as `dotnet pwsh.dll`: the shims and steps need the pwsh launcher itself
    $script:PwshExe = if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { (Get-Command pwsh -ErrorAction Stop).Source } else { $hostPath }
    $bashCandidates = @('C:\Program Files\Git\bin\bash.exe', '/usr/bin/bash', '/bin/bash') | Where-Object { Test-Path $_ }
    $script:BashExe = @($bashCandidates)[0]
    $script:Sep = [IO.Path]::PathSeparator
    # the runtime that hosts this pwsh, for the child pwsh of a dotnet-tool install (a tool apphost takes the first dotnet.exe on PATH, which may be a newer SDK without that runtime, so that folder comes right after the shims)
    $script:HostDotnetRoot = if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { Split-Path $hostPath } else { $null }

    # Runs a step's script exactly like the Actions runner: pwsh gets `$ErrorActionPreference = 'stop'` first and the LASTEXITCODE exit
    # last, bash gets -e (and -o pipefail when `shell: bash` is written out).
    function script:Invoke-WorkflowScript([string]$Run, [string]$Shell, [bool]$ShellExplicit, [hashtable]$Env, $Matrix) {
        $work = Join-Path $TestDrive ('wf-' + [guid]::NewGuid().ToString('N')); $bin = Join-Path $work 'bin'; $temp = Join-Path $work 'temp'
        New-Item -ItemType Directory -Force $bin, $temp | Out-Null
        $callLog = Join-Path $work 'calls.log'
        $tool = $script:FakeTool.Replace('\', '/'); $pwshFwd = $script:PwshExe.Replace('\', '/')
        foreach ($n in 'dotnet', 'docker', 'vpk') {
            [IO.File]::WriteAllText((Join-Path $bin $n), "#!/usr/bin/env bash`nexec `"$pwshFwd`" -NoProfile -File `"$tool`" $n `"`$@`"`n", [Text.UTF8Encoding]::new($false))
            if ($IsWindows) { [IO.File]::WriteAllText((Join-Path $bin "$n.cmd"), "@`"$($script:PwshExe)`" -NoProfile -File `"$($script:FakeTool)`" $n %*`r`n") }
            else { chmod +x (Join-Path $bin $n) }
        }
        # expressions the runner would evaluate
        $projects = if ($Matrix -and $Matrix.ContainsKey('projects')) { [string]$Matrix.projects } else { '' }
        $text = $Run.Replace('${{ matrix.projects }}', $projects)
        $text = [regex]::Replace($text, '\$\{\{\s*steps\.version\.outputs\.prerelease[^}]*\}\}', '')
        $text = [regex]::Replace($text, '\$\{\{\s*steps\.version\.outputs\.version\s*\}\}', '1.2.3')
        $text = [regex]::Replace($text, '\$\{\{\s*steps\.version\.outputs\.tag\s*\}\}', 'v1.2.3')
        $text = [regex]::Replace($text, '\$\{\{\s*steps\.signing\.outputs\.enabled\s*\}\}', 'false')
        $text = [regex]::Replace($text, '\$\{\{[^}]*\}\}', 'x')
        $ext = if ($Shell -eq 'pwsh') { 'ps1' } else { 'sh' }
        $file = Join-Path $work "step.$ext"
        if ($Shell -eq 'pwsh') { $text = "`$ErrorActionPreference = 'stop'`n$text`nif ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit `$LASTEXITCODE }`n" }
        [IO.File]::WriteAllText($file, $text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        $psi = if ($Shell -eq 'pwsh') {
            $p = [Diagnostics.ProcessStartInfo]::new($script:PwshExe); foreach ($a in '-NoProfile', '-Command', ". '$file'") { $p.ArgumentList.Add($a) }; $p
        } else {
            $p = [Diagnostics.ProcessStartInfo]::new($script:BashExe); foreach ($a in '--noprofile', '--norc', '-e') { $p.ArgumentList.Add($a) }
            if ($ShellExplicit) { $p.ArgumentList.Add('-o'); $p.ArgumentList.Add('pipefail') }
            $p.ArgumentList.Add($file.Replace('\', '/')); $p
        }
        $psi.WorkingDirectory = $script:Repo
        $dirs = @($bin); if ($script:HostDotnetRoot) { $dirs += $script:HostDotnetRoot }; $dirs += Split-Path $script:PwshExe; if ($script:BashExe) { $dirs += Split-Path $script:BashExe }
        $psi.Environment['PATH'] = ($dirs -join $script:Sep) + $script:Sep + $env:PATH
        $psi.Environment.Remove('DOTNET_ROOT') | Out-Null
        if ($script:HostDotnetRoot) { $psi.Environment['DOTNET_ROOT'] = $script:HostDotnetRoot }
        foreach ($k in $Env.Keys) { $psi.Environment[$k] = $Env[$k] }
        $psi.Environment['RUNNER_TEMP'] = $temp; $psi.Environment['LIFESTREAM_CAPTURE_DIR'] = Join-Path $temp 'captures'
        $psi.Environment['GITHUB_SHA'] = 'a3f9c2e1b7d4085e6f1c9a2b3d4e5f60718293a4'; $psi.Environment['GITHUB_REF_NAME'] = 'v1.2.3'; $psi.Environment['FAKE_TOOL_LOG'] = $callLog
        $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $proc = [Diagnostics.Process]::Start($psi)
        $o = $proc.StandardOutput.ReadToEndAsync(); $e = $proc.StandardError.ReadToEndAsync(); $proc.WaitForExit()
        [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = $o.Result + $e.Result; Calls = @(if (Test-Path $callLog) { Get-Content $callLog }) }
    }
}

Describe 'INFRA-01 every workflow step that builds, tests, publishes, packs or builds an image prints through the scanning wrapper' {
    It 'found the steps to check (a vacuous pass would hide a broken discovery): <Steps> steps, <Tools> that call a tool' -ForEach @(@{ Steps = $stepCases.Count; Tools = $toolCases.Count }) {
        $Steps | Should -BeGreaterThan 15
        $Tools | Should -BeGreaterThan 12
    }

    It '<Label>: every command line goes through tools/run-logged.ps1 (written to a file, scanned, then printed)' -ForEach $stepCases {
        foreach ($l in $Lines) { $l | Should -Match 'run-logged\.ps1' -Because "this command prints straight to the job log: $l" }
    }

    It 'the check itself can fail: a bare dotnet build line is not accepted' {
        'dotnet build Lifestream.slnx -c Release' | Should -Not -Match 'run-logged\.ps1'
        'docker buildx build --load . 2>&1 | tee build.log' | Should -Not -Match 'run-logged\.ps1'
        'vpk pack --packId x' | Should -Not -Match 'run-logged\.ps1'
    }
}

Describe 'INFRA-01 the exact workflow command lines run (dry-run tools, real wrapper and scanner)' {
    It '<Label>: the script runs to exit 0 and calls the tool' -ForEach $toolCases {
        $r = Invoke-WorkflowScript $Run $Shell $ShellExplicit $Env $Matrix
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $r.Output | Should -Not -Match 'MSB1008'
        $r.Output | Should -Not -Match 'was NOT printed'
        $r.Calls.Count | Should -BeGreaterThan 0 -Because "no dotnet, docker or vpk call was made:`n$($r.Output)"
        if ($Run -match 'dotnet\s+build') { @($r.Calls | Where-Object { $_ -match '^dotnet build ' }).Count | Should -BeGreaterThan 0 }
        if ($Run -match 'dotnet\s+test') { @($r.Calls | Where-Object { $_ -match '^dotnet test ' }).Count | Should -BeGreaterThan 0 }
        if ($Run -match 'dotnet\s+publish') { @($r.Calls | Where-Object { $_ -match '^dotnet publish ' }).Count | Should -BeGreaterThan 0 }
        if ($Run -match 'docker\s+buildx\s+build') { @($r.Calls | Where-Object { $_ -match '^docker buildx build ' }).Count | Should -BeGreaterThan 0 }
        if ($Run -match '(?<![\w.$:/\\-])vpk(?![\w.-])') { @($r.Calls | Where-Object { $_ -match '^vpk ' }).Count | Should -BeGreaterThan 0 }
    }

    It 'the harness can fail: the ci.yml build step as it was (three projects in one dotnet build) exits 1 with MSB1008' {
        $old = "./tools/run-logged.ps1 -Log `"`$env:RUNNER_TEMP/test-logs/build.log`" -Command 'dotnet build tests/Lifestream.Domain.Tests tests/Lifestream.Client.Tests tests/Lifestream.Api.Tests -c Release'"
        $r = Invoke-WorkflowScript $old 'pwsh' $true @{} $null
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'MSB1008'
    }
}
