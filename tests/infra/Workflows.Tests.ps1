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

    It 'prunes with actions/delete-package-versions@v5, container packages, keeping 3 versions' {
        $i = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Uses $s) -eq 'actions/delete-package-versions@v5' }
        $i | Should -BeGreaterThan -1
        $with = $script:Steps[$i].with
        $with.'package-type' | Should -Be 'container'
        $with.'package-name' | Should -Be 'lifestream-api'
        [int]$with.'min-versions-to-keep' | Should -Be 3
    }

    It 'prunes only after the health check and the app update, so the deployed image is the newest and is never deleted' {
        $update = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'az containerapp update' }
        $health = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match '/healthz' }
        $prune = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Uses $s) -like 'actions/delete-package-versions@*' }
        $update | Should -BeGreaterThan -1
        $health | Should -BeGreaterThan $update
        $prune | Should -BeGreaterThan $health
    }

    It 'afterwards requires at most 3 remaining versions, the deployed tag among them, and docker manifest inspect to pass' {
        $prune = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Uses $s) -like 'actions/delete-package-versions@*' }
        $verify = StepIndex $script:Deploy.Yaml.jobs.deploy { param($s) (Run $s) -match 'docker manifest inspect' }
        $verify | Should -BeGreaterThan $prune
        $run = Run $script:Steps[$verify]
        $run | Should -Match '-gt 3'
        $run | Should -Match 'grep -q "\$GITHUB_SHA"'
        $run | Should -Match 'docker manifest inspect "\$IMAGE:\$GITHUB_SHA"'
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
