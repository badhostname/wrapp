function Set-CMContentDistribution {
    <#
    .SYNOPSIS
        Distributes SCCM application content to Distribution Point Groups.

    .DESCRIPTION
        Wraps Start-CMContentDistribution to distribute the specified
        application's content to one or more Distribution Point Groups.

    .PARAMETER AppName
        The SCCM application name to distribute.

    .PARAMETER DistributionPointGroups
        Array of Distribution Point Group names to distribute to.

    .PARAMETER Validate
        If specified, logs actions without making changes.

    .OUTPUTS
        [PSCustomObject] with Success and Errors properties.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppName,

        [Parameter(Mandatory = $true)]
        [string[]]$DistributionPointGroups,

        [switch]$Validate
    )

    $DistErrors = [System.Collections.Generic.List[string]]::new()

    Write-Log "Distributing '$AppName' content to DP groups: $($DistributionPointGroups -join ', ')"

    if ($Validate) {
        Write-Log "[VALIDATE] Would distribute '$AppName' to: $($DistributionPointGroups -join ', ')"
        return [PSCustomObject]@{
            Success = $true
            Errors  = @()
        }
    }

    try {
        $null = Start-CMContentDistribution -ApplicationName $AppName -DistributionPointGroupName $DistributionPointGroups
        Write-Log "Content distribution started for '$AppName'."

        return [PSCustomObject]@{
            Success = $true
            Errors  = @()
        }
    }
    catch {
        Write-Log "Content distribution failed for '$AppName': $_" -Type 3
        $DistErrors.Add("Content distribution for '$AppName': $_")

        return [PSCustomObject]@{
            Success = $false
            Errors  = $DistErrors.ToArray()
        }
    }
}
