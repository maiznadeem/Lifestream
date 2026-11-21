# The GitHub repository is maiznadeem/Lifestream (renamed from the all-lowercase name on 2026-11-21). The OIDC subject Azure trusts is
# case-sensitive and must equal what GitHub sends, so the old names (and the old casing) must not survive in a tracked file. Container
# image names must be lowercase, so the image stays ghcr.io/maiznadeem/lifestream-api. INFRA-13 / INFRA-14.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:OldName = 'Lifestream' + '-WPF'   # split so this file does not contain the old name itself
    $script:OldCasing = 'maiznadeem/' + 'lifestream'
}

Describe 'repository name' {
    It 'no tracked file still contains the old repository name (case-insensitive), except an explicit history note' {
        $hits = @(& git -C $script:Root grep -n -i -I -F -e $script:OldName -- . ':!docs/history/**' 2>$null)
        $hits | Should -BeNullOrEmpty -Because "stale references: $($hits -join '; ')"
    }

    It 'no tracked file still uses the old lowercase repository name, except lowercase image names and the dated plans' {
        $hits = @(& git -C $script:Root grep -n -I -P -e "$($script:OldCasing)(?!-)" -- . ':!docs/superpowers/**' ':!docs/history/**' 2>$null)
        $hits | Should -BeNullOrEmpty -Because "stale references: $($hits -join '; ')"
    }

    It 'the deploy workflow image stays ghcr.io/maiznadeem/lifestream-api, because image names must be lowercase' {
        $deploy = Get-Content (Join-Path $script:Root '.github' 'workflows' 'deploy-api.yml') -Raw
        $deploy | Should -Match 'IMAGE: ghcr\.io/maiznadeem/lifestream-api\b'
    }

    It 'the release workflow points at maiznadeem/Lifestream' {
        $release = Get-Content (Join-Path $script:Root '.github' 'workflows' 'release.yml') -Raw
        $release | Should -Match 'REPO_URL: https://github\.com/maiznadeem/Lifestream\r?\n'
        $release | Should -Match '--repo maiznadeem/Lifestream\b'
    }
}
