function Get-ConfigValue {
    <#
    .SYNOPSIS
        Returns a property value from a PSCustomObject if it is present and
        truthy, otherwise returns the default.

    .DESCRIPTION
        Replaces the recurring
        `if ($obj.X) { $obj.X } else { $default }` pattern across the
        Wrapp.Packager Public functions. The lookup is property-presence-aware
        (PSObject.Properties[name]) so it doesn't choke on objects deserialised
        from JSON where missing properties cause "property cannot be found"
        errors under StrictMode.

    .PARAMETER ConfigObject
        The PSCustomObject (or hashtable) to read from.

    .PARAMETER PropertyName
        The name of the property to look up.

    .PARAMETER DefaultValue
        The value to return when the property is absent, null, empty string,
        or otherwise falsy. May itself be null.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $ConfigObject,

        [Parameter(Mandatory)]
        [string]$PropertyName,

        $DefaultValue
    )

    if ($null -eq $ConfigObject) { return $DefaultValue }

    if ($ConfigObject -is [System.Collections.IDictionary]) {
        if ($ConfigObject.Contains($PropertyName) -and $ConfigObject[$PropertyName]) {
            return $ConfigObject[$PropertyName]
        }
        return $DefaultValue
    }

    if ($ConfigObject.PSObject.Properties[$PropertyName] -and $ConfigObject.$PropertyName) {
        return $ConfigObject.$PropertyName
    }
    return $DefaultValue
}
