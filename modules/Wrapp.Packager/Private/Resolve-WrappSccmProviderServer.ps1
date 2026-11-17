function Resolve-WrappSccmProviderServer {
    <#
    .SYNOPSIS
        Finds the SMS Provider server the Configuration Manager console knows about.

    .DESCRIPTION
        The ConfigurationManager module creates a site drive on import only
        when the console has completed a connection to the site under the
        current Windows account. When it has not, a drive can still be built
        with New-PSDrive as long as the SMS Provider server is known. This
        reads it from the places the console records it, in order:

          1. The console's recent connections (HKCU ...\AdminUI\MRU\n,
             ServerName) - present once the account has opened the console,
             even if the connection never completed.
          2. The server the console installer was pointed at
             (HKLM ...\AdminUI\Connection, Server) - present for every
             account on the machine.

        Returns '.' (this machine) when neither is present, which is only
        right on the site server itself; callers surface that in their
        error message.

    .OUTPUTS
        [string] Server name, or '.' when unknown.
    #>
    [CmdletBinding()]
    param()

    $mruKeys = Get-ChildItem -Path 'HKCU:\Software\Microsoft\ConfigMgr10\AdminUI\MRU' -ErrorAction SilentlyContinue | Sort-Object Name
    foreach ($key in $mruKeys) {
        $name = (Get-ItemProperty -Path $key.PSPath -Name 'ServerName' -ErrorAction SilentlyContinue).ServerName
        if ($name) { return $name }
    }

    foreach ($path in 'HKLM:\SOFTWARE\Microsoft\ConfigMgr10\AdminUI\Connection', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\ConfigMgr10\AdminUI\Connection') {
        $name = (Get-ItemProperty -Path $path -Name 'Server' -ErrorAction SilentlyContinue).Server
        if ($name) { return $name }
    }

    return '.'
}
