<#
.SYNOPSIS
    Removes the Monaco language support Wrapp never uses from the vendored tree.

.DESCRIPTION
    Monaco ships ~100 language definitions and four heavyweight language
    services (TypeScript, CSS, HTML, JSON). Wrapp only ever sets these
    languages on a model: powershell, json, xml, plaintext (plus the diff/
    history views, which use powershell or plaintext). Everything else is dead
    weight in every install: it inflates the download and, because the MSI
    installs one component per file, it lengthens MSI costing on install.

    SAFETY RULE (enforced, not assumed): a file is only deleted when its name
    is NOT referenced from a statically-loaded module. `editor/editor.main.js`
    declares its AMD dependencies up front - anything named there is loaded
    eagerly and deleting it breaks the editor outright. Language chunks and the
    heavy workers are reached lazily (by language id, or via a worker URL
    string), so they can go. The script prints every decision.

    Run after re-vendoring Monaco (see docs/dependency-servicing.md), then
    validate all four editor surfaces: Scripts tabs, Config JSON, diff view,
    history view.

.EXAMPLE
    .\scripts\Trim-Monaco.ps1 -WhatIf
    .\scripts\Trim-Monaco.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$MonacoRoot
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $MonacoRoot) { $MonacoRoot = Join-Path $scriptDir '..\src\Wrapp.GUI\Assets\monaco\vs' }
$vs = (Resolve-Path $MonacoRoot).Path

# Languages Wrapp actually uses. 'plaintext' needs no chunk (built in).
$keepLanguages = @('powershell', 'json', 'xml')

# Language services to keep. TypeScript/CSS/HTML are never used by Wrapp;
# JSON stays for Config.json (schema-aware editing).
$keepServices = @('json')
$dropServices = @('typescript', 'css', 'html')

# editor.main.js's static dependency list -- anything named there is eager.
$editorMain = Get-Content (Join-Path $vs 'editor\editor.main.js') -Raw

function Test-StaticallyReferenced([string]$fileName) {
    $stem = [IO.Path]::GetFileNameWithoutExtension($fileName)
    return $editorMain.Contains($stem)
}

$deleted = @(); $kept = @()

# ---- 1. Unused basic-language chunks at the vs root -------------------------
# Chunk names look like "<language>-<hash>.js" (e.g. ruby-CZO8zYTz.js).
foreach ($file in Get-ChildItem $vs -File -Filter *.js) {
    if ($file.Name -notmatch '^([a-z0-9\-\+#]+)-[A-Za-z0-9_\-]{8,}\.js$') { continue }
    $lang = $Matches[1]
    # Infrastructure chunks share the shape; never touch them.
    if ($lang -in @('editor', 'index', 'main', 'workers', 'initialize', 'monaco.contribution',
                    'editorWorkerHost', 'lspLanguageFeatures', 'toggleHighContrast',
                    'cssMode', 'htmlMode', 'jsonMode', 'tsMode', 'nls.messages')) { continue }
    if ($lang -match 'worker') { continue }
    if ($lang -in $keepLanguages) { $kept += $file.Name; continue }
    if (Test-StaticallyReferenced $file.Name) { $kept += "$($file.Name) (statically referenced)"; continue }
    if ($PSCmdlet.ShouldProcess($file.FullName, 'Delete unused language chunk')) { Remove-Item $file.FullName -Force }
    $deleted += $file.Name
}

# ---- 2. Heavy language services we never instantiate ------------------------
foreach ($svc in $dropServices) {
    $dir = Join-Path $vs "language\$svc"
    if (Test-Path $dir) {
        if ($PSCmdlet.ShouldProcess($dir, 'Delete unused language service')) { Remove-Item $dir -Recurse -Force }
        $deleted += "language\$svc\*"
    }
}

# ---- 3. Their web workers (referenced only by a URL string, loaded on demand)
foreach ($worker in Get-ChildItem (Join-Path $vs 'assets') -File -Filter *.worker-*.js -ErrorAction SilentlyContinue) {
    $svc = ($worker.Name -split '\.')[0]
    if ($svc -in $keepServices -or $svc -eq 'editor') { $kept += $worker.Name; continue }
    if ($svc -notin $dropServices -and $svc -ne 'ts') { $kept += $worker.Name; continue }
    if ($PSCmdlet.ShouldProcess($worker.FullName, 'Delete unused language worker')) { Remove-Item $worker.FullName -Force }
    $deleted += "assets\$($worker.Name)"
}

$remaining = Get-ChildItem $vs -Recurse -File
"Deleted $($deleted.Count) item(s):"
$deleted | ForEach-Object { "  - $_" }
""
"Monaco tree now: $($remaining.Count) files, $([math]::Round(($remaining | Measure-Object Length -Sum).Sum/1MB,1)) MB"
"Kept languages: $($keepLanguages -join ', ') (+ plaintext, built in); services: $($keepServices -join ', ')"
