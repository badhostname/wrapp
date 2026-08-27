<#
.SYNOPSIS
    Applies, exports, or clears Wrapp enterprise policy on this machine —
    the OFFLINE counterpart to Group Policy / Intune ADMX deployment.

.DESCRIPTION
    Wrapp reads policy from the registry only:

        HKLM\SOFTWARE\Policies\Wrapp                (machine mandatory — wins)
        HKCU\SOFTWARE\Policies\Wrapp                (user mandatory)
        ...\Wrapp\Recommended                       (defaults the user may change)

    Domain GPO, Intune ADMX ingestion, and this script all write the SAME
    keys, so disconnected fleets get identical enforcement: deploy this
    script + a policies.json via SCCM, an Intune Win32 app, a task sequence,
    or an image bake. A mandatory value is enforced and its control disabled
    ("Managed by your organization"); Recommended values seed defaults the
    user can still change. Restart Wrapp to apply.

    The policies.json shape (all sections optional):

        {
          "Mandatory": {
            "UpdateFeedUrl": "\\\\fileserver\\wrapp\\releases",
            "UpdateMode": "NotifyOnly",
            "IntunePackageDefaults.InstallExperience": "system"
          },
          "Recommended": { "Theme": "Dark" },
          "HiddenSections": [ "Inventory", "GitHistory" ],
          "HiddenSettingsTabs": [ "KeyVault" ]
        }

    Dotted keys become subkeys (IntunePackageDefaults\InstallExperience).
    Booleans/ints are written as REG_DWORD, strings as REG_SZ.

.PARAMETER PolicyFile
    Path to a policies.json to apply.

.PARAMETER Scope
    Machine (HKLM, default — requires elevation) or User (HKCU).

.PARAMETER Export
    Writes the currently applied policy for the scope to this path as JSON.

.PARAMETER Clear
    Removes every Wrapp policy value for the scope.

.EXAMPLE
    .\Apply-WrappPolicy.ps1 -PolicyFile .\policies.json
.EXAMPLE
    .\Apply-WrappPolicy.ps1 -Export .\effective.json -Scope Machine
.EXAMPLE
    .\Apply-WrappPolicy.ps1 -Clear -Scope User
#>
[CmdletBinding(DefaultParameterSetName = 'Apply')]
param(
    [Parameter(ParameterSetName = 'Apply', Mandatory)]
    [string]$PolicyFile,

    [Parameter(ParameterSetName = 'Export', Mandatory)]
    [string]$Export,

    [Parameter(ParameterSetName = 'Clear', Mandatory)]
    [switch]$Clear,

    [ValidateSet('Machine', 'User')]
    [string]$Scope = 'Machine'
)

$ErrorActionPreference = 'Stop'
$hive = if ($Scope -eq 'Machine') { 'HKLM:' } else { 'HKCU:' }
$root = "$hive\SOFTWARE\Policies\Wrapp"

function Assert-Elevation {
    if ($Scope -ne 'Machine') { return }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Machine-scope policy requires an elevated session (HKLM is admin-writable only — that ACL is the security model).'
    }
}

function Write-PolicyValue {
    param([string]$BasePath, [string]$Key, $Value)
    $subKey = $BasePath
    $name = $Key
    if ($Key.Contains('.')) {
        $parts = $Key.Split('.', 2)
        $subKey = Join-Path $BasePath $parts[0]
        $name = $parts[1]
    }
    if (-not (Test-Path $subKey)) { New-Item -Path $subKey -Force | Out-Null }
    if ($Value -is [bool]) {
        New-ItemProperty -Path $subKey -Name $name -Value ([int]$Value) -PropertyType DWord -Force | Out-Null
    }
    elseif ($Value -is [int] -or $Value -is [long]) {
        New-ItemProperty -Path $subKey -Name $name -Value ([int]$Value) -PropertyType DWord -Force | Out-Null
    }
    else {
        New-ItemProperty -Path $subKey -Name $name -Value ([string]$Value) -PropertyType String -Force | Out-Null
    }
    Write-Host "  set $subKey!$name"
}

switch ($PSCmdlet.ParameterSetName) {
    'Clear' {
        Assert-Elevation
        if (Test-Path $root) {
            Remove-Item -Path $root -Recurse -Force -Confirm:$false
            Write-Host "Cleared all Wrapp policy under $root"
        }
        else { Write-Host "No Wrapp policy present under $root" }
        return
    }

    'Export' {
        $result = [ordered]@{ Mandatory = [ordered]@{}; Recommended = [ordered]@{}
                              HiddenSections = @(); HiddenSettingsTabs = @() }
        if (Test-Path $root) {
            $rootKey = Get-Item $root
            foreach ($n in $rootKey.GetValueNames()) { $result.Mandatory[$n] = $rootKey.GetValue($n) }
            foreach ($sub in $rootKey.GetSubKeyNames()) {
                $subKey = Get-Item (Join-Path $root $sub)
                foreach ($n in $subKey.GetValueNames()) {
                    switch ($sub) {
                        'Recommended' { $result.Recommended[$n] = $subKey.GetValue($n) }
                        'HiddenSections' { $result.HiddenSections += $n }
                        'HiddenSettingsTabs' { $result.HiddenSettingsTabs += $n }
                        'HiddenSettings' { }
                        default { $result.Mandatory["$sub.$n"] = $subKey.GetValue($n) }
                    }
                }
            }
        }
        $json = $result | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($Export, $json, (New-Object Text.UTF8Encoding($false)))
        Write-Host "Exported $Scope policy to $Export"
        return
    }

    'Apply' {
        Assert-Elevation
        $raw = [IO.File]::ReadAllText((Resolve-Path $PolicyFile).Path)
        $policy = $raw | ConvertFrom-Json

        Write-Host "Applying Wrapp policy ($Scope) from $PolicyFile"
        if ($policy.Mandatory) {
            foreach ($p in $policy.Mandatory.PSObject.Properties) {
                Write-PolicyValue -BasePath $root -Key $p.Name -Value $p.Value
            }
        }
        if ($policy.Recommended) {
            foreach ($p in $policy.Recommended.PSObject.Properties) {
                Write-PolicyValue -BasePath (Join-Path $root 'Recommended') -Key $p.Name -Value $p.Value
            }
        }
        foreach ($listName in 'HiddenSections', 'HiddenSettingsTabs', 'HiddenSettings') {
            $items = $policy.$listName
            if ($items) {
                $listPath = Join-Path $root $listName
                if (-not (Test-Path $listPath)) { New-Item -Path $listPath -Force | Out-Null }
                foreach ($item in $items) {
                    New-ItemProperty -Path $listPath -Name $item -Value 1 -PropertyType DWord -Force | Out-Null
                    Write-Host "  hide $listName\$item"
                }
            }
        }

        # Simple name->value maps: Placeholders (custom {{tokens}}, always
        # non-sensitive) and RedactionPatterns (label -> regex, merged with
        # the org defaults file's patterns).
        foreach ($mapName in 'Placeholders', 'RedactionPatterns') {
            $map = $policy.$mapName
            if ($map) {
                $mapPath = Join-Path $root $mapName
                if (-not (Test-Path $mapPath)) { New-Item -Path $mapPath -Force | Out-Null }
                foreach ($p in $map.PSObject.Properties) {
                    New-ItemProperty -Path $mapPath -Name $p.Name -Value ([string]$p.Value) -PropertyType String -Force | Out-Null
                    Write-Host "  set $mapName\$($p.Name)"
                }
            }
        }

        # Keyed entry lists: one subkey per entry (subkey name = the entry's
        # Key), values inside. ClientSecret is REFUSED here and ignored by
        # the app - secrets are per-user DPAPI and never provisioned via a
        # world-readable hive. DeploymentGroups arrays become REG_MULTI_SZ.
        foreach ($listName in 'IntuneTenants', 'SccmSites', 'Domains') {
            $entries = $policy.$listName
            if (-not $entries) { continue }
            foreach ($entry in $entries.PSObject.Properties) {
                $entryPath = Join-Path (Join-Path $root $listName) $entry.Name
                if (-not (Test-Path $entryPath)) { New-Item -Path $entryPath -Force | Out-Null }
                foreach ($v in $entry.Value.PSObject.Properties) {
                    if ($v.Name -eq 'ClientSecret') {
                        Write-Warning "  $listName\$($entry.Name): ClientSecret cannot be provisioned via policy - skipped"
                        continue
                    }
                    if ($v.Value -is [System.Array]) {
                        New-ItemProperty -Path $entryPath -Name $v.Name -Value ([string[]]$v.Value) -PropertyType MultiString -Force | Out-Null
                    }
                    elseif ($v.Value -is [bool]) {
                        New-ItemProperty -Path $entryPath -Name $v.Name -Value ([int]$v.Value) -PropertyType DWord -Force | Out-Null
                    }
                    else {
                        New-ItemProperty -Path $entryPath -Name $v.Name -Value ([string]$v.Value) -PropertyType String -Force | Out-Null
                    }
                    Write-Host "  set $listName\$($entry.Name)!$($v.Name)"
                }
            }
        }

        Write-Host 'Done. Restart Wrapp to apply (policy is read once at launch).'
        return
    }
}
