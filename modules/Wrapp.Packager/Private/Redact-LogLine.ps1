# Phase 11 hardening (S-5): token redaction for the PowerShell side.
# Mirrors Services/Infrastructure/AppLogger.cs's Redact() pipeline so
# anything written through Write-Log gets the same scrub as the C# logger
# applies before persisting LogEntry records to the GUI's app.log.
#
# Patterns must remain in sync with AppLogger.cs. If either side adds a
# pattern, the other must follow -- the PowerShell side runs inside the
# packaging runspace where Graph/MSAL tokens, $script:AuthenticationHeader,
# and Azure DevOps PATs all transit, so an unscrubbed Write-Log here would
# leak into both the per-package CMTrace log and the domain log copy.
$script:RedactPatterns = @(
    @{ Pattern = 'eyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]+';  Replace = 'eyJ***REDACTED***' }
    @{ Pattern = '(?i)\bBearer\s+[A-Za-z0-9_\-\.=+/]+';                              Replace = 'Bearer ***REDACTED***' }
    @{ Pattern = '(?i)\bBasic\s+[A-Za-z0-9+/=_\-]+';                                 Replace = 'Basic ***REDACTED***' }
    @{ Pattern = '(?i)client[_\-]?secret"?\s*[=:]\s*"?[^"\s,}\]]+';                  Replace = 'client_secret=***REDACTED***' }
    @{ Pattern = '(?i)\b(access_token|refresh_token|id_token)\s*=\s*[^&\s"]+';       Replace = '$1=***REDACTED***' }
    @{ Pattern = "(?i)\bpat\s*=\s*[^&\s`"']+";                                       Replace = 'pat=***REDACTED***' }
)

function Redact-LogLine {
    <#
    .SYNOPSIS
        Scrubs tokens / credentials from a log message before it is written.

    .DESCRIPTION
        Phase 11 hardening (S-5). Applies the AppLogger-mirrored regex set to
        any Write-Log payload. Designed to be cheap (compiled implicitly by
        the regex engine on first use) and idempotent -- running the scrub
        twice yields the same output.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [AllowEmptyString()]
        [string]$Text
    )
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    foreach ($p in $script:RedactPatterns) {
        $Text = $Text -replace $p.Pattern, $p.Replace
    }
    return $Text
}
