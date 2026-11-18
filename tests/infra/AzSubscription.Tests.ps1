# Every az command in the runbook and in the scripts that call az directly passes --subscription explicitly, so nothing can run
# against whatever the CLI's default subscription happens to be. (The scripts that build argument arrays are covered by their own
# mocked-az tests, which assert --subscription on every recorded call.)

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Sub = '88f308cf-f1e5-4e4d-b0bc-3e8b14ef9c65'
    function script:AzLines([string]$relative) {
        $text = (Get-Content (Join-Path $script:Root $relative) -Raw) -replace '\\r?\n', ' ' -replace '`\r?\n\s*', ' '
        foreach ($m in [regex]::Matches($text, '(?<![\w-])az\s+(?!extension|bicep|login\b)[a-z][a-z-]*[^`\r\n]*')) { $m.Value }
    }
}

Describe 'az commands pass --subscription explicitly' {
    It '<File> has at least one az command and every one passes --subscription <Sub>' -ForEach @(
        @{ File = 'infra/README.md' }
        @{ File = 'infra/bootstrap-sql.ps1' }
    ) {
        $lines = @(AzLines $File)
        $lines.Count | Should -BeGreaterThan 0
        foreach ($l in $lines) { $l | Should -Match "--subscription $($script:Sub)|--subscription \`$SubscriptionId" -Because $l }
    }

    It 'the scripts that call az take a mandatory-format -SubscriptionId and pass it to every call' {
        foreach ($f in 'infra/sync-sql-firewall.ps1', 'infra/enable-entra-only.ps1') {
            $text = Get-Content (Join-Path $script:Root $f) -Raw
            $text | Should -Match '\[string\]\$SubscriptionId'
            $text | Should -Match '--subscription \$SubscriptionId'
            $calls = [regex]::Matches($text, '(?m)^.*& az @.*$')
            $calls.Count | Should -BeGreaterThan 0
            foreach ($c in $calls) { $c.Value | Should -Match '--subscription \$SubscriptionId' -Because $c.Value }
        }
    }
}
