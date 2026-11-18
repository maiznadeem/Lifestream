# Shared by prune-image-versions.ps1 and verify-image-versions.ps1 (dot-sourced). Talks to GitHub only through `gh`.

function Get-PackageVersion {
    <# Returns the package versions newest first as objects { Id; Created; Tags[] }. Throws when the query fails. #>
    param([Parameter(Mandatory)][string]$Owner, [Parameter(Mandatory)][string]$Package)
    $lines = & gh api --paginate "/users/$Owner/packages/container/$Package/versions" --jq '.[] | {id: .id, created: .created_at, tags: (.metadata.container.tags // [])}' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api (list versions of $Package) failed (exit $LASTEXITCODE): $($lines | Out-String)" }
    $versions = foreach ($line in @($lines)) {
        $text = "$line".Trim()
        if (-not $text) { continue }
        $o = $text | ConvertFrom-Json
        [pscustomobject]@{ Id = [int64]$o.id; Created = [datetime]$o.created; Tags = @($o.tags) }
    }
    @($versions | Sort-Object -Property @{ Expression = 'Created'; Descending = $true }, @{ Expression = 'Id'; Descending = $true })
}
