function Write-Log {
    <#
    .SYNOPSIS
        Writes a CMTrace-compatible log entry to file and console.

    .DESCRIPTION
        Produces log lines in the SCCM CMTrace XML format:
        <![LOG[Message]LOG]!><time="HH:mm:ss.fff+000" date="MM-dd-yyyy" component="CallerName" context="" type="1" thread="PID" file="">

        Type mapping:
          1 = Info    -> Write-Host with timestamp
          2 = Warning -> Write-Warning
          3 = Error   -> Write-Host -ForegroundColor Red

    .PARAMETER Message
        The log message text.

    .PARAMETER Type
        Log severity: 1 (Info), 2 (Warning), 3 (Error). Default: 1.

    .PARAMETER Component
        Component name for the CMTrace 'component' field.
        Defaults to the calling function name via the call stack.

    .PARAMETER NoConsole
        Suppress console output (file-only logging).

    .EXAMPLE
        Write-Log "Starting package creation"
        Write-Log "Config field missing" -Type 2
        Write-Log "Fatal auth failure" -Type 3
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipeline = $true)]
        [AllowEmptyString()]
        [string]$Message,

        [Parameter(Position = 1)]
        [ValidateSet(1, 2, 3)]
        [int]$Type = 1,

        [string]$Component,

        [switch]$NoConsole
    )

    process {
        # Resolve component from call stack if not specified
        if (-not $Component) {
            $Caller = (Get-PSCallStack)[1]
            $Component = if ($Caller.FunctionName -and $Caller.FunctionName -ne '<ScriptBlock>') {
                $Caller.FunctionName
            }
            else {
                'Main'
            }
        }

        # Phase 11 hardening (S-5): scrub tokens / credentials before
        # formatting. Mirrors AppLogger.Redact on the C# side so the same
        # protection applies to the CMTrace log and any domain-share copy.
        $Message = Redact-LogLine $Message

        # Build CMTrace-format timestamp
        $Now = Get-Date
        $TimeStr = '{0}.{1:D3}+000' -f $Now.ToString('HH:mm:ss'), $Now.Millisecond
        $DateStr = $Now.ToString('MM-dd-yyyy')

        # CMTrace XML line
        $LogLine = '<![LOG[{0}]LOG]!><time="{1}" date="{2}" component="{3}" context="" type="{4}" thread="{5}" file="">' -f $Message, $TimeStr, $DateStr, $Component, $Type, $PID

        # Write to file (if log file initialized)
        if ($script:LogFile) {
            try {
                $LogDir = Split-Path -Path $script:LogFile -Parent
                if (-not (Test-Path -Path $LogDir)) {
                    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
                }
                Out-File -FilePath $script:LogFile -Append -Encoding ASCII -InputObject $LogLine -ErrorAction Stop
            }
            catch {
                # Silently fail file writes to avoid recursion
            }
        }

        # Console output (unless suppressed)
        if (-not $NoConsole) {
            $Timestamp = $Now.ToString('yyyy-MM-dd HH:mm:ss')
            switch ($Type) {
                1 { Write-Host "[$Timestamp] [INFO]  $Message" }
                2 { Write-Warning "[$Timestamp] $Message" }
                3 { Write-Host "[$Timestamp] [ERROR] $Message" -ForegroundColor Red }
            }
        }
    }
}
