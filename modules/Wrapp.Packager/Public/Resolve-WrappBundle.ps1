function Resolve-WrappBundle {
    <#
    .SYNOPSIS
        Finds a Wrapp bundle from any path inside it.

    .DESCRIPTION
        A bundle root is the folder that contains Script\Config.json (or, for
        bundles made before the Script layout, Config.json directly). Every
        other part of the bundle sits at a fixed place under that root:

            <root>\Script\        Config.json and the scripts
            <root>\B\             binaries (Appease)   | <root>\Files\ (PSADT)
            <root>\Shortcuts\     .lnk shortcuts
            <root>\Icon\          the icon (or wherever App.IconFile points)

        No file inside the bundle and no setting outside it states where the
        root is, so the only way to find it is to look: at the given path,
        then in each parent in turn, stopping at the share or drive root. The
        folder names are never compared with the app's Company, Name or
        Version; those come from Config.json.

        Used by Invoke-WrappPackaging, Invoke-WrappIntune and Invoke-WrappSccm,
        whose -ConfigPath therefore accepts the root, the Config.json or any
        path inside the bundle. Your own scripts can call it the same way.

    .PARAMETER Path
        The bundle root, its Script\Config.json, or any file or folder inside
        the bundle.

    .PARAMETER MaxDepth
        How many parents to try above the starting folder. Default 8.

    .OUTPUTS
        [PSCustomObject] with:
            Root         the bundle folder
            ConfigPath   the Config.json
            ScriptDir    where the scripts are (Root\Script, or Root for the legacy layout)
            BinaryDir    Root\B or Root\Files
            ShortcutDir  Root\Shortcuts
            IconDir      the folder App.IconFile points into, else Root\Icon
            Layout       'Script' or 'Legacy'
            Framework    'Appease' or 'PSADT'
            App          Company, Name, Version, DotVersion from Config.json

    .EXAMPLE
        Resolve-WrappBundle -Path '\\dist\packages$\Ricoh\UPD_PCL6\4_45_0_0'
        Resolve-WrappBundle -Path '\\dist\packages$\Ricoh\UPD_PCL6\4_45_0_0\Script\Config.json'
        Resolve-WrappBundle -Path '\\dist\packages$\Ricoh\UPD_PCL6\4_45_0_0\B\drivers\x64\oemsetup.inf'
        All three return the same bundle.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipeline = $true)]
        [string]$Path,

        [ValidateRange(0, 32)]
        [int]$MaxDepth = 8
    )

    process {
        if ([string]::IsNullOrWhiteSpace($Path)) {
            throw "Resolve-WrappBundle: -Path is empty."
        }

        $full = [System.IO.Path]::GetFullPath($Path.Trim())
        if (Test-Path -LiteralPath $full -PathType Container) {
            $start = $full
        }
        elseif (Test-Path -LiteralPath $full -PathType Leaf) {
            $start = [System.IO.Path]::GetDirectoryName($full)
        }
        else {
            throw "Resolve-WrappBundle: '$Path' does not exist."
        }

        # The marker at one folder: Script\Config.json (root = the folder),
        # Config.json inside a folder named Script (root = its parent), or a
        # legacy Config.json at the root.
        function Test-BundleMarker([string]$dir) {
            $leaf = [System.IO.Path]::GetFileName($dir)
            if ($leaf -ieq 'Script') {
                $inScript = Join-Path -Path $dir -ChildPath 'Config.json'
                if (Test-Path -LiteralPath $inScript -PathType Leaf) {
                    return @{ Root = [System.IO.Path]::GetDirectoryName($dir); ConfigPath = $inScript; Layout = 'Script' }
                }
            }
            $scriptLayout = Join-Path -Path (Join-Path -Path $dir -ChildPath 'Script') -ChildPath 'Config.json'
            if (Test-Path -LiteralPath $scriptLayout -PathType Leaf) {
                return @{ Root = $dir; ConfigPath = $scriptLayout; Layout = 'Script' }
            }
            $legacy = Join-Path -Path $dir -ChildPath 'Config.json'
            if (Test-Path -LiteralPath $legacy -PathType Leaf) {
                return @{ Root = $dir; ConfigPath = $legacy; Layout = 'Legacy' }
            }
            return $null
        }

        $found = $null
        $dir = $start
        $level = 0
        while ($dir -and $level -le $MaxDepth) {
            $found = Test-BundleMarker $dir
            if ($found) { break }
            $dir = [System.IO.Path]::GetDirectoryName($dir)   # $null at the drive or share root
            $level++
        }

        if (-not $found) {
            throw "Resolve-WrappBundle: '$Path' is not inside a Wrapp bundle (no Script\Config.json in that folder or the $MaxDepth folders above it)."
        }

        # A bundle inside a bundle: the nearest one is the answer, but say so.
        $above = [System.IO.Path]::GetDirectoryName($found.Root)
        $remaining = $MaxDepth - $level
        while ($above -and $remaining -gt 0) {
            if (Test-BundleMarker $above) {
                Write-Warning "Resolve-WrappBundle: '$($found.Root)' sits inside another bundle at '$above'; using the nearest one."
                break
            }
            $above = [System.IO.Path]::GetDirectoryName($above)
            $remaining--
        }

        try {
            $config = Get-Content -LiteralPath $found.ConfigPath -Raw | ConvertFrom-Json
        }
        catch {
            throw "Resolve-WrappBundle: '$($found.ConfigPath)' is not readable as Config.json: $($_.Exception.Message)"
        }
        $app = $config.App

        $framework = if ("$($app.ScriptFramework)" -ieq 'PSADT') { 'PSADT' } else { 'Appease' }
        $binaryName = if ($framework -eq 'PSADT') { 'Files' } else { 'B' }
        $binaryDir = Join-Path -Path $found.Root -ChildPath $binaryName
        if (-not (Test-Path -LiteralPath $binaryDir -PathType Container)) {
            # A bundle whose framework changed keeps the folder it has.
            $other = Join-Path -Path $found.Root -ChildPath $(if ($binaryName -eq 'B') { 'Files' } else { 'B' })
            if (Test-Path -LiteralPath $other -PathType Container) { $binaryDir = $other }
        }

        $iconDir = Join-Path -Path $found.Root -ChildPath 'Icon'
        $iconFile = "$($app.IconFile)"
        if ($iconFile -and -not [System.IO.Path]::IsPathRooted($iconFile) -and $iconFile -notmatch '(^|[\\/])\.\.([\\/]|$)') {
            $iconParent = [System.IO.Path]::GetDirectoryName($iconFile)
            if ($iconParent) { $iconDir = Join-Path -Path $found.Root -ChildPath $iconParent }
        }

        [pscustomobject]@{
            Root        = $found.Root
            ConfigPath  = $found.ConfigPath
            ScriptDir   = [System.IO.Path]::GetDirectoryName($found.ConfigPath)
            BinaryDir   = $binaryDir
            ShortcutDir = Join-Path -Path $found.Root -ChildPath 'Shortcuts'
            IconDir     = $iconDir
            Layout      = $found.Layout
            Framework   = $framework
            App         = [pscustomobject]@{
                Company    = "$($app.Company)"
                Name       = "$($app.Name)"
                Version    = "$($app.Version)"
                DotVersion = "$($app.DotVersion)"
            }
        }
    }
}
