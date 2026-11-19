# The GitHub repository is maiznadeem/lifestream. The OIDC subject Azure trusts is case-sensitive and must equal what GitHub sends,
# so the old name (and the old casing) must not survive anywhere in a tracked file. INFRA-13 / INFRA-14.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:OldName = 'Lifestream' + '-WPF'   # split so this file does not contain the old name itself
}

Describe 'repository name' {
    It 'no tracked file still contains the old repository name (case-insensitive), except an explicit history note' {
        $hits = @(& git -C $script:Root grep -n -i -I -F -e $script:OldName -- . ':!docs/history/**' 2>$null)
        $hits | Should -BeNullOrEmpty -Because "stale references: $($hits -join '; ')"
    }

    It 'the deploy workflow image stays ghcr.io/maiznadeem/lifestream-api, matching the repository name' {
        $deploy = Get-Content (Join-Path $script:Root '.github' 'workflows' 'deploy-api.yml') -Raw
        $deploy | Should -Match 'IMAGE: ghcr\.io/maiznadeem/lifestream-api\b'
    }

    It 'the release workflow points at maiznadeem/lifestream' {
        $release = Get-Content (Join-Path $script:Root '.github' 'workflows' 'release.yml') -Raw
        $release | Should -Match 'REPO_URL: https://github\.com/maiznadeem/lifestream\r?\n'
        $release | Should -Match '--repo maiznadeem/lifestream\b'
    }
}
