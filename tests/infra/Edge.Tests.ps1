# The Cloudflare Worker in front of the API (edge/api): the zone's WAF runs first, then it forwards to the container with the shared
# secret. These checks pin what keeps the secret safe and the container reachable only through Cloudflare.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Js = Get-Content (Join-Path $script:Root 'edge' 'api' 'index.js') -Raw
    $script:Toml = Get-Content (Join-Path $script:Root 'edge' 'api' 'wrangler.toml') -Raw
}

Describe 'API edge Worker' {
    It 'builds the target from the fixed origin, setting only path and query (a path like //evil.example must not change the host)' {
        $script:Js | Should -Match 'const target = new URL\(env\.ORIGIN\);'
        $script:Js | Should -Match 'target\.pathname = url\.pathname;'
        $script:Js | Should -Match 'target\.search = url\.search;'
        $script:Js | Should -Not -Match 'new URL\(\s*url\.pathname'
        $script:Js | Should -Not -Match 'new URL\([^)]*,\s*env\.ORIGIN'
    }

    It 'replaces any client copy of the trusted headers and takes the visitor address from Cloudflare' {
        foreach ($name in 'x-origin-auth', 'x-client-ip', 'x-forwarded-host', 'x-forwarded-proto') {
            $script:Js | Should -Match "'$name'" -Because "$name must be deleted before it is set"
        }
        $script:Js | Should -Match "headers\.set\('x-origin-auth', env\.ORIGIN_AUTH\)"
        $script:Js | Should -Match "headers\.set\('x-client-ip', request\.headers\.get\('cf-connecting-ip'\)"
        $script:Js | Should -Match "redirect: 'manual'"
    }

    It 'answers a failed fetch with a 502, which the desktop app retries during a cold start' {
        $script:Js | Should -Match 'status: 502'
    }

    It 'serves only api.lifestream.himaiz.com: no workers.dev or preview address, and the secret is not in the file' {
        $script:Toml | Should -Match 'name = "lifestream-api-edge"'
        $script:Toml | Should -Match 'workers_dev = false'
        $script:Toml | Should -Match 'preview_urls = false'
        $script:Toml | Should -Match 'pattern = "api\.lifestream\.himaiz\.com", custom_domain = true'
        $script:Toml | Should -Match 'ORIGIN = "https://lifestream-api\.[a-z0-9-]+\.eastus\.azurecontainerapps\.io"'
        $script:Toml | Should -Not -Match '(?im)^\s*ORIGIN_AUTH\s*='
    }
}
