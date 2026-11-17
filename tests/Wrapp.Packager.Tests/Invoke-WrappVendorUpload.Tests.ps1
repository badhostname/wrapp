# Pester 5. Run from the repo root:
#   Invoke-Pester -Path tests\Wrapp.Packager.Tests -Output Detailed
# Private helpers run inside the module.

BeforeAll {
    $manifest = Join-Path $PSScriptRoot '..\..\modules\Wrapp.Packager\Wrapp.Packager.psd1'
    Import-Module $manifest -Force
}

Describe 'Invoke-WrappVendorUpload' {

    BeforeEach {
        $script:Out = Join-Path $env:TEMP ('WrappPester_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:Out | Out-Null
        $script:IntuneWin = Join-Path $script:Out 'Snagit_26_4_0.intunewin'
        Set-Content -LiteralPath $script:IntuneWin -Value 'x'
        # Left by an earlier run: must survive.
        New-Item -ItemType Directory -Path (Join-Path $script:Out 'Expand_aaaaaaaaaaaa') | Out-Null
        # Not the vendor's pattern: must survive.
        New-Item -ItemType Directory -Path (Join-Path $script:Out 'Expand_notours') | Out-Null
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Out -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'keeps the vendor''s warnings and errors as the reason, and removes only the Expand_ folder this call left' {
        $out = $script:Out
        # Like Add-IntuneWin32App when Intune gives no storage location: it
        # has unpacked the upload, warns, writes an error, returns nothing.
        $call = {
            New-Item -ItemType Directory -Path (Join-Path $out 'Expand_df558fbb34b6') | Out-Null
            Set-Content -LiteralPath (Join-Path $out 'Expand_df558fbb34b6\IntunePackage.intunewin') -Value 'payload'
            Write-Verbose 'Using native method for file transfer'
            Write-Warning "Intune service request for operation 'AzureStorageUriRequest' failed"
            Write-Error 'Cannot validate argument on parameter ''StorageUri''.'
        }.GetNewClosure()

        $state = @{}
        InModuleScope Wrapp.Packager -Parameters @{ c = $call; p = $script:IntuneWin; s = $state } {
            Invoke-WrappVendorUpload -Call $c -IntuneWinPath $p -State $s -AppName 'Snagit' 6>$null
        }

        $state.Result | Should -BeNullOrEmpty
        $state.Problems.Count | Should -Be 2
        $state.Problems[0] | Should -BeLike "*AzureStorageUriRequest' failed*"
        $state.Problems[1] | Should -BeLike '*StorageUri*'
        $state.RemovedFolders | Should -HaveCount 1
        Test-Path (Join-Path $script:Out 'Expand_df558fbb34b6') | Should -BeFalse
        Test-Path (Join-Path $script:Out 'Expand_aaaaaaaaaaaa') | Should -BeTrue
        Test-Path (Join-Path $script:Out 'Expand_notours') | Should -BeTrue
        Test-Path $script:IntuneWin | Should -BeTrue
    }

    It 'returns the app on success, with no problems and nothing to clean' {
        $call = { Write-Verbose 'Successfully created Win32 app'; [pscustomobject]@{ Id = '9c3c48c0-a32b-4cb2-8636-46d6f38e5fc6' } }
        $state = @{}
        InModuleScope Wrapp.Packager -Parameters @{ c = $call; p = $script:IntuneWin; s = $state } {
            Invoke-WrappVendorUpload -Call $c -IntuneWinPath $p -State $s 6>$null
        }
        $state.Result.Id | Should -Be '9c3c48c0-a32b-4cb2-8636-46d6f38e5fc6'
        $state.Problems | Should -BeNullOrEmpty
        $state.RemovedFolders | Should -BeNullOrEmpty
    }

    It 'records a terminating failure and still cleans up' {
        $out = $script:Out
        $call = {
            New-Item -ItemType Directory -Path (Join-Path $out 'Expand_0123456789ab') | Out-Null
            throw 'The remote server returned an error: (403) Forbidden.'
        }.GetNewClosure()
        $state = @{}
        InModuleScope Wrapp.Packager -Parameters @{ c = $call; p = $script:IntuneWin; s = $state } {
            Invoke-WrappVendorUpload -Call $c -IntuneWinPath $p -State $s 6>$null
        }
        $state.Problems | Should -HaveCount 1
        $state.Problems[0] | Should -BeLike '*403*'
        Test-Path (Join-Path $script:Out 'Expand_0123456789ab') | Should -BeFalse
    }
}

Describe 'Test-Win32AppCollisions' {

    It 'refuses to run without the three Graph globals, instead of reporting no collisions' {
        $saved = @{
            AccessToken          = $Global:AccessToken
            AuthenticationHeader = $Global:AuthenticationHeader
            AccessTokenTenantID  = $Global:AccessTokenTenantID
        }
        try {
            # The pre-check's old setup: header and tenant, no AccessToken.
            $Global:AuthenticationHeader = @{ Authorization = 'Bearer x' }
            $Global:AccessTokenTenantID = '11111111-1111-1111-1111-111111111111'
            Remove-Variable -Scope Global -Name AccessToken -ErrorAction SilentlyContinue

            { Test-Win32AppCollisions -PackageList @([pscustomobject]@{ AppName = 'Snagit' }) 6>$null } |
                Should -Throw '*not authenticated*'
        }
        finally {
            foreach ($k in $saved.Keys) {
                if ($null -eq $saved[$k]) { Remove-Variable -Scope Global -Name $k -ErrorAction SilentlyContinue }
                else { Set-Variable -Scope Global -Name $k -Value $saved[$k] }
            }
        }
    }
}
