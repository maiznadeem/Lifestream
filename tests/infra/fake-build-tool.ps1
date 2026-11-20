# Dry-run stand-in for dotnet, docker and vpk, used by Workflows.Tests.ps1 to execute the exact command lines of the workflow steps
# without building anything:  fake-build-tool.ps1 <tool> <arguments as the workflow passed them>
# It records the call in $env:FAKE_TOOL_LOG and prints one line. It refuses what the real tool refuses and the workflows once got wrong:
# `dotnet build|publish|pack` take ONE project or solution (MSBuild: "MSB1008: Only one project can be specified", exit 1).
# `pwsh -File` turns `-p:Name=value` into the two arguments `-p` and `Name=value` (the colon is lost); the real dotnet gets one token.
$tokens = @()
for ($i = 0; $i -lt $args.Count; $i++) { $t = [string]$args[$i]; if ($t -in '-p', '-property' -and $i + 1 -lt $args.Count -and [string]$args[$i + 1] -match '^[\w.]+=') { $t += ':' + [string]$args[++$i] }; $tokens += $t }
$tool = $tokens[0]
$rest = @($tokens | Select-Object -Skip 1)
if ($env:FAKE_TOOL_LOG) { Add-Content -LiteralPath $env:FAKE_TOOL_LOG -Value ("$tool " + ($rest -join ' ')) }

if ($tool -eq 'dotnet' -and $rest.Count -gt 0 -and $rest[0] -in 'build', 'publish', 'pack') {
    $valueOptions = '-c', '--configuration', '-r', '--runtime', '-o', '--output', '-f', '--framework', '-a', '--arch', '-v', '--verbosity', '--os'
    $positional = @()
    for ($i = 1; $i -lt $rest.Count; $i++) {
        $t = [string]$rest[$i]
        if ($t -in $valueOptions) { $i++; continue }
        # `--self-contained` takes an optional true or false, as the Dockerfile and secret-scan.yml write it (`--self-contained false`);
        # the real dotnet reads that word as the option's value, not as a second project. Any other word after it is still positional.
        if ($t -in '--self-contained', '--sc' -and $i + 1 -lt $rest.Count -and [string]$rest[$i + 1] -in 'true', 'false') { $i++; continue }
        if ($t.StartsWith('-')) { continue }
        $positional += $t
    }
    if ($positional.Count -gt 1) {
        Write-Output 'MSBUILD : error MSB1008: Only one project can be specified.'
        Write-Output ('    Switch: ' + $positional[1])
        exit 1
    }
}
Write-Output ("fake $tool ok: " + ($rest -join ' '))
exit 0
