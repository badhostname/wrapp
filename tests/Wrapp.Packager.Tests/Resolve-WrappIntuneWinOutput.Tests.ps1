# Pester 5. Run from the repo root:
#   Invoke-Pester -Path tests\Wrapp.Packager.Tests -Output Detailed
# Resolve-WrappIntuneWinOutput is private, so the tests run inside the module.

BeforeAll {
    $manifest = Join-Path $PSScriptRoot '..\..\modules\Wrapp.Packager\Wrapp.Packager.psd1'
    Import-Module $manifest -Force

    $script:Root = Join-Path $env:TEMP ('WrappPester_' + [guid]::NewGuid().ToString('N'))
    $script:Bundle = Join-Path $script:Root 'share\Ricoh\UPD_PCL6\4_45_0_0'
    New-Item -ItemType Directory -Path (Join-Path $script:Bundle 'Script') -Force | Out-Null

    # A drive letter nothing is mounted on: an unreachable root that answers at once.
    $script:MissingDrive = ([char[]](90..65) | ForEach-Object { "$($_):\" } | Where-Object { -not (Test-Path -LiteralPath $_) } | Select-Object -First 1)
}

AfterAll {
    Remove-Item -LiteralPath $script:Root -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Resolve-WrappIntuneWinOutput' {

    It 'joins a relative Output.Path to the bundle root, so beside-the-bundle travels with the bundle' {
        $config = [pscustomobject]@{ App = @{}; Script = [pscustomobject]@{ IntunePackager = [pscustomobject]@{ Output = [pscustomobject]@{ Path = '..\Output' } } } }
        $r = InModuleScope Wrapp.Packager -Parameters @{ c = $config; p = $script:Bundle } { Resolve-WrappIntuneWinOutput -Config $c -PackagePath $p }
        $r.Source | Should -Be 'Config'
        $r.Path   | Should -Be (Join-Path (Split-Path $script:Bundle -Parent) 'Output')
    }

    It 'uses an absolute Output.Path as it is when its root is reachable' {
        $central = Join-Path $script:Root 'central\Ricoh\UPD\4_45_0_0'
        $config = [pscustomobject]@{ App = @{}; Script = [pscustomobject]@{ IntunePackager = [pscustomobject]@{ Output = [pscustomobject]@{ Path = $central } } } }
        $r = InModuleScope Wrapp.Packager -Parameters @{ c = $config; p = $script:Bundle } { Resolve-WrappIntuneWinOutput -Config $c -PackagePath $p }
        $r.Source | Should -Be 'Config'
        $r.Path   | Should -Be $central
    }

    It 'falls back to Output beside the bundle when the root is not reachable, and says why' {
        $script:MissingDrive | Should -Not -BeNullOrEmpty -Because 'the test needs a drive letter nothing is mounted on'
        $unreachable = "$($script:MissingDrive)intunewin\Ricoh"   # not Join-Path: it validates the drive
        $config = [pscustomobject]@{ App = @{}; Script = [pscustomobject]@{ IntunePackager = [pscustomobject]@{ Output = [pscustomobject]@{ Path = $unreachable } } } }
        $r = InModuleScope Wrapp.Packager -Parameters @{ c = $config; p = $script:Bundle } { Resolve-WrappIntuneWinOutput -Config $c -PackagePath $p }
        $r.Source    | Should -Be 'Fallback'
        $r.Path      | Should -Be (Join-Path (Split-Path $script:Bundle -Parent) 'Output')
        $r.Requested | Should -Be $unreachable
        $r.Reason    | Should -BeLike '*not reachable*'
    }

    It 'uses the tenant IntuneWinPath with the old composition for a bundle without an Output block' {
        $config = [pscustomobject]@{
            App    = [pscustomobject]@{ Company = 'Ricoh'; Name = 'Universal Print Driver PCL6'; Version = '4_45_0_0' }
            Script = [pscustomobject]@{ IntunePackager = [pscustomobject]@{ Packages = @() } }
        }
        $tenant = [pscustomobject]@{ IntuneWinPath = (Join-Path $script:Root 'legacy') }
        $r = InModuleScope Wrapp.Packager -Parameters @{ c = $config; p = $script:Bundle; t = $tenant } { Resolve-WrappIntuneWinOutput -Config $c -PackagePath $p -TenantConfig $t }
        $r.Source | Should -Be 'Tenant'
        $r.Path   | Should -Be (Join-Path $script:Root 'legacy\Ricoh\Universal Print Driver PCL6\4_45_0_0')
    }

    It 'defaults to Output beside the bundle when nothing is configured' {
        $config = [pscustomobject]@{ App = @{}; Script = [pscustomobject]@{ IntunePackager = [pscustomobject]@{ Packages = @() } } }
        $r = InModuleScope Wrapp.Packager -Parameters @{ c = $config; p = $script:Bundle } { Resolve-WrappIntuneWinOutput -Config $c -PackagePath $p -TenantConfig ([pscustomobject]@{ IntuneWinPath = '' }) }
        $r.Source | Should -Be 'Default'
        $r.Path   | Should -Be (Join-Path (Split-Path $script:Bundle -Parent) 'Output')
    }

    It 'prefers the Output block over the tenant path' {
        $config = [pscustomobject]@{ App = @{}; Script = [pscustomobject]@{ IntunePackager = [pscustomobject]@{ Output = [pscustomobject]@{ Path = '..\Output' } } } }
        $tenant = [pscustomobject]@{ IntuneWinPath = (Join-Path $script:Root 'legacy') }
        $r = InModuleScope Wrapp.Packager -Parameters @{ c = $config; p = $script:Bundle; t = $tenant } { Resolve-WrappIntuneWinOutput -Config $c -PackagePath $p -TenantConfig $t }
        $r.Source | Should -Be 'Config'
    }
}
