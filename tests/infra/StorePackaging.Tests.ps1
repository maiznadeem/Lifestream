# Tests for packaging/store: the manifest template and build-msix.ps1. Uses a tiny fake publish folder (a placeholder
# Lifestream.exe file), and makeappx from the Windows SDK to build a real, unsigned MSIX.

BeforeAll {
    $script:Dir = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'packaging' 'store')).Path
    $script:Build = Join-Path $script:Dir 'build-msix.ps1'
    $hostPath = (Get-Process -Id $PID).Path
    if ((Split-Path $hostPath -Leaf) -like 'dotnet*') { $script:Pwsh = $hostPath; $script:PwshArgs = @((Join-Path $PSHOME 'pwsh.dll'), '-NoProfile', '-File') }
    else { $script:Pwsh = $hostPath; $script:PwshArgs = @('-NoProfile', '-File') }

    $script:Publish = Join-Path $TestDrive 'publish'
    New-Item -ItemType Directory -Path $script:Publish | Out-Null
    Set-Content (Join-Path $script:Publish 'Lifestream.exe') 'placeholder, not a real executable'

    function script:Invoke-Build([string[]]$Extra, [string]$out) {
        $text = & $script:Pwsh @script:PwshArgs $script:Build -PublishDir $script:Publish -Version 2.0.0 -OutputDir $out @Extra 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $text }
    }
    $script:Id = @('-IdentityName', 'Example.Lifestream', '-Publisher', 'CN=00000000-0000-0000-0000-000000000000', '-PublisherDisplayName', 'Example Publisher')
}

Describe 'packaging/store' {
    It 'has a manifest template that is well-formed XML, runs full trust, and holds only placeholders for identity' {
        $text = Get-Content (Join-Path $script:Dir 'AppxManifest.template.xml') -Raw
        { [xml]$text } | Should -Not -Throw
        $text | Should -Match 'runFullTrust'
        $text | Should -Match '\{\{IDENTITY_NAME\}\}'
        $text | Should -Match '\{\{PUBLISHER\}\}'
        $text | Should -Match 'Executable="Lifestream\.exe"'
    }

    It 'writes a valid manifest with version x.y.z.0 and the supplied identity, plus the three logo assets' {
        $out = Join-Path $TestDrive 'manifest-only'
        $r = Invoke-Build ($script:Id + '-ManifestOnly') $out
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $xml = [xml](Get-Content (Join-Path $out 'layout' 'AppxManifest.xml') -Raw)
        $xml.Package.Identity.Version | Should -Be '2.0.0.0'
        $xml.Package.Identity.Name | Should -Be 'Example.Lifestream'
        $xml.Package.Identity.Publisher | Should -Be 'CN=00000000-0000-0000-0000-000000000000'
        foreach ($a in 'StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png') { Test-Path (Join-Path $out 'layout' 'Assets' $a) | Should -BeTrue }
        (Get-Content (Join-Path $out 'layout' 'AppxManifest.xml') -Raw) | Should -Not -Match '\{\{'
    }

    It 'builds a real MSIX with makeappx that contains the manifest, block map and the app' {
        $out = Join-Path $TestDrive 'full'
        $r = Invoke-Build $script:Id $out
        $r.ExitCode | Should -Be 0 -Because $r.Output
        $msix = Join-Path $out 'Lifestream-2.0.0-x64.msix'
        Test-Path $msix | Should -BeTrue
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($msix)
        try { $names = $zip.Entries.FullName } finally { $zip.Dispose() }
        $names | Should -Contain 'AppxManifest.xml'
        $names | Should -Contain 'AppxBlockMap.xml'
        $names | Should -Contain 'Lifestream.exe'
    }

    It 'refuses to build without the Partner Center identity (no placeholder identity ever ships)' {
        $r = Invoke-Build @('-IdentityName', '', '-Publisher', '', '-PublisherDisplayName', '') (Join-Path $TestDrive 'noid')
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'IdentityName is empty'
        $r.Output | Should -Match 'STORE_IDENTITY_NAME'
    }
}
