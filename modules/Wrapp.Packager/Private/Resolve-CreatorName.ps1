function Resolve-CreatorName {
    <#
    .SYNOPSIS
        Resolves the current user's display name from Active Directory.
        Falls back to $env:USERNAME if AD lookup fails (non-domain machines,
        workgroup, Entra ID-only joined devices).

    .OUTPUTS
        [string] Display name or username.
    #>
    [CmdletBinding()]
    param()

    $Creator = $env:USERNAME

    try {
        $objDomain = New-Object System.DirectoryServices.DirectoryEntry
        $objSearcher = New-Object System.DirectoryServices.DirectorySearcher
        $objSearcher.SearchRoot = $objDomain
        $objSearcher.Filter = "(&(objectCategory=User)(SAMAccountName=$Creator))"
        $Result = $objSearcher.FindOne()
        if ($Result -and $Result.Properties.displayname) {
            $Creator = [string]$Result.Properties.displayname
            Write-Log "Resolved creator display name from AD: $Creator"
        }
    }
    catch {
        Write-Log "AD display name lookup failed, using USERNAME '$Creator'" -Type 2
    }

    return $Creator
}
