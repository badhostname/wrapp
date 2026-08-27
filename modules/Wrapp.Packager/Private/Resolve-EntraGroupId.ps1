function Resolve-EntraGroupId {
    <#
    .SYNOPSIS
        Resolves a group name or GUID to a validated Entra ID group object ID.

    .DESCRIPTION
        If the input is already a GUID, it is returned as-is.
        If the input is a group display name, queries Microsoft Graph via the
        existing IntuneWin32App authentication header to find the matching group.
        Errors if no match or multiple matches are found.

    .PARAMETER GroupIdentifier
        A group display name (string) or object ID (GUID).

    .OUTPUTS
        [string] The resolved group object ID (GUID).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GroupIdentifier
    )

    $GuidPattern = '^[{(]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}[)}]?$'

    # If already a GUID, return as-is (strip optional braces/parens)
    if ($GroupIdentifier -match $GuidPattern) {
        $clean = $GroupIdentifier -replace '[{}()]', ''
        Write-Log "GroupID '$GroupIdentifier' is a GUID. Using as-is."
        return $clean
    }

    # Resolve group name via Microsoft Graph
    Write-Log "GroupID '$GroupIdentifier' is a display name. Resolving via Graph API..."

    if (-not $Global:AuthenticationHeader) {
        throw "Cannot resolve group name '$GroupIdentifier': no active authentication. Run Connect-MSIntuneGraph first."
    }

    $encodedName = [System.Uri]::EscapeDataString($GroupIdentifier)
    $uri = "https://graph.microsoft.com/v1.0/groups?`$filter=displayName eq '$encodedName'&`$select=id,displayName"

    try {
        $response = Invoke-RestMethod -Uri $uri -Headers $Global:AuthenticationHeader -Method Get -ErrorAction Stop
    }
    catch {
        throw "Failed to query Graph API for group '$GroupIdentifier': $_"
    }

    $groups = @($response.value)

    if ($groups.Count -eq 0) {
        throw "No Entra ID group found with display name '$GroupIdentifier'. Verify the name is correct and you have Group.Read.All permissions."
    }

    if ($groups.Count -gt 1) {
        $matches = ($groups | ForEach-Object { "'$($_.displayName)' ($($_.id))" }) -join ', '
        throw "Multiple groups found matching '$GroupIdentifier': $matches. Use the group object ID (GUID) instead to avoid ambiguity."
    }

    $resolvedId = $groups[0].id
    Write-Log "Resolved group '$GroupIdentifier' to object ID: $resolvedId"
    return $resolvedId
}
