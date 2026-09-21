function Convert-VendorVerboseToStep {
    <#
    .SYNOPSIS
        Translates a vendored IntuneWin32App verbose message into a structured
        upload sub-step (status text + percent) for Write-WrappStep, or $null
        when the message is not a recognized sub-step marker.

    .DESCRIPTION
        The vendored IntuneWin32App module (pinned 1.5.0) reports its upload
        pipeline only as Write-Verbose prose. This function owns the
        prose-to-step coupling so it lives in the SAME repo tree as the pinned
        vendored version it matches (modules/), and is updated together with
        any vendored bump -- instead of hiding in .NET regexes (PhaseDetector)
        that a module update silently breaks.

        Percent values map the upload pipeline onto the AppCreation step's
        sub-progress (0-100). The chunked-upload percent itself (25-80 band)
        still flows via the PowerShell progress stream, which is structured
        (PercentComplete) and needs no translation.

    .PARAMETER Message
        The verbose message text from Add-IntuneWin32App / its helpers.

    .OUTPUTS
        [hashtable] @{ Status = <UI text>; Percent = <0-100>; Detail = <extra> }
        or $null when the message is not a recognized sub-step.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Message
    )

    switch -Regex ($Message) {
        'Attempting to gather additional meta data from \.intunewin' {
            return @{ Status = 'Reading .intunewin metadata...'; Percent = 2 }
        }
        'Start constructing basic layout' {
            return @{ Status = 'Building app body...'; Percent = 5 }
        }
        'Attempting to create Win32 app using constructed body' {
            return @{ Status = 'Registering app in Intune...'; Percent = 8 }
        }
        'Successfully created Win32 app with ID: (.+)' {
            return @{ Status = 'App registered'; Percent = 12; Detail = $Matches[1].Trim() }
        }
        'Successfully created contentVersions resource' {
            return @{ Status = 'Content version created'; Percent = 15 }
        }
        'Constructing Win32 app content file body for uploading' {
            return @{ Status = 'Preparing file entry...'; Percent = 18 }
        }
        'Waiting for Intune service to process contentVersions/files request' {
            return @{ Status = 'Waiting for storage URI...'; Percent = 22 }
        }
        '(?:Using native method|falling back to (?:using )?native method|Content size is less than|Using AzCopy\.exe method)' {
            return @{ Status = 'Uploading 0%'; Percent = 25 }
        }
        'Waiting for Intune service to process the commit file request' {
            return @{ Status = 'Committing to Intune...'; Percent = 82 }
        }
        "operation 'CommitFile' is in pending state \(attempt (\d+)\)" {
            $attempt = [int]$Matches[1]
            return @{
                Status  = "Processing commit (attempt $attempt)..."
                Percent = [Math]::Min(95, 82 + $attempt)
            }
        }
        'Updating committedContentVersion property' {
            return @{ Status = 'Finalizing app version...'; Percent = 97 }
        }
        'Successfully created Win32 app and committed file content' {
            return @{ Status = 'App creation complete'; Percent = 100 }
        }
    }

    return $null
}
