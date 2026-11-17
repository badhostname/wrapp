# Pester 5. Run from the repo root:
#   Invoke-Pester -Path tests\Wrapp.Packager.Tests -Output Detailed
# Imports the module by its manifest, as every consumer does.

BeforeAll {
    $manifest = Join-Path $PSScriptRoot '..\..\modules\Wrapp.Packager\Wrapp.Packager.psd1'
    Import-Module $manifest -Force

    $script:Root = Join-Path $env:TEMP ('WrappPester_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Root | Out-Null

    # A bundle on disk: Config.json in the Script layout (or at the root for
    # Legacy), plus the folders the resolver reports on.
    function New-TestBundle {
        param([string]$Dir, [string]$Layout = 'Script', [string]$Framework = 'Appease', [string]$IconFile = 'Icon\app.png')
        $configDir = if ($Layout -eq 'Script') { Join-Path $Dir 'Script' } else { $Dir }
        New-Item -ItemType Directory -Path $configDir -Force | Out-Null
        $binary = if ($Framework -eq 'PSADT') { 'Files' } else { 'B' }
        New-Item -ItemType Directory -Path (Join-Path $Dir "$binary\drivers\x64") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $Dir 'Shortcuts') -Force | Out-Null
        Set-Content -Path (Join-Path $Dir "$binary\drivers\x64\oemsetup.inf") -Value 'inf'
        $config = @{
            App = @{
                Company = 'Ricoh'; Name = 'Universal Print Driver PCL6'
                Version = '4_45_0_0'; DotVersion = '4.45.0.0'
                ScriptFramework = $Framework; IconFile = $IconFile
            }
        } | ConvertTo-Json -Depth 5
        Set-Content -Path (Join-Path $configDir 'Config.json') -Value $config -Encoding UTF8
        return $Dir
    }

    # The folder names deliberately do not match the app fields, so a test
    # that passes proves the fields came from Config.json.
    $script:Bundle = New-TestBundle -Dir (Join-Path $script:Root 'share\Template\Vendor\SomeApp\4_45_0_0')
}

AfterAll {
    Remove-Item -LiteralPath $script:Root -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Resolve-WrappBundle' {

    It 'is exported by the manifest' {
        (Get-Command Resolve-WrappBundle).ModuleName | Should -Be 'Wrapp.Packager'
    }

    It 'resolves the same bundle from the root, from Script\, from Config.json and from a file deep inside B\' {
        $starts = @(
            $script:Bundle
            (Join-Path $script:Bundle 'Script')
            (Join-Path $script:Bundle 'Script\Config.json')
            (Join-Path $script:Bundle 'B\drivers\x64\oemsetup.inf')
            (Join-Path $script:Bundle 'B\drivers')
        )
        foreach ($start in $starts) {
            $b = Resolve-WrappBundle -Path $start
            $b.Root        | Should -Be $script:Bundle -Because "started at $start"
            $b.ConfigPath  | Should -Be (Join-Path $script:Bundle 'Script\Config.json')
            $b.ScriptDir   | Should -Be (Join-Path $script:Bundle 'Script')
            $b.BinaryDir   | Should -Be (Join-Path $script:Bundle 'B')
            $b.ShortcutDir | Should -Be (Join-Path $script:Bundle 'Shortcuts')
            $b.IconDir     | Should -Be (Join-Path $script:Bundle 'Icon')
            $b.Layout      | Should -Be 'Script'
            $b.Framework   | Should -Be 'Appease'
        }
    }

    It 'takes the app fields from Config.json, never from the folder names' {
        $b = Resolve-WrappBundle -Path $script:Bundle
        $b.App.Company    | Should -Be 'Ricoh'
        $b.App.Name       | Should -Be 'Universal Print Driver PCL6'
        $b.App.Version    | Should -Be '4_45_0_0'
        $b.App.DotVersion | Should -Be '4.45.0.0'
    }

    It 'accepts the path on the pipeline' {
        ($script:Bundle | Resolve-WrappBundle).Root | Should -Be $script:Bundle
    }

    It 'recognises the legacy layout with Config.json at the root' {
        $legacy = New-TestBundle -Dir (Join-Path $script:Root 'legacy\App\1_0_0') -Layout Legacy
        $b = Resolve-WrappBundle -Path (Join-Path $legacy 'B\drivers\x64\oemsetup.inf')
        $b.Root       | Should -Be $legacy
        $b.ConfigPath | Should -Be (Join-Path $legacy 'Config.json')
        $b.ScriptDir  | Should -Be $legacy
        $b.Layout     | Should -Be 'Legacy'
    }

    It 'reports Files as the binary folder of a PSADT bundle' {
        $psadt = New-TestBundle -Dir (Join-Path $script:Root 'psadt\App\2_0_0') -Framework PSADT
        $b = Resolve-WrappBundle -Path $psadt
        $b.Framework | Should -Be 'PSADT'
        $b.BinaryDir | Should -Be (Join-Path $psadt 'Files')
    }

    It 'points IconDir at the folder App.IconFile names, and ignores a value that leaves the bundle' {
        $custom = New-TestBundle -Dir (Join-Path $script:Root 'icons\App\1_0_0') -IconFile 'Images\app.png'
        (Resolve-WrappBundle -Path $custom).IconDir | Should -Be (Join-Path $custom 'Images')

        $escape = New-TestBundle -Dir (Join-Path $script:Root 'icons\Bad\1_0_0') -IconFile '..\..\elsewhere\app.png'
        (Resolve-WrappBundle -Path $escape).IconDir | Should -Be (Join-Path $escape 'Icon')
    }

    It 'throws, naming the start path, when nothing above is a bundle' {
        $plain = Join-Path $script:Root 'plain\folder\deeper'
        New-Item -ItemType Directory -Path $plain -Force | Out-Null
        { Resolve-WrappBundle -Path $plain } | Should -Throw "*$plain*not inside a Wrapp bundle*"
    }

    It 'throws when the path does not exist' {
        { Resolve-WrappBundle -Path (Join-Path $script:Root 'missing') } | Should -Throw '*does not exist*'
    }

    It 'stops at the drive root without error other than not found' {
        { Resolve-WrappBundle -Path ([System.IO.Path]::GetPathRoot($script:Root)) } | Should -Throw '*not inside a Wrapp bundle*'
    }

    It 'gives up beyond MaxDepth parents' {
        $deep = Join-Path $script:Bundle 'B\a\b\c\d\e\f\g\h\i'
        New-Item -ItemType Directory -Path $deep -Force | Out-Null
        (Resolve-WrappBundle -Path $deep -MaxDepth 12).Root | Should -Be $script:Bundle
        { Resolve-WrappBundle -Path $deep -MaxDepth 3 } | Should -Throw '*not inside a Wrapp bundle*'
    }

    It 'returns the nearest bundle when one sits inside another, and warns' {
        $outer = New-TestBundle -Dir (Join-Path $script:Root 'nested\Outer\1_0_0')
        $inner = New-TestBundle -Dir (Join-Path $outer 'B\Inner\2_0_0')
        $b = Resolve-WrappBundle -Path (Join-Path $inner 'Script') -WarningVariable warnings -WarningAction SilentlyContinue
        $b.Root | Should -Be $inner
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*inside another bundle*'

        # From the outer bundle's own files there is no ambiguity.
        (Resolve-WrappBundle -Path (Join-Path $outer 'Shortcuts')).Root | Should -Be $outer
    }
}
