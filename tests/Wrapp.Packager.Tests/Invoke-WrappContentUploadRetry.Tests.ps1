# Pester 5. Run from the repo root:
#   Invoke-Pester -Path tests\Wrapp.Packager.Tests -Output Detailed
# The IntuneWin32App cmdlets are replaced by mocks; nothing reaches Graph.

BeforeAll {
    $manifest = Join-Path $PSScriptRoot '..\..\modules\Wrapp.Packager\Wrapp.Packager.psd1'
    Import-Module $manifest -Force

    # Stand-ins so the vendor cmdlets resolve for Mock when IntuneWin32App is not loaded.
    if (-not (Get-Command Get-IntuneWin32App -ErrorAction SilentlyContinue)) {
        function global:Get-IntuneWin32App { param($ID) }
        $script:DefinedGet = $true
    }
    if (-not (Get-Command Update-IntuneWin32AppPackageFile -ErrorAction SilentlyContinue)) {
        function global:Update-IntuneWin32AppPackageFile { param($ID, $FilePath) }
        $script:DefinedUpdate = $true
    }

    InModuleScope Wrapp.Packager {
        $script:SavedRetryCount = $script:ModuleDefaults.ContentUploadRetryCount
        $script:SavedRetryDelay = $script:ModuleDefaults.ContentUploadRetryDelaySeconds
        $script:ModuleDefaults.ContentUploadRetryCount = 2
        $script:ModuleDefaults.ContentUploadRetryDelaySeconds = 0
    }

    $script:Out = Join-Path $env:TEMP ('WrappPester_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Out | Out-Null
    $script:IntuneWin = Join-Path $script:Out 'Snagit_26_4_0.intunewin'
    Set-Content -LiteralPath $script:IntuneWin -Value 'x'
}

AfterAll {
    InModuleScope Wrapp.Packager {
        $script:ModuleDefaults.ContentUploadRetryCount = $script:SavedRetryCount
        $script:ModuleDefaults.ContentUploadRetryDelaySeconds = $script:SavedRetryDelay
    }
    if ($script:DefinedGet) { Remove-Item function:global:Get-IntuneWin32App -ErrorAction SilentlyContinue }
    if ($script:DefinedUpdate) { Remove-Item function:global:Update-IntuneWin32AppPackageFile -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $script:Out -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Invoke-WrappContentUploadRetry' {

    BeforeEach {
        $global:WrappTestCommitted = $false
        $global:WrappTestUploads = 0
        $global:WrappTestName = 'Snagit'
        $global:WrappTestExists = $true
        Mock -ModuleName Wrapp.Packager Invoke-TokenRefreshIfNeeded { }
        Mock -ModuleName Wrapp.Packager Get-IntuneWin32App {
            if (-not $global:WrappTestExists) { return $null }
            [pscustomobject]@{
                id                      = '9c3c48c0-a32b-4cb2-8636-46d6f38e5fc6'
                displayName             = $global:WrappTestName
                committedContentVersion = if ($global:WrappTestCommitted) { '2' } else { $null }
            }
        }
    }

    BeforeAll {
        # Pester 5: helpers must be defined in a run-phase block.
        function Invoke-Retry {
            $state = @{}
            $null = InModuleScope Wrapp.Packager -Parameters @{ p = $script:IntuneWin; s = $state } {
                Invoke-WrappContentUploadRetry -AppId '9c3c48c0-a32b-4cb2-8636-46d6f38e5fc6' -AppName 'Snagit' -IntuneWinPath $p -TenantID 't' -ClientID 'c' -State $s 6>$null 3>$null
            }
            $state
        }
    }

    AfterAll {
        Remove-Variable -Scope Global -Name WrappTestCommitted, WrappTestUploads, WrappTestName, WrappTestExists -ErrorAction SilentlyContinue
    }

    It 'retries the upload onto the same app until its content is committed' {
        Mock -ModuleName Wrapp.Packager Update-IntuneWin32AppPackageFile {
            $global:WrappTestUploads++
            if ($global:WrappTestUploads -eq 1) { Write-Warning "Intune service request for operation 'AzureStorageUriRequest' failed" }
            else { $global:WrappTestCommitted = $true }
        }
        $state = Invoke-Retry

        $state.Result.committedContentVersion | Should -Be '2'
        $state.Attempts | Should -Be 3        # the first upload, then two retries
        $global:WrappTestUploads | Should -Be 2
        $state.Problems | Should -HaveCount 1
        $state.Problems[0] | Should -BeLike "attempt 2:*AzureStorageUriRequest*"
        Should -Invoke -ModuleName Wrapp.Packager Update-IntuneWin32AppPackageFile -Times 2 -Exactly -ParameterFilter { $ID -eq '9c3c48c0-a32b-4cb2-8636-46d6f38e5fc6' }
    }

    It 'treats an app whose content landed after all as done, without uploading again' {
        $global:WrappTestCommitted = $true
        Mock -ModuleName Wrapp.Packager Update-IntuneWin32AppPackageFile { $global:WrappTestUploads++ }
        $state = Invoke-Retry
        $state.Result | Should -Not -BeNullOrEmpty
        $global:WrappTestUploads | Should -Be 0
    }

    It 'never touches an app that is no longer named as this run created it' {
        $global:WrappTestName = 'Something else'
        Mock -ModuleName Wrapp.Packager Update-IntuneWin32AppPackageFile { $global:WrappTestUploads++ }
        $state = Invoke-Retry
        $state.Result | Should -BeNullOrEmpty
        $state.Stopped | Should -BeLike "*now named 'Something else'*"
        $global:WrappTestUploads | Should -Be 0
    }

    It 'stops when the app cannot be read back' {
        $global:WrappTestExists = $false
        Mock -ModuleName Wrapp.Packager Update-IntuneWin32AppPackageFile { $global:WrappTestUploads++ }
        $state = Invoke-Retry
        $state.Stopped | Should -BeLike '*could not be read back*'
        $global:WrappTestUploads | Should -Be 0
    }

    It 'gives up after the configured attempts and keeps every reason' {
        Mock -ModuleName Wrapp.Packager Update-IntuneWin32AppPackageFile {
            $global:WrappTestUploads++
            Write-Warning "upload $global:WrappTestUploads refused"
        }
        $state = Invoke-Retry
        $state.Result | Should -BeNullOrEmpty
        $state.Attempts | Should -Be 3
        $state.Problems | Should -HaveCount 2
        $state.Problems[1] | Should -BeLike 'attempt 3:*upload 2 refused*'
    }
}

Describe 'Write-WrappFollowUp' {

    It 'emits a tagged notice and adds it to the run result' {
        $result = [pscustomobject]@{ FollowUps = [System.Collections.Generic.List[object]]::new() }
        $out = InModuleScope Wrapp.Packager -Parameters @{ r = $result } {
            $params = @{
                Result   = $r
                Target   = 'Intune'
                TargetId = 'd35fe7ad-abdf-4422-8ef9-8234b4c7a904'
                Package  = 'Snagit'
                Step     = 'AppCreation'
                Summary  = 'The upload to Intune failed after 3 attempt(s); the app exists in Intune without content.'
                Reason   = "Intune service request for operation 'AzureStorageUriRequest' failed"
                Actions  = @('Delete the incomplete app', 'Delete the extraction folder')
            }
            Write-WrappFollowUp @params 6>$null 3>$null
        }
        $out._Type | Should -Be 'WrappFollowUp'
        $out.Step | Should -Be 'AppCreation'
        @($out.Actions) | Should -HaveCount 2
        $result.FollowUps | Should -HaveCount 1
    }
}
