function Expand-WrappTokens {
    <#
    .SYNOPSIS
        Expands the double-brace metadata template tokens ({{Company}},
        {{Name}}, ...) against the bundle's App section. Module-side mirror
        of the GUI's TemplateService.ApplyTokens so user-defaults metadata
        templates (e.g. OwnerTemplate = '{{Company}} IT') resolve identically
        in CLI runs.

    .PARAMETER Value
        The template string (may contain zero tokens; returned as-is).

    .PARAMETER App
        The Config.json App section object.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][AllowNull()][string]$Value,
        $App
    )

    if ([string]::IsNullOrEmpty($Value)) { return $Value }

    $result = $Value
    if ($App) {
        $result = $result.
            Replace('{{Company}}',    [string]$App.Company).
            Replace('{{Name}}',       [string]$App.Name).
            Replace('{{DotVersion}}', [string]$App.DotVersion).
            Replace('{{Version}}',    [string]$App.Version).
            Replace('{{Language}}',   [string]$App.Language).
            Replace('{{GUID}}',       [string]$App.GUID)
    }
    $result = $result.
        Replace('{{Date}}',   (Get-Date -Format 'yyyy-MM-dd')).
        Replace('{{Author}}', [string]$env:USERNAME)

    return $result
}
